using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DvfTests;

/// <summary>
/// Integration tests of POST /api/mutations against a small, known database (5 rows, created by
/// <see cref="TestDb"/>): pagination envelope, filters, operators, date handling and all the
/// 400 error cases.
/// </summary>
public sealed class MutationsApiTests : IClassFixture<ApiFixture>
{
    private readonly WebApplicationFactory<Program> _factory;

    public MutationsApiTests(ApiFixture fixture)
    {
        _factory = fixture.Factory!;
    }

    private HttpClient Client => _factory.CreateClient();

    private static async Task<JsonElement> PostAsync(HttpClient client, object body)
    {
        using var response = await client.PostAsJsonAsync("/api/mutations", body);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    // The JSON body as a raw string: the request model uses object? for the operand, so raw JSON
    // keeps full control over the value kind (number vs string) sent on the wire.
    private static async Task<JsonElement> PostRawAsync(HttpClient client, string json)
    {
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/mutations", content);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private static (long TotalRows, int Page, int PageSize, long TotalPages, int RowCount) Pagination(JsonElement doc)
    {
        var pagination = doc.GetProperty("pagination");
        return (
            pagination.GetProperty("totalRows").GetInt64(),
            pagination.GetProperty("page").GetInt32(),
            pagination.GetProperty("pageSize").GetInt32(),
            pagination.GetProperty("totalPages").GetInt64(),
            doc.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task Unfiltered_ReturnsAllRows_WithCorrectEnvelope()
    {
        var doc = await PostAsync(Client, new { skip = 0, take = 10 });

        var (totalRows, page, pageSize, totalPages, rowCount) = Pagination(doc);
        Assert.Equal(5, totalRows);
        Assert.Equal(1, page);
        Assert.Equal(10, pageSize);
        Assert.Equal(1, totalPages);
        Assert.Equal(5, rowCount);

        // Every row exposes exactly the 43 public columns: no more (in particular not the
        // internal "Date mutation ISO" column), no less.
        var row = doc.GetProperty("rows").EnumerateArray().First();
        var names = row.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Equal(43, names.Count);
        Assert.DoesNotContain("Date mutation ISO", names);
        Assert.Contains("Valeur fonciere", names);
        Assert.Contains("Date mutation", names);
    }

    [Fact]
    public async Task Pagination_SkipsAndTakesRows_AndDerivesPageNumbers()
    {
        var first = await PostAsync(Client, new { skip = 0, take = 2 });
        Assert.Equal(2, Pagination(first).RowCount);

        var second = await PostAsync(Client, new { skip = 2, take = 2 });
        var (totalRows, page, pageSize, totalPages, rowCount) = Pagination(second);
        Assert.Equal(5, totalRows);
        Assert.Equal(2, page);
        Assert.Equal(2, pageSize);
        Assert.Equal(3, totalPages);
        Assert.Equal(2, rowCount);

        // The last page holds exactly the remaining row.
        var last = await PostAsync(Client, new { skip = 4, take = 2 });
        Assert.Equal(1, Pagination(last).RowCount);
        Assert.Equal(3, Pagination(last).Page);

        // An out-of-range skip returns an empty page; the page number is derived
        // from skip / take + 1 (51 for skip=100, take=2).
        var beyond = await PostAsync(Client, new { skip = 100, take = 2 });
        Assert.Equal(0, Pagination(beyond).RowCount);
        Assert.Equal(51, Pagination(beyond).Page);
    }

    [Fact]
    public async Task EqualsFilter_OnCodePostal_MatchesExactlyTheParisRows()
    {
        var doc = await PostRawAsync(Client, """
            { "skip": 0, "take": 10,
              "filters": [ { "field": "Code postal", "operator": "EQUALS", "operand": 75001 } ] }
            """);

        var (totalRows, _, _, _, rowCount) = Pagination(doc);
        Assert.Equal(2, totalRows);
        Assert.Equal(2, rowCount);

        // Both rows are the Paris rows, in ascending value order of insertion.
        foreach (var row in doc.GetProperty("rows").EnumerateArray())
        {
            Assert.Equal(75001, row.GetProperty("Code postal").GetInt64());
            Assert.Equal("Paris", row.GetProperty("Commune").GetString());
        }
    }

    [Fact]
    public async Task RangeFilter_OnValeurFonciere_FiltersChronologicallyIndependentValues()
    {
        var doc = await PostRawAsync(Client, """
            { "skip": 0, "take": 10,
              "filters": [ { "field": "Valeur fonciere", "operator": "GREATER_THAN", "operand": 400000 } ] }
            """);

        var (totalRows, _, _, _, rowCount) = Pagination(doc);
        Assert.Equal(3, totalRows);
        Assert.Equal(3, rowCount);

        foreach (var row in doc.GetProperty("rows").EnumerateArray())
        {
            Assert.True(row.GetProperty("Valeur fonciere").GetDouble() > 400_000);
        }
    }

    [Fact]
    public async Task LikeFilter_OnCommune_SupportsWildcards()
    {
        var doc = await PostRawAsync(Client, """
            { "skip": 0, "take": 10,
              "filters": [ { "field": "Commune", "operator": "LIKE", "operand": "%lyon%" } ] }
            """);

        var (totalRows, _, _, _, rowCount) = Pagination(doc);
        Assert.Equal(1, totalRows);
        Assert.Equal(1, rowCount);
        Assert.Equal("Lyon", doc.GetProperty("rows")[0].GetProperty("Commune").GetString());
    }

    [Fact]
    public async Task CombinedFilters_AreCombinedWithAnd()
    {
        var doc = await PostRawAsync(Client, """
            { "skip": 0, "take": 10,
              "filters": [
                { "field": "Code departement", "operator": "EQUALS", "operand": "69" },
                { "field": "Valeur fonciere", "operator": "LOWER_THAN", "operand": 300000 } ] }
            """);

        var (totalRows, _, _, _, rowCount) = Pagination(doc);
        Assert.Equal(1, totalRows);
        Assert.Equal(1, rowCount);

        var row = doc.GetProperty("rows")[0];
        Assert.Equal("Oullins", row.GetProperty("Commune").GetString());
        Assert.Equal(250_000, row.GetProperty("Valeur fonciere").GetDouble());
    }

    [Fact]
    public async Task DateRangeFilter_ComparesChronologically_NotAsStrings()
    {
        // "2025-01-01" <= date <= "2025-02-28": chronologically the 01/01/2025 and the
        // 05/02/2025 rows. As plain strings, "05/02/2025" would sort BEFORE "01/01/2025"
        // ("0" < "1"), so a string comparison could not produce this result.
        var doc = await PostRawAsync(Client, """
            { "skip": 0, "take": 10,
              "filters": [
                { "field": "Date mutation", "operator": "GREATER_THAN_OR_EQUALS", "operand": "2025-01-01" },
                { "field": "Date mutation", "operator": "LOWER_THAN_OR_EQUALS", "operand": "2025-02-28" } ] }
            """);

        var (totalRows, _, _, _, rowCount) = Pagination(doc);
        Assert.Equal(2, totalRows);
        Assert.Equal(2, rowCount);

        var dates = doc.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("Date mutation").GetString()).OrderBy(d => d).ToArray();
        Assert.Equal(new[] { "01/01/2025", "05/02/2025" }, dates);
    }

    [Fact]
    public async Task DateEquals_AcceptsBothFormats()
    {
        var iso = await PostRawAsync(Client, """
            { "skip": 0, "take": 10,
              "filters": [ { "field": "Date mutation", "operator": "EQUALS", "operand": "01/01/2025" } ] }
            """);
        Assert.Equal(1, Pagination(iso).TotalRows);

        var stored = await PostRawAsync(Client, """
            { "skip": 0, "take": 10,
              "filters": [ { "field": "Date mutation", "operator": "EQUALS", "operand": "2025-01-01" } ] }
            """);
        Assert.Equal(1, Pagination(stored).TotalRows);
        Assert.Equal(
            iso.GetProperty("rows")[0].GetProperty("Valeur fonciere").GetDouble(),
            stored.GetProperty("rows")[0].GetProperty("Valeur fonciere").GetDouble());
    }

    [Fact]
    public async Task NoMatch_ReturnsEmptyPage_WithZeroCounts()
    {
        var doc = await PostRawAsync(Client, """
            { "skip": 0, "take": 10,
              "filters": [ { "field": "Commune", "operator": "EQUALS", "operand": "Brest" } ] }
            """);

        var (totalRows, page, pageSize, totalPages, rowCount) = Pagination(doc);
        Assert.Equal(0, totalRows);
        Assert.Equal(1, page);
        Assert.Equal(10, pageSize);
        Assert.Equal(0, totalPages);
        Assert.Equal(0, rowCount);
    }

    // ------------------------------------------------------------------ error cases (400)

    private static async Task<string> ErrorAsync(HttpClient client, string json)
    {
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/mutations", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return doc.GetProperty("error").GetString()!;
    }

    [Theory]
    [InlineData("""{ "take": 10 }""", "'skip'")]
    [InlineData("""{ "skip": 0 }""", "'take'")]
    [InlineData("""{ "skip": -1, "take": 10 }""", "'skip'")]
    [InlineData("""{ "skip": 0, "take": 0 }""", "'take'")]
    [InlineData("""{ "skip": 0, "take": 1001 }""", "cannot exceed")]
    public async Task InvalidPagination_IsRejected(string body, string messagePart)
    {
        Assert.Contains(messagePart, await ErrorAsync(Client, body));
    }

    [Theory]
    [InlineData("""{ "skip": 0, "take": 10, "filters": [ { "field": "Unknown field", "operator": "EQUALS", "operand": "x" } ] }""", "Unknown field")]
    [InlineData("""{ "skip": 0, "take": 10, "filters": [ { "field": "Commune", "operator": "BOGUS", "operand": "x" } ] }""", "Invalid operator")]
    [InlineData("""{ "skip": 0, "take": 10, "filters": [ { "field": "Commune", "operator": "GREATER_THAN", "operand": "x" } ] }""", "not allowed on field 'Commune'")]
    [InlineData("""{ "skip": 0, "take": 10, "filters": [ { "field": "Valeur fonciere", "operator": "LIKE", "operand": "%x%" } ] }""", "not allowed on field 'Valeur fonciere'")]
    [InlineData("""{ "skip": 0, "take": 10, "filters": [ { "field": "Code postal", "operator": "EQUALS", "operand": "not a number" } ] }""", "must be an integer")]
    [InlineData("""{ "skip": 0, "take": 10, "filters": [ { "field": "Valeur fonciere", "operator": "EQUALS", "operand": "abc" } ] }""", "must be a number")]
    [InlineData("""{ "skip": 0, "take": 10, "filters": [ { "field": "Date mutation", "operator": "EQUALS", "operand": "31/13/2025" } ] }""", "must be a date")]
    [InlineData("""{ "skip": 0, "take": 10, "filters": [ { "field": "Date mutation", "operator": "EQUALS" } ] }""", "requires an 'operand'")]
    public async Task InvalidFilter_IsRejected(string body, string messagePart)
    {
        Assert.Contains(messagePart, await ErrorAsync(Client, body));
    }

    [Fact]
    public async Task MalformedJson_IsRejected()
    {
        // A malformed JSON body is rejected with 400. The response body is empty (the
        // framework returns a bare 400 before the endpoint handler runs).
        using var content = new StringContent("{ this is not json", System.Text.Encoding.UTF8, "application/json");
        using var response = await Client.PostAsync("/api/mutations", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
