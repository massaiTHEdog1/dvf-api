namespace DvfApi.Models;

/// <summary>Request body of POST /api/mutations.</summary>
public sealed record MutationsQueryRequest
{
    /// <summary>
    /// Optional list of filters, combined with AND. Absent or empty = no filter.
    /// </summary>
    public List<MutationFilter>? Filters { get; init; }

    /// <summary>Mandatory. Number of rows to skip before the first returned row (0 or more).</summary>
    public int? Skip { get; init; }

    /// <summary>Mandatory. Number of rows to return (positive integer, at most 1000).</summary>
    public int? Take { get; init; }
}

/// <summary>A single filter: one column, one operator, one value.</summary>
public sealed record MutationFilter
{
    /// <summary>
    /// Name of a "mutations" table column, e.g. "Code postal" or "Valeur fonciere"
    /// (the exact column names, including spaces and without accents).
    /// </summary>
    public string? Field { get; init; }

    /// <summary>
    /// Comparison operator, matched case-insensitively. Allowed values:
    /// EQUALS, NOT_EQUALS, LIKE, GREATER_THAN, GREATER_THAN_OR_EQUALS, LOWER_THAN,
    /// LOWER_THAN_OR_EQUALS. Only the operators valid for the column type are accepted:
    /// numeric columns (INTEGER/REAL) take the comparison and range operators;
    /// text columns take EQUALS, NOT_EQUALS, LIKE; the date column ("Date mutation") takes
    /// all operators (range operators compare chronologically).
    /// </summary>
    public string? Operator { get; init; }

    /// <summary>
    /// Value to compare against. Integer (or numeric string) for INTEGER columns,
    /// number (or numeric string) for REAL columns, string for TEXT columns,
    /// date string ("DD/MM/YYYY" or "YYYY-MM-DD") for the date column.
    /// With LIKE, the value is a SQL pattern: '%' and '_' wildcards are supported.
    /// </summary>
    public object? Operand { get; init; }
}
