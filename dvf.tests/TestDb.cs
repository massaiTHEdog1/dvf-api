using DvfApi.Database;
using Microsoft.Data.Sqlite;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;

namespace DvfTests;

/// <summary>
/// Creates a small temporary SQLite database with the exact same schema as the production
/// "mutations" table (same columns, types and derived sortable date column) and inserts a
/// fixed, known dataset. The database is deleted when <see cref="Dispose"/> is called.
/// The integration tests point the API at it through the "Configuration:DatabasePath"
/// configuration key (the importer then finds every year already present and imports nothing).
/// </summary>
public sealed class TestDb : IAsyncDisposable
{
    public string DatabasePath { get; } = Path.Combine(Path.GetTempPath(), $"dvf-tests-{Guid.NewGuid():N}.db");

    // Fixed dataset (5 rows, years 2024-2025, covering the filter columns used by the tests).
    private static readonly (string Date, long Postal, string Commune, string Dept, long CommuneCode, double Valeur, int Pieces)[] Rows =
    {
        ("15/03/2024", 75001, "Paris",        "75", 75056, 300_000.00, 2),
        ("01/01/2025", 75001, "Paris",        "75", 75056, 1_200_000.00, 4),
        ("05/02/2025", 69002, "Lyon",         "69", 69123, 500_000.50, 3),
        ("29/02/2024", 69100, "Oullins",      "69", 69144, 250_000.00, 1),
        ("31/12/2025", 13008, "Marseille",    "13", 13055, 800_000.00, 5),
    };

    public TestDb()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DatabasePath)!);

        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        using var command = new SqliteCommand(
            $"CREATE TABLE mutations (\n{MutationsSchema.TableColumnDefinitions()}\n);", connection);
        command.ExecuteNonQuery();

        // One row per column of the table, bound positionally.
        var insert = new SqliteCommand(
            $"INSERT INTO mutations (" +
            $"\"Identifiant de document\", \"Reference document\", \"1 Articles CGI\", \"Nature mutation\"," +
            $"\"No disposition\", \"Date mutation\", \"Valeur fonciere\", \"Code voie\", \"Voie\"," +
            $"\"Code postal\", \"Commune\", \"Code departement\", \"Code commune\", \"Section\"," +
            $"\"1er lot\", \"Surface Carrez du 1er lot\", \"Nombre de lots\", \"Code type local\", \"Type local\"," +
            $"\"Nombre pieces principales\", \"Surface terrain\", \"{MutationsSchema.SortableDateColumn}\") " +
            $"VALUES (@id, @ref, @cgi1, @nature, @noDisp, @date, @valeur, @codeVoie, @voie, " +
            $"@cp, @commune, @dept, @codeCommune, @section, @lot1, @carrez, @lots, @typeLocal, @typeLocalName, " +
            $"@pieces, @terrain, @isoDate);", connection);

        foreach (var row in Rows)
        {
            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("@id", $"TEST-{row.Postal}-{row.Date}");
            insert.Parameters.AddWithValue("@ref", "TEST-REF");
            insert.Parameters.AddWithValue("@cgi1", "1329");
            insert.Parameters.AddWithValue("@nature", "VENTE");
            insert.Parameters.AddWithValue("@noDisp", 1L);
            insert.Parameters.AddWithValue("@date", row.Date);
            insert.Parameters.AddWithValue("@valeur", row.Valeur);
            insert.Parameters.AddWithValue("@codeVoie", (object?)"");
            insert.Parameters.AddWithValue("@voie", "TEST ST");
            insert.Parameters.AddWithValue("@cp", row.Postal);
            insert.Parameters.AddWithValue("@commune", row.Commune);
            insert.Parameters.AddWithValue("@dept", row.Dept);
            insert.Parameters.AddWithValue("@codeCommune", row.CommuneCode);
            insert.Parameters.AddWithValue("@section", (object?)"");
            insert.Parameters.AddWithValue("@lot1", "1");
            insert.Parameters.AddWithValue("@carrez", (object?)42.5d);
            insert.Parameters.AddWithValue("@lots", 1L);
            insert.Parameters.AddWithValue("@typeLocal", "2");
            insert.Parameters.AddWithValue("@typeLocalName", "Maison");
            insert.Parameters.AddWithValue("@pieces", row.Pieces);
            insert.Parameters.AddWithValue("@terrain", 0L);
            insert.Parameters.AddWithValue("@isoDate", $"{row.Date.Substring(6, 4)}-{row.Date.Substring(3, 2)}-{row.Date.Substring(0, 2)}");
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Runs the production startup routine (table check, sortable date column, indexes) against the
    /// test database with an empty zips folder: nothing is imported, but the indexes are created and
    /// the code path is exercised exactly as in production.
    /// </summary>
    public async Task InitializeAsync()
    {
        var emptyZips = Path.Combine(Path.GetTempPath(), $"dvf-tests-zips-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyZips);
        await MutationsImporter.InitializeAsync(emptyZips, DatabasePath, NullLogger.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        if (File.Exists(DatabasePath))
        {
            File.Delete(DatabasePath);
        }
    }
}
