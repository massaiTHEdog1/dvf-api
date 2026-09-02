using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DvfApi.Database;

/// <summary>
/// Creates the SQLite database (if needed) and imports new DVF files from the "mutations-zips" folder.
/// Each zip contains one "|" delimited CSV text file with a full year of mutations.
/// A year is imported only if it is not already present in the "mutations" table.
/// </summary>
public static class MutationsImporter
{
    private const string Table = "mutations";

    // Indexes on the columns most often used as /api/mutations filters (created once, idempotently).
    // Note: no index on "Date mutation" itself: its "DD/MM/YYYY" format does not sort
    // chronologically as a string. The derived "Date mutation ISO" column ("YYYY-MM-DD") is
    // indexed instead and serves the date range filters.
    private static readonly (string Name, string Column)[] TableIndexes =
    {
        ("idx_mutations_code_postal", "Code postal"),
        ("idx_mutations_code_commune", "Code commune"),
        ("idx_mutations_code_departement", "Code departement"),
        ("idx_mutations_valeur_fonciere", "Valeur fonciere"),
        ("idx_mutations_date_iso", MutationsSchema.SortableDateColumn),
    };

    // Backfill of the sortable date column is done in chunks of this many rows (rowid ranges).
    private const long DateBackfillChunkSize = 1_000_000;

    // Matches zip file names like "valeursfoncieres-2025.txt.zip".
    private static readonly Regex YearPattern = new(@"(\d{4})", RegexOptions.Compiled);

    // Number format for parsing French numbers: the files use ',' as decimal separator ("468000,00").
    private static readonly NumberFormatInfo FrenchNumbers = new CultureInfo("fr-FR").NumberFormat;


    public static async Task InitializeAsync(string zipsFolder, string databasePath, ILogger logger)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();

        await EnsureTableAsync(connection, logger);
        await EnsureSortableDateColumnAsync(connection, logger);
        await EnsureIndexesAsync(connection, logger);

        var importedYears = await GetImportedYearsAsync(connection);
        var existingYears = importedYears.ToHashSet();

        foreach (var zipPath in Directory.EnumerateFiles(zipsFolder, "*.zip").OrderBy(p => p))
        {
            if (!TryExtractYear(Path.GetFileName(zipPath), out var year))
            {
                logger.LogWarning("Skipping file {File}: no year found in file name.", zipPath);
                continue;
            }

            if (existingYears.Contains(year))
            {
                logger.LogInformation("Year {Year} already present in database, skipping file {File}.", year, zipPath);
                continue;
            }

            logger.LogInformation("Importing year {Year} from {File}...", year, zipPath);
            var count = await ImportFileAsync(connection, zipPath, year, logger);
            logger.LogInformation("Imported {Count:N0} rows for year {Year}.", count, year);
        }
    }

    /// <summary>Creates the "mutations" table if it does not exist.</summary>
    private static async Task EnsureTableAsync(SqliteConnection connection, ILogger logger)
    {
        using var existsQuery = new SqliteCommand(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='mutations';", connection);
        var exists = (long)(await existsQuery.ExecuteScalarAsync())!;

        if (exists == 0)
        {
            using var createCommand = new SqliteCommand(
                $"CREATE TABLE {Table} (\n{MutationsSchema.TableColumnDefinitions()}\n);", connection);
            await createCommand.ExecuteNonQueryAsync();
            logger.LogInformation("Created table '{Table}'.", Table);
        }
    }

    /// <summary>
    /// Ensures the derived sortable date column "Date mutation ISO" ("YYYY-MM-DD") exists and is
    /// filled. Databases created before this column existed get it added (ALTER TABLE) and
    /// backfilled from the "DD/MM/YYYY" source value, in rowid chunks so progress is logged and
    /// memory stays bounded. Idempotent: rows already filled are never touched.
    /// </summary>
    private static async Task EnsureSortableDateColumnAsync(SqliteConnection connection, ILogger logger)
    {
        using var existsQuery = new SqliteCommand(
            $"SELECT COUNT(*) FROM pragma_table_info('{Table}') WHERE name = '{MutationsSchema.SortableDateColumn}';",
            connection);
        var columnExists = (long)(await existsQuery.ExecuteScalarAsync())!;

        if (columnExists == 0)
        {
            using var alterCommand = new SqliteCommand(
                $"ALTER TABLE {Table} ADD COLUMN \"{MutationsSchema.SortableDateColumn}\" TEXT;", connection);
            await alterCommand.ExecuteNonQueryAsync();
            logger.LogInformation("Added column '{Column}' to table '{Table}'.", MutationsSchema.SortableDateColumn, Table);
        }

        // Backfill the rows that still have no value (only possible on pre-existing databases;
        // new imports fill the column directly). Chunked by rowid ranges, one transaction each.
        using var maxQuery = new SqliteCommand($"SELECT MAX(rowid) FROM {Table};", connection);
        var maxRowId = (await maxQuery.ExecuteScalarAsync()) as long?;
        if (maxRowId is null)
        {
            return; // empty table, nothing to backfill
        }

        var total = 0L;
        for (var low = 1L; low <= maxRowId; low += DateBackfillChunkSize)
        {
            var high = Math.Min(low + DateBackfillChunkSize - 1, maxRowId.Value);
            await using var transaction = await connection.BeginTransactionAsync();
            using var updateCommand = new SqliteCommand($"""
                UPDATE {Table}
                SET "{MutationsSchema.SortableDateColumn}" =
                    substr("{MutationsSchema.DateColumn}", 7, 4) || '-' ||
                    substr("{MutationsSchema.DateColumn}", 4, 2) || '-' ||
                    substr("{MutationsSchema.DateColumn}", 1, 2)
                WHERE "{MutationsSchema.DateColumn}" IS NOT NULL
                  AND "{MutationsSchema.SortableDateColumn}" IS NULL
                  AND rowid BETWEEN @low AND @high;
                """, connection, (SqliteTransaction)transaction);
            updateCommand.Parameters.AddWithValue("@low", low);
            updateCommand.Parameters.AddWithValue("@high", high);
            var updated = await updateCommand.ExecuteNonQueryAsync();
            await transaction.CommitAsync();

            if (updated > 0)
            {
                total += updated;
                logger.LogInformation("Backfilled {Updated:N0} rows of '{Column}' (rowid {Low:N0}-{High:N0}).",
                    updated, MutationsSchema.SortableDateColumn, low, high);
            }
        }

        if (total > 0)
        {
            logger.LogInformation("Backfilled {Total:N0} rows of '{Column}' in total.", total, MutationsSchema.SortableDateColumn);
        }
    }

    /// <summary>Creates the filter indexes if they do not exist (takes a few minutes on a full database).</summary>
    private static async Task EnsureIndexesAsync(SqliteConnection connection, ILogger logger)
    {
        foreach (var (name, column) in TableIndexes)
        {
            var elapsed = Stopwatch.StartNew();
            using var command = new SqliteCommand($"CREATE INDEX IF NOT EXISTS {name} ON {Table} (\"{column}\");", connection);
            await command.ExecuteNonQueryAsync();
            logger.LogInformation("Index {Name} on '{Column}' ready ({Seconds:F1}s).", name, column, elapsed.Elapsed.TotalSeconds);
        }
    }

    /// <summary>Returns the set of years already imported (the "Date mutation" field is "DD/MM/YYYY").</summary>
    private static async Task<HashSet<string>> GetImportedYearsAsync(SqliteConnection connection)
    {
        var years = new HashSet<string>();
        using var query = new SqliteCommand(
            "SELECT DISTINCT substr(\"Date mutation\", 7) AS year FROM mutations;", connection);
        await using var reader = await query.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            years.Add(reader.GetString(0));
        }

        return years;
    }

    private static bool TryExtractYear(string fileName, out string year)
    {
        var match = YearPattern.Match(fileName);
        year = match.Success ? match.Groups[1].Value : string.Empty;
        return match.Success;
    }

    /// <summary>
    /// Converts a raw date value ("DD/MM/YYYY", source format) to the sortable "YYYY-MM-DD" form.
    /// Empty or unexpected values become NULL (the data is validated at import time, this is
    /// defensive only).
    /// </summary>
    private static string? ToSortableDate(string value) => value.Length == 10
        ? $"{value.Substring(6, 4)}-{value.Substring(3, 2)}-{value.Substring(0, 2)}"
        : null;

    /// <summary>
    /// Converts a field value to the value that should be stored in the given column type.
    /// Empty fields become NULL. Numeric columns accept only plain digits; REAL columns accept
    /// the French number format (decimal separator ',').
    /// </summary>
    private static object? ConvertField(string value, string type)
    {
        if (value.Length == 0)
        {
            return null;
        }

        return type switch
        {
            "INTEGER" => long.Parse(value, CultureInfo.InvariantCulture),
            "REAL" => double.Parse(value, FrenchNumbers),
            _ => value
        };
    }

    /// <summary>
    /// Opens the zip, reads the "|" delimited text entry and inserts its rows into the "mutations" table.
    /// Returns the number of inserted rows.
    /// </summary>
    private static async Task<long> ImportFileAsync(SqliteConnection connection, string zipPath, string year, ILogger logger)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.First();

        using var stream = entry.Open();
        using var textReader = new StreamReader(stream);

        var tableColumns = MutationsSchema.Columns;
        var tableColumnTypes = MutationsSchema.ColumnTypes;

        // The table has one derived column more than the source files have fields: the sortable
        // date column, filled from the raw "Date mutation" value ("DD/MM/YYYY" -> "YYYY-MM-DD").
        var dateFieldIndex = Array.IndexOf(tableColumns, MutationsSchema.DateColumn);
        var derivedParameterName = "@p" + tableColumns.Length;
        var transaction = await connection.BeginTransactionAsync();

        // One named parameter per table column, added to the command so the values are bound on every execution.
        var parameterNames = tableColumns.Select((_, i) => $"@p{i}").Append(derivedParameterName).ToArray();
        var insertColumns = string.Join(", ", tableColumns.Select(c => $"\"{c}\""))
            + $", \"{MutationsSchema.SortableDateColumn}\"";
        var insertCommand = new SqliteCommand(
            $"INSERT INTO {Table} ({insertColumns}) VALUES ({string.Join(", ", parameterNames)});",
            connection)
        {
            Transaction = (SqliteTransaction)transaction
        };
        var parameters = tableColumns
            .Select((_, i) => { var p = new SqliteParameter(); p.ParameterName = $"@p{i}"; return p; })
            .Append(new SqliteParameter { ParameterName = derivedParameterName })
            .ToArray();
        foreach (var parameter in parameters)
        {
            insertCommand.Parameters.Add(parameter);
        }

        // Skip the header line of the file.
        _ = await textReader.ReadLineAsync();

        var expectedCount = tableColumns.Length;

        long count = 0;
        long batchSize = 0;

        string? line = await textReader.ReadLineAsync();
        while (line != null)
        {
            var fields = line.Split('|');

            // Safety check: the row must have the same number of fields as the table has columns.
            if (fields.Length != expectedCount)
            {
                logger.LogWarning("Row with {Length} fields (expected {Expected}) skipped in {File}.", fields.Length, expectedCount, zipPath);
            }
            else
            {
                for (var i = 0; i < expectedCount; i++)
                {
                    var converted = ConvertField(fields[i], tableColumnTypes[i]);
                    parameters[i].Value = converted ?? DBNull.Value;
                }

                // Derived sortable date column: "DD/MM/YYYY" -> "YYYY-MM-DD".
                var rawDate = fields[dateFieldIndex];
                parameters[tableColumns.Length].Value = (object?)ToSortableDate(rawDate) ?? DBNull.Value;

                await insertCommand.ExecuteNonQueryAsync();
                count++;
            }

            batchSize++;

            // Periodically yield control so the request pipeline stays responsive.
            if (batchSize % 50_000 == 0)
            {
                logger.LogInformation("Imported {Count:N0} rows so far for year {Year}...", count, year);
                await Task.Yield();
            }

            line = await textReader.ReadLineAsync();
        }

        await transaction.CommitAsync();
        return count;
    }
}
