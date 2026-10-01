namespace Chargeback.SharedKernel.Paging;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

/// <summary>1-based page request; values outside the allowed range are clamped.</summary>
public sealed record PageRequest
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    public PageRequest(int? page = null, int? pageSize = null)
    {
        Page = Math.Max(1, page ?? 1);
        PageSize = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
    }

    public int Page { get; }

    public int PageSize { get; }

    public int Offset => (Page - 1) * PageSize;
}
