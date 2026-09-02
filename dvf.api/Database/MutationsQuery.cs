using DvfApi.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace DvfApi.Database;

/// <summary>Invalid /api/mutations request; mapped to HTTP 400 by the endpoint.</summary>
public sealed class MutationsQueryException : Exception
{
    public MutationsQueryException(string message) : base(message)
    {
    }
}

/// <summary>
/// Builds and executes the SQL query behind POST /api/mutations.
/// Only safe parts are ever written into the SQL text: column names come from the fixed
/// whitelist in <see cref="MutationsSchema"/>, operators from a fixed set, the table name is
/// constant. Filter operands and the pagination values are always bound as parameters.
///
/// Date filters: the "Date mutation" column is stored in the source "DD/MM/YYYY" format, which
/// does not sort chronologically as a string. Range operators on it are therefore evaluated
/// against the derived sortable column "Date mutation ISO" ("YYYY-MM-DD"), and date operands
/// are accepted in either "DD/MM/YYYY" or "YYYY-MM-DD" format (normalized per target column).
///
/// The response is a standard pagination envelope: the rows of the requested page plus the
/// total row count for the current filters (COUNT(*) on the same WHERE clause), the current
/// page (skip / take + 1), the page size (take) and the total number of pages.
/// </summary>
public static class MutationsQuery
{
    private const string Table = "mutations";

    /// <summary>Upper bound for "take": a page of at most 1000 rows.</summary>
    public const int MaxTake = 1000;

    private static readonly string SelectColumns =
        string.Join(", ", MutationsSchema.Columns.Select(c => $"\"{c}\""));

    public static async Task<MutationsQueryResponse> QueryAsync(string databasePath, MutationsQueryRequest request)
    {
        ValidatePagination(request);
        var skip = request.Skip!.Value;
        var take = request.Take!.Value;

        var (whereSql, parameters) = BuildWhereClause(request);

        using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();

        // Total number of rows matching the current filters: same WHERE clause as the data
        // query, without LIMIT/OFFSET.
        var totalRows = 0L;
        using (var countCommand = new SqliteCommand($"SELECT COUNT(*) FROM {Table}{whereSql}", connection))
        {
            AddParameters(countCommand, parameters);
            // COUNT(*) never returns NULL, but guard defensively.
            totalRows = Convert.ToInt64(await countCommand.ExecuteScalarAsync()!, CultureInfo.InvariantCulture);
        }

        var sql = $"SELECT {SelectColumns} FROM {Table}{whereSql} LIMIT @take OFFSET @skip";
        var rows = new List<Dictionary<string, object?>>();
        using (var command = new SqliteCommand(sql, connection))
        {
            AddParameters(command, parameters);
            command.Parameters.Add(new SqliteParameter("@take", take));
            command.Parameters.Add(new SqliteParameter("@skip", skip));

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>(reader.FieldCount);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                rows.Add(row);
            }
        }

        return new MutationsQueryResponse
        {
            Rows = rows,
            Pagination = new PaginationInfo
            {
                TotalRows = totalRows,
                Page = skip / take + 1,
                PageSize = take,
                TotalPages = (totalRows + take - 1) / take
            }
        };
    }

    /// <summary>
    /// Validates the request filters and builds the SQL WHERE clause: only safe parts go into
    /// the SQL text (whitelisted column names, fixed operators), operands are always bound as
    /// parameters. Returns the clause ("" when there is no filter) and its parameters, shared
    /// by the COUNT(*) and the data query.
    /// </summary>
    private static (string WhereSql, List<SqliteParameter> Parameters) BuildWhereClause(MutationsQueryRequest request)
    {
        var parameters = new List<SqliteParameter>();
        var conditions = new List<string>();

        foreach (var filter in request.Filters ?? (IEnumerable<MutationFilter>)Array.Empty<MutationFilter>())
        {
            if (string.IsNullOrWhiteSpace(filter.Field))
            {
                throw new MutationsQueryException("Each filter requires a 'field'.");
            }

            if (!MutationsSchema.TryGetColumnType(filter.Field, out var columnType))
            {
                throw new MutationsQueryException($"Unknown field '{filter.Field}'.");
            }

            if (!TryParseOperator(filter.Operator, out var @operator))
            {
                throw new MutationsQueryException(
                    $"Invalid operator '{filter.Operator}'. Allowed values: {AllowedOperatorValues}.");
            }

            if (!MutationsSchema.AllowedOperators(filter.Field).Contains(@operator))
            {
                throw new MutationsQueryException(
                    $"Operator '{filter.Operator}' is not allowed on field '{filter.Field}' (declared type {columnType}).");
            }

            // Range operators on the date column are evaluated against the derived sortable
            // column ("YYYY-MM-DD"); equality / NOT_EQUALS stay on the stored column and are
            // normalized to the stored "DD/MM/YYYY" format. LIKE passes the pattern through as
            // written (it applies to the stored "DD/MM/YYYY" values).
            var isDateRange = MutationsSchema.IsDateColumn(filter.Field)
                && @operator is FilterOperator.GREATER_THAN or FilterOperator.GREATER_THAN_OR_EQUALS
                    or FilterOperator.LOWER_THAN or FilterOperator.LOWER_THAN_OR_EQUALS;
            var isDateComparison = MutationsSchema.IsDateColumn(filter.Field)
                && @operator is FilterOperator.EQUALS or FilterOperator.NOT_EQUALS;

            var sqlColumn = isDateRange ? MutationsSchema.SortableDateColumn : filter.Field;
            var value = isDateRange
                ? ConvertDateOperand(filter.Operand, filter.Field, sortable: true)
                : isDateComparison
                    ? ConvertDateOperand(filter.Operand, filter.Field, sortable: false)
                    : ConvertOperand(filter.Operand, columnType, filter.Field);

            var parameter = new SqliteParameter { ParameterName = $"@p{parameters.Count}", Value = value };
            parameters.Add(parameter);
            conditions.Add($"\"{sqlColumn}\" {ToSqlOperator(@operator)} {parameter.ParameterName}");
        }

        return conditions.Count > 0
            ? (" WHERE " + string.Join(" AND ", conditions), parameters)
            : (string.Empty, parameters);
    }

    /// <summary>
    /// Copies the filter parameters onto the given command as fresh instances (a
    /// SqliteParameter can only belong to one command's parameter collection).
    /// </summary>
    private static void AddParameters(SqliteCommand command, IReadOnlyList<SqliteParameter> parameters)
    {
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new SqliteParameter { ParameterName = parameter.ParameterName, Value = parameter.Value });
        }
    }

    /// <summary>
    /// Pagination is mandatory: "skip" must be a non-negative integer, "take" a positive
    /// integer, and no more than <see cref="MaxTake"/> rows are returned per request.
    /// </summary>
    private static void ValidatePagination(MutationsQueryRequest request)
    {
        if (request.Skip is not { } skip || skip < 0)
        {
            throw new MutationsQueryException("Pagination is mandatory: 'skip' must be a non-negative integer.");
        }

        if (request.Take is not { } take || take <= 0)
        {
            throw new MutationsQueryException("Pagination is mandatory: 'take' must be a positive integer.");
        }

        if (take > MaxTake)
        {
            throw new MutationsQueryException($"'take' cannot exceed {MaxTake}.");
        }
    }

    private const string AllowedOperatorValues =
        "EQUALS, NOT_EQUALS, LIKE, GREATER_THAN, GREATER_THAN_OR_EQUALS, LOWER_THAN, LOWER_THAN_OR_EQUALS";

    /// <summary>Parses the operator string case-insensitively.</summary>
    private static bool TryParseOperator(string? value, out FilterOperator @operator)
    {
        @operator = default;
        return !string.IsNullOrWhiteSpace(value)
            && Enum.TryParse<FilterOperator>(value, ignoreCase: true, out @operator);
    }

    private static string ToSqlOperator(FilterOperator @operator) => @operator switch
    {
        FilterOperator.EQUALS => "=",
        FilterOperator.NOT_EQUALS => "!=",
        FilterOperator.LIKE => "LIKE",
        FilterOperator.GREATER_THAN => ">",
        FilterOperator.GREATER_THAN_OR_EQUALS => ">=",
        FilterOperator.LOWER_THAN => "<",
        FilterOperator.LOWER_THAN_OR_EQUALS => "<=",
        _ => throw new MutationsQueryException($"Unsupported operator '{@operator}'.")
    };

    private static readonly string[] DateOperandFormats =
    {
        "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "yyyy-M-d"
    };

    /// <summary>
    /// Converts a date operand ("DD/MM/YYYY" or "YYYY-MM-DD", with or without zero padding)
    /// to the form of the target column: sortable "YYYY-MM-DD" for the derived ISO column,
    /// stored "DD/MM/YYYY" for the original column.
    /// </summary>
    private static object ConvertDateOperand(object? operand, string fieldName, bool sortable)
    {
        if (operand is not JsonElement element || element.ValueKind == JsonValueKind.Null)
        {
            throw new MutationsQueryException($"Filter on '{fieldName}' requires an 'operand'.");
        }

        if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } text)
        {
            throw new MutationsQueryException(
                $"Operand for field '{fieldName}' (date) must be a string in 'DD/MM/YYYY' or 'YYYY-MM-DD' format.");
        }

        if (!DateTime.TryParseExact(text, DateOperandFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new MutationsQueryException(
                $"Operand for field '{fieldName}' (date) must be a date in 'DD/MM/YYYY' or 'YYYY-MM-DD' format (got '{text}').");
        }

        return sortable
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Converts the JSON operand to a CLR value matching the declared type of the column:
    /// INTEGER columns accept an integer (or a numeric string), REAL columns a number (or a
    /// numeric string), TEXT columns a string (or a bare number, kept as written in the JSON).
    /// </summary>
    private static object ConvertOperand(object? operand, string columnType, string fieldName)
    {
        if (operand is not JsonElement element || element.ValueKind == JsonValueKind.Null)
        {
            throw new MutationsQueryException($"Filter on '{fieldName}' requires an 'operand'.");
        }

        switch (columnType)
        {
            case "INTEGER":
                if (element.ValueKind == JsonValueKind.Number)
                {
                    if (element.TryGetInt64(out var integer))
                    {
                        return integer;
                    }
                    throw new MutationsQueryException($"Operand for '{fieldName}' is out of the integer range.");
                }
                if (element.ValueKind == JsonValueKind.String
                    && long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInteger))
                {
                    return parsedInteger;
                }
                throw new MutationsQueryException($"Operand for field '{fieldName}' (INTEGER) must be an integer.");

            case "REAL":
                if (element.ValueKind == JsonValueKind.Number)
                {
                    if (element.TryGetDouble(out var real))
                    {
                        return real;
                    }
                    throw new MutationsQueryException($"Operand for '{fieldName}' is out of the number range.");
                }
                if (element.ValueKind == JsonValueKind.String
                    && double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedReal))
                {
                    return parsedReal;
                }
                throw new MutationsQueryException($"Operand for field '{fieldName}' (REAL) must be a number.");

            default: // TEXT
                if (element.ValueKind == JsonValueKind.String && element.GetString() is { } text)
                {
                    return text;
                }
                if (element.ValueKind == JsonValueKind.Number)
                {
                    return element.GetRawText();
                }
                throw new MutationsQueryException($"Operand for field '{fieldName}' (TEXT) must be a string.");
        }
    }
}
