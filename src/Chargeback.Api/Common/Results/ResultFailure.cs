using System.Reflection;
using Chargeback.SharedKernel.Results;

namespace Chargeback.Api.Common.Results;

/// <summary>
/// Creates a failed <typeparamref name="TResponse"/> from an <see cref="Error"/> so pipeline behaviors
/// can short-circuit generically. Every request must return <see cref="Result"/> or <see cref="Result{T}"/>
/// (enforced by architecture tests).
/// </summary>
public static class ResultFailure<TResponse>
{
    private static readonly Func<Error, TResponse> Factory = BuildFactory();

    public static TResponse Create(Error error) => Factory(error);

    private static Func<Error, TResponse> BuildFactory()
    {
        var type = typeof(TResponse);
        if (type == typeof(Result))
        {
            return error => (TResponse)(object)Result.Failure(error);
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Result<>))
        {
            var method = type.GetMethod(
                nameof(Result.Failure),
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly,
                [typeof(Error)])!;
            var factory = method.CreateDelegate<Func<Error, TResponse>>();
            return factory;
        }

        return _ => throw new InvalidOperationException(
            $"{type} is not a Result type. All MediatR requests must return Result or Result<T>.");
    }
}
