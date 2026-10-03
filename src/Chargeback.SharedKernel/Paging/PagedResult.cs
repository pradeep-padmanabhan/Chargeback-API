namespace Chargeback.SharedKernel.Paging;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

/// <summary>
/// 1-based page request; page and pageSize outside the allowed range are clamped. <c>SortBy</c> / <c>SortDirection</c>
/// are the raw client values; each list validates them against its own supported fields (null = createdAt desc).
/// </summary>
public sealed record PageRequest
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public PageRequest(int? page = null, int? pageSize = null, string? sortBy = null, string? sortDirection = null)
    {
        Page = Math.Max(1, page ?? 1);
        PageSize = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        SortBy = string.IsNullOrWhiteSpace(sortBy) ? null : sortBy.Trim();
        SortDirection = string.IsNullOrWhiteSpace(sortDirection) ? null : sortDirection.Trim();
    }

    public int Page { get; }

    public int PageSize { get; }

    public string? SortBy { get; }

    public string? SortDirection { get; }

    public int Offset => (Page - 1) * PageSize;
}
