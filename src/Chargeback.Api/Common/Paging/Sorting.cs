using Chargeback.Api.Common.Results;
using Chargeback.SharedKernel.Paging;
using Chargeback.SharedKernel.Results;

namespace Chargeback.Api.Common.Paging;

/// <summary>
/// A list query that supports <c>sortBy</c> / <c>sortDirection</c> (approved contract, common guide §3.4).
/// <c>ValidationBehavior</c> checks the requested sort against <see cref="Sort"/> before authorization and the handler.
/// </summary>
public interface ISortableRequest
{
    PageRequest Page { get; }

    SortMap Sort { get; }
}

/// <summary>
/// The fields a list may be sorted by, mapped to trusted SQL columns. Client input only ever selects a key; it is
/// never interpolated into SQL. Default: <c>createdAt</c> descending. Every sort ends with a unique tie-breaker so
/// paging is stable.
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

        var column = _columns[page.SortBy ?? DefaultField];
        var direction = string.Equals(page.SortDirection, Ascending, StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
        return $"ORDER BY {column} {direction} NULLS LAST, {_tieBreaker} {direction}";
    }

    private static bool IsDirection(string value) =>
        string.Equals(value, Ascending, StringComparison.OrdinalIgnoreCase) || string.Equals(value, Descending, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Endpoint metadata: the list's sortable fields, published as the <c>sortBy</c> enum in OpenAPI.</summary>
public sealed record SortFieldsMetadata(IReadOnlyList<string> Fields);

public static class SortingEndpointExtensions
{
    public static RouteHandlerBuilder WithSortFields(this RouteHandlerBuilder builder, SortMap sort)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(sort);
        return builder.WithMetadata(new SortFieldsMetadata(sort.Fields));
    }
}
