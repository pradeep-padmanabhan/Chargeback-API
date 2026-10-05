namespace Chargeback.SharedKernel.Paging;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

/// <summary>
/// 1-based page request holding the caller's values unchanged (null page = 1, null pageSize = 20). Out-of-range values are
/// rejected with 400 by the API pipeline, never clamped. <c>SortBy</c> / <c>SortDirection</c> are the raw client values;
/// each list validates them against its own supported fields (null = createdAt desc).
/// </summary>
public sealed record PageRequest
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public PageRequest(int? page = null, int? pageSize = null, string? sortBy = null, string? sortDirection = null)
    {
        Page = page ?? 1;
        PageSize = pageSize ?? DefaultPageSize;
        SortBy = string.IsNullOrWhiteSpace(sortBy) ? null : sortBy.Trim();
        SortDirection = string.IsNullOrWhiteSpace(sortDirection) ? null : sortDirection.Trim();
    }

    public int Page { get; }

    public int PageSize { get; }

    public string? SortBy { get; }

    public string? SortDirection { get; }

    /// <summary>page ≥ 1 and 1 ≤ pageSize ≤ <see cref="MaxPageSize"/>.</summary>
    public bool IsWithinBounds => Page >= 1 && PageSize is >= 1 and <= MaxPageSize;

    public long Offset => ((long)Page - 1) * PageSize;
}
