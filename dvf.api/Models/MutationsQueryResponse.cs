namespace DvfApi.Models;

/// <summary>
/// Response body of POST /api/mutations: the rows of the requested page plus standard
/// pagination metadata computed for the current filters.
/// </summary>
public sealed record MutationsQueryResponse
{
    /// <summary>
    /// The rows of the requested page: one object per row, one entry per column
    /// (JSON null when the source field is empty).
    /// </summary>
    public List<Dictionary<string, object?>> Rows { get; init; } = [];

    /// <summary>Pagination metadata for the current filters.</summary>
    public PaginationInfo Pagination { get; init; } = new();
}

/// <summary>Standard pagination metadata accompanying the rows of POST /api/mutations.</summary>
public sealed record PaginationInfo
{
    /// <summary>Total number of rows matching the current filters (before pagination).</summary>
    public long TotalRows { get; init; }

    /// <summary>Current page, 1-based (derived from the requested "skip" and "take").</summary>
    public int Page { get; init; }

    /// <summary>Number of rows per page (the requested "take").</summary>
    public int PageSize { get; init; }

    /// <summary>Total number of pages for the current filters: ceil(totalRows / pageSize).</summary>
    public long TotalPages { get; init; }
}
