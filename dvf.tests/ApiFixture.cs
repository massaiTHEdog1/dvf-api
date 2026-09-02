using DvfApi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DvfTests;

/// <summary>
/// Integration test fixture: hosts the real application (the same "Program" entry point, the real
/// middleware pipeline and the real endpoints) with a small, known SQLite database and an empty
/// DVF zips folder, so the importer imports nothing. One fixture instance (and therefore one
/// database) is shared by all the tests in a class.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    private TestDb? _database;

    public WebApplicationFactory<Program>? Factory { get; private set; }

    public async Task InitializeAsync()
    {
        _database = new TestDb();
        await _database.InitializeAsync();

        var zipsFolder = Path.Combine(Path.GetTempPath(), $"dvf-tests-zips-{Guid.NewGuid():N}");
        Directory.CreateDirectory(zipsFolder);

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Configuration:ZipsFolder"] = zipsFolder,
                        ["Configuration:DatabasePath"] = _database!.DatabasePath
                    });
                });
            });
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }
}
