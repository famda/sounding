namespace Sounding.Dashboard.Application.Common;

public sealed record PaginatedList<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => (TotalCount + PageSize - 1) / PageSize;
    public bool HasNextPage => Page < TotalPages;

    // The offset is computed wide: page × pageSize overflows an int for large pages, and a negative Skip reads as 0.
    public static PaginatedList<T> From(IReadOnlyList<T> all, PageRequest page)
    {
        var offset = (long)(page.Page - 1) * page.PageSize;
        IReadOnlyList<T> items = offset >= all.Count ? [] : [.. all.Skip((int)offset).Take(page.PageSize)];
        return new(items, page.Page, page.PageSize, all.Count);
    }
}

public readonly record struct PageRequest(int Page, int PageSize)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
}
