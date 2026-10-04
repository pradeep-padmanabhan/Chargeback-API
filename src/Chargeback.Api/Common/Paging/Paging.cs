using Chargeback.Api.Common.Results;
using Chargeback.SharedKernel.Paging;
using Chargeback.SharedKernel.Results;

namespace Chargeback.Api.Common.Paging;

/// <summary>
/// A paged list query (approved contract, common guide §3.4). <c>ValidationBehavior</c> checks page, pageSize and the
/// requested sort (against <see cref="Sort"/>) before authorization and the handler. Every request carrying a
/// <see cref="PageRequest"/> must implement this.
/// </summary>
public interface IPagedRequest
{
    PageRequest Page { get; }

    SortMap Sort { get; }
}

public static class PagingValidation
{
    /// <returns>
    /// null when valid; 400 INVALID_SORT_FIELD for an unsupported sortBy; otherwise 400 VALIDATION_FAILED listing every
    /// out-of-range page / pageSize and an unsupported sortDirection.
    /// </returns>
    public static Error? Validate(PageRequest page, SortMap sort)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(sort);
        if (page.SortBy is { } field && !sort.Fields.Contains(field, StringComparer.Ordinal))
        {
            return Errors.InvalidSortField(field, sort.Fields);
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (page.Page < 1)
        {
            errors["page"] = ["page must be 1 or greater."];
        }

        if (page.PageSize is < 1 or > PageRequest.MaxPageSize)
        {
            errors["pageSize"] = [$"pageSize must be between 1 and {PageRequest.MaxPageSize}."];
        }

        if (sort.Validate(page) is { ValidationErrors: { } sortErrors })
        {
            foreach (var (key, messages) in sortErrors)
            {
                errors[key] = messages;
            }
        }

        return errors.Count == 0 ? null : Error.Validation(errors);
    }
}

/// <summary>
/// The fields a list may be sorted by, mapped to trusted SQL columns. Client input only ever selects a key; it is
/// never interpolated into SQL. Default: <c>createdAt</c> descending, unless the list declares another default with
/// <see cref="WithDefaultSort"/> (e.g. <c>/admin/banks</c>: <c>bankName asc</c>). A request that names <c>sortBy</c> but no
/// direction is descending. Every sort ends with a unique tie-breaker so paging is stable.
/// </summary>
public sealed class SortMap
{
    public const string DefaultField = "createdAt";
    public const string Ascending = "asc";
    public const string Descending = "desc";

    private readonly Dictionary<string, string> _columns;
    private readonly string _tieBreaker;

    public SortMap(string tieBreakerColumn, params (string Field, string Column)[] columns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tieBreakerColumn);
        ArgumentNullException.ThrowIfNull(columns);
        _tieBreaker = tieBreakerColumn;
        _columns = columns.ToDictionary(c => c.Field, c => c.Column, StringComparer.Ordinal);
        if (!_columns.ContainsKey(DefaultField))
        {
            throw new ArgumentException($"A sort map must support the default field '{DefaultField}'.", nameof(columns));
        }

        Fields = [.. _columns.Keys];
    }

    /// <summary>Applied when the request sends neither <c>sortBy</c> nor <c>sortDirection</c>.</summary>
    public string DefaultSortField { get; private set; } = DefaultField;

    public string DefaultSortDirection { get; private set; } = Descending;

    /// <summary>Declares a list-specific default sort (a documented exception to the contract default).</summary>
    public SortMap WithDefaultSort(string field, string direction)
    {
        if (!_columns.ContainsKey(field) || !IsDirection(direction))
        {
            throw new ArgumentException($"Unsupported default sort '{field} {direction}'.", nameof(field));
        }

        DefaultSortField = field;
        DefaultSortDirection = direction.ToLowerInvariant();
        return this;
    }

    /// <summary>Supported <c>sortBy</c> values (camelCase, case-sensitive), in declaration order.</summary>
    public IReadOnlyList<string> Fields { get; }

    /// <returns>null when the sort is supported; otherwise 400 INVALID_SORT_FIELD or a sortDirection validation error.</returns>
    public Error? Validate(PageRequest page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.SortBy is { } field && !_columns.ContainsKey(field))
        {
            return Errors.InvalidSortField(field, Fields);
        }

        if (page.SortDirection is { } direction && !IsDirection(direction))
        {
            return Error.Validation(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["sortDirection"] = [$"sortDirection must be '{Ascending}' or '{Descending}'."],
            });
        }

        return null;
    }

    /// <summary>The ORDER BY clause for a validated request.</summary>
    public string OrderBy(PageRequest page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (Validate(page) is { } error)
        {
            throw new InvalidOperationException($"Unvalidated sort reached the handler: {error.Code}.");
        }

        var useDefault = page.SortBy is null && page.SortDirection is null;
        var column = _columns[page.SortBy ?? DefaultSortField];
        var requested = useDefault ? DefaultSortDirection : page.SortDirection;
        var direction = string.Equals(requested, Ascending, StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
        return $"ORDER BY {column} {direction} NULLS LAST, {_tieBreaker} {direction}";
    }

    private static bool IsDirection(string value) =>
        string.Equals(value, Ascending, StringComparison.OrdinalIgnoreCase) || string.Equals(value, Descending, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Endpoint metadata: the list's sortable fields, published as the <c>sortBy</c> enum in OpenAPI.</summary>
public sealed record SortFieldsMetadata(IReadOnlyList<string> Fields, string DefaultField, string DefaultDirection);

public static class SortingEndpointExtensions
{
    public static RouteHandlerBuilder WithSortFields(this RouteHandlerBuilder builder, SortMap sort)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(sort);
        return builder.WithMetadata(new SortFieldsMetadata(sort.Fields, sort.DefaultSortField, sort.DefaultSortDirection));
    }
}
