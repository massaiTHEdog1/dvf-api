using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DvfTests;

/// <summary>
/// Integration tests of the non-API routes: the French landing page (static files) and the
/// OpenAPI / Swagger endpoints. The real application pipeline is exercised.
/// </summary>
public sealed class LandingPageTests : IClassFixture<ApiFixture>
{
    private readonly WebApplicationFactory<Program> _factory;

    public LandingPageTests(ApiFixture fixture)
    {
        _factory = fixture.Factory!;
    }

    [Fact]
    public async Task Root_ServesTheFrenchLandingPage()
    {
        using var response = await _factory.CreateClient().GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType?.MediaType);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Données de valeurs foncières", html);
        Assert.Contains("Licence Ouverte", html);
        Assert.Contains("Mentions légales", html);
        Assert.Contains("https://dvf-api.fr", html);
    }

    [Fact]
    public async Task IndexHtml_IsServedDirectlyToo()
    {
        using var response = await _factory.CreateClient().GetAsync("/index.html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task OpenApiSpec_IsServedAndContainsOnlyTheMutationsRoute()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.MediaType);

        var spec = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"openapi\"", spec);
        Assert.Contains("/api/mutations", spec);
        // The landing page is a static file, not an API route: it must not appear in the spec.
        Assert.DoesNotContain("\"/index.html\"", spec);
    }

    [Fact]
    public async Task SwaggerUi_IsServed()
    {
        var options = new WebApplicationFactoryClientOptions { AllowAutoRedirect = false };
        using var client = _factory.CreateClient(options);
        using var response = await client.GetAsync("/api/swagger");

        // The route redirects to swagger/index.html (relative).
        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("swagger/index.html", response.Headers.Location?.ToString());
    }
}
