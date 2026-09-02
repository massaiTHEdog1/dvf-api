using DvfApi.Models;

namespace DvfApi.Database;

/// <summary>
/// Schema of the "mutations" table: the 43 DVF columns (one per field of the source files) and
/// their declared SQLite types, plus the set of filter operators allowed for each type.
/// Shared by the importer (table creation) and the query builder (operator validation).
/// </summary>
public static class MutationsSchema
{
    // Column names of the "mutations" table: one column per field of the DVF files (43 fields,
    // identical in the 2021-2025 exports).
    public static readonly string[] Columns =
    {
        "Identifiant de document",
        "Reference document",
        "1 Articles CGI",
        "2 Articles CGI",
        "3 Articles CGI",
        "4 Articles CGI",
        "5 Articles CGI",
        "No disposition",
        "Date mutation",
        "Nature mutation",
        "Valeur fonciere",
        "No voie",
        "B/T/Q",
        "Type de voie",
        "Code voie",
        "Voie",
        "Code postal",
        "Commune",
        "Code departement",
        "Code commune",
        "Prefixe de section",
        "Section",
        "No plan",
        "No Volume",
        "1er lot",
        "Surface Carrez du 1er lot",
        "2eme lot",
        "Surface Carrez du 2eme lot",
        "3eme lot",
        "Surface Carrez du 3eme lot",
        "4eme lot",
        "Surface Carrez du 4eme lot",
        "5eme lot",
        "Surface Carrez du 5eme lot",
        "Nombre de lots",
        "Code type local",
        "Type local",
        "Identifiant local",
        "Surface reelle bati",
        "Nombre pieces principales",
        "Nature culture",
        "Nature culture speciale",
        "Surface terrain"
    };

    // Declared column types, detected from the 2021-2025 files (900k sampled rows per column,
    // all matching):
    // - INTEGER for fields that are purely numeric (codes keep their leading zeros in the
    //   source, but they are stored as numbers so that range filters work).
    // - REAL for amounts and surfaces, stored in French format ("468000,00") and converted to a
    //   double with '.' as decimal separator.
    // - TEXT for everything else (dates, names, free-text codes like "B078" or "2A").
    public static readonly string[] ColumnTypes =
    {
        "TEXT",    // Identifiant de document
        "TEXT",    // Reference document
        "TEXT",    // 1 Articles CGI
        "TEXT",    // 2 Articles CGI
        "TEXT",    // 3 Articles CGI
        "TEXT",    // 4 Articles CGI
        "TEXT",    // 5 Articles CGI
        "INTEGER", // No disposition
        "TEXT",    // Date mutation
        "TEXT",    // Nature mutation
        "REAL",    // Valeur fonciere
        "INTEGER", // No voie
        "TEXT",    // B/T/Q
        "TEXT",    // Type de voie
        "TEXT",    // Code voie
        "TEXT",    // Voie
        "INTEGER", // Code postal
        "TEXT",    // Commune
        "TEXT",    // Code departement
        "INTEGER", // Code commune
        "TEXT",    // Prefixe de section
        "TEXT",    // Section
        "INTEGER", // No plan
        "TEXT",    // No Volume
        "TEXT",    // 1er lot
        "REAL",    // Surface Carrez du 1er lot
        "TEXT",    // 2eme lot
        "REAL",    // Surface Carrez du 2eme lot
        "TEXT",    // 3eme lot
        "REAL",    // Surface Carrez du 3eme lot
        "TEXT",    // 4eme lot
        "REAL",    // Surface Carrez du 4eme lot
        "TEXT",    // 5eme lot
        "REAL",    // Surface Carrez du 5eme lot
        "INTEGER", // Nombre de lots
        "TEXT",    // Code type local
        "TEXT",    // Type local
        "TEXT",    // Identifiant local
        "INTEGER", // Surface reelle bati
        "INTEGER", // Nombre pieces principales
        "TEXT",    // Nature culture
        "TEXT",    // Nature culture speciale
        "INTEGER", // Surface terrain
    };

    /// <summary>
    /// The date column of the table. It is stored as TEXT "DD/MM/YYYY" (source format), which
    /// does NOT sort chronologically as a string ("31/01/2021" > "05/02/2021"), so range
    /// filters are evaluated against <see cref="SortableDateColumn"/> instead.
    /// </summary>
    public const string DateColumn = "Date mutation";

    /// <summary>
    /// Derived column holding the same date as sortable TEXT "YYYY-MM-DD" (ISO 8601, sorts
    /// chronologically). Maintained by the importer (filled at import time and backfilled on
    /// existing databases). Not exposed in API responses.
    /// </summary>
    public const string SortableDateColumn = "Date mutation ISO";

    /// <summary>True if the given column holds dates (stored in "DD/MM/YYYY" source format).</summary>
    public static bool IsDateColumn(string columnName) => columnName == DateColumn;

    /// <summary>
    /// All table column definitions for CREATE TABLE: the 43 source columns plus the derived
    /// sortable date column.
    /// </summary>
    public static string TableColumnDefinitions() => string.Join(",\n",
            Columns.Zip(ColumnTypes, (name, type) => $"\"{name}\" {type}"))
        + $",\n\"{SortableDateColumn}\" TEXT";

    /// <summary>Returns the declared type of a column, or false if the name is not a known column.</summary>
    public static bool TryGetColumnType(string columnName, out string columnType)
    {
        for (var i = 0; i < Columns.Length; i++)
        {
            if (Columns[i] == columnName)
            {
                columnType = ColumnTypes[i];
                return true;
            }
        }

        columnType = string.Empty;
        return false;
    }

    /// <summary>
    /// Operators allowed on the given column: numeric columns accept comparisons and range
    /// operators, text columns accept equality and LIKE only, date columns accept all operators
    /// (range operators are evaluated against the sortable ISO column).
    /// </summary>
    public static IReadOnlySet<FilterOperator> AllowedOperators(string columnName)
    {
        if (IsDateColumn(columnName))
        {
            return DateOperators;
        }

        return TryGetColumnType(columnName, out var columnType)
            ? AllowedOperatorsForType(columnType)
            : TextOperators;
    }

    /// <summary>
    /// Operators allowed on a column of the given declared type: numeric columns accept
    /// comparisons and range operators, text columns accept equality and LIKE only.
    /// </summary>
    public static IReadOnlySet<FilterOperator> AllowedOperatorsForType(string columnType) => columnType switch
    {
        "INTEGER" or "REAL" => NumericOperators,
        _ => TextOperators
    };

    private static readonly HashSet<FilterOperator> DateOperators = new()
    {
        FilterOperator.EQUALS,
        FilterOperator.NOT_EQUALS,
        FilterOperator.LIKE,
        FilterOperator.GREATER_THAN,
        FilterOperator.GREATER_THAN_OR_EQUALS,
        FilterOperator.LOWER_THAN,
        FilterOperator.LOWER_THAN_OR_EQUALS
    };

    private static readonly HashSet<FilterOperator> NumericOperators = new()
    {
        FilterOperator.EQUALS,
        FilterOperator.NOT_EQUALS,
        FilterOperator.GREATER_THAN,
        FilterOperator.GREATER_THAN_OR_EQUALS,
        FilterOperator.LOWER_THAN,
        FilterOperator.LOWER_THAN_OR_EQUALS
    };

    private static readonly HashSet<FilterOperator> TextOperators = new()
    {
        FilterOperator.EQUALS,
        FilterOperator.NOT_EQUALS,
        FilterOperator.LIKE
    };
}
