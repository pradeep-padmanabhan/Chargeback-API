using System.Text.Json;
using Chargeback.Api.Common.Paging;
using Chargeback.Api.Common.Results;
using Chargeback.SharedKernel.Results;
using FluentValidation;
using MediatR;

namespace Chargeback.Api.Common.Behaviors;

/// <summary>
/// Pipeline step 2. Request-shape validation (FluentValidation 11, plus list paging and sort) only: validators must not query
/// the database, so running before authorization discloses nothing. The ten chargeback gates are
/// business validations inside the Intake slice, not validators.
/// </summary>
public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IValidator<TRequest>[] _validators = validators.ToArray();

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        // Paging is request shape too (common guide §3.4): out-of-range page/pageSize → 400 VALIDATION_FAILED,
        // unsupported sortBy → 400 INVALID_SORT_FIELD.
        if (request is IPagedRequest paged && PagingValidation.Validate(paged.Page, paged.Sort) is { } pagingError)
        {
            return ResultFailure<TResponse>.Create(pagingError);
        }

        if (_validators.Length == 0)
        {
            return await next(cancellationToken);
        }

        var context = new ValidationContext<TRequest>(request);
        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var validator in _validators)
        {
            var result = await validator.ValidateAsync(context, cancellationToken);
            failures.AddRange(result.Errors);
        }

        if (failures.Count == 0)
        {
            return await next(cancellationToken);
        }

        var errors = failures
            .GroupBy(f => ToCamelCasePath(f.PropertyName), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).Distinct().ToArray(), StringComparer.Ordinal);

        return ResultFailure<TResponse>.Create(Error.Validation(errors));
    }

    private static string ToCamelCasePath(string propertyPath) =>
        string.Join('.', propertyPath.Split('.').Select(JsonNamingPolicy.CamelCase.ConvertName));
}
