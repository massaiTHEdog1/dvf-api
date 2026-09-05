using DvfApi.Database;
using DvfApi.Logging;
using DvfApi.Models;
using Microsoft.OpenApi;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Serilog: console + daily rolling file (path from "Configuration:LogsFolder" in appsettings.json).
// Installed before Build() so every logger resolved from DI — including the framework's
// (Microsoft.*) loggers, which Serilog.AspNetCore bridges to Serilog — goes through Serilog.
var serilogLogger = SerilogConfiguration.Create(builder.Configuration, builder.Environment);
builder.Host.UseSerilog(serilogLogger, dispose: true);

// The app listens on plain HTTP behind a reverse proxy, which terminates TLS and
// redirects http:// to https://. ASP.NET Core therefore cannot know the external scheme on its
// own and would advertise the server as http://dvf-api.fr in the OpenAPI document, making
// Swagger UI call the API over HTTP. The external base URL is configured instead
// ("OpenApi:ServerUrl" in appsettings.json); it is applied to both the Microsoft OpenAPI
// generator (served at /openapi/v1.json) and Swagger Gen (used by Swagger UI).
var openApiServerUrl = builder.Configuration["OpenApi:ServerUrl"];
if (openApiServerUrl is not null)
{
    var server = new OpenApiServer { Url = openApiServerUrl };
    builder.Services.AddOpenApi(options =>
        options.AddDocumentTransformer((document, context, cancellationToken) =>
        {
            document.Servers = new List<OpenApiServer> { server };
            return Task.CompletedTask;
        }));
}
else
{
    builder.Services.AddOpenApi();
}
builder.Services.AddSwaggerGen(options =>
{
    if (openApiServerUrl is not null)
    {
        options.AddServer(new OpenApiServer { Url = openApiServerUrl });
    }
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "DVF API",
        Version = "v1",
        Description = "API based on the French real estate transaction data (DVF)."
    });
});

// CORS: allow any origin (any headers/methods), so the API can be called from any frontend.
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();

// Paths of the DVF data folder and of the SQLite database (configured in appsettings.json).
// Read from the final configuration (app.Configuration) so that configuration added later —
// for example by WebApplicationFactory in tests — takes effect.
var config = app.Configuration;
string zipsFolder = config["Configuration:ZipsFolder"]!;
string databasePath = config["Configuration:DatabasePath"]!;

// At startup: make sure the SQLite database and its indexes exist, and import any new DVF file
// (a file is imported only if its year is not already present in the database).
await MutationsImporter.InitializeAsync(zipsFolder, databasePath, app.Logger);

app.Logger.LogInformation("Database ready at {Path}.", databasePath);

// Landing page: the French description of the site (wwwroot/index.html, with the API
// documentation, the data license and the legal notices). "UseDefaultFiles" rewrites "/" to
// "/index.html" and the static files middleware serves it. Static files are not API routes,
// so the page stays out of the OpenAPI document.
// Request logging: pushes a request id on the log context (so every log line of the request
// carries it), and logs request start / completion with method, path, status, client IP and
// duration. The request id comes from the incoming X-Request-Id header when present, otherwise
// a new GUID is generated; it is returned to the client in the X-Request-Id response header.
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseCors();

app.UseDefaultFiles();
app.UseStaticFiles();

// Swagger UI (served at /api/swagger).
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "DVF API v1");
    options.RoutePrefix = "api/swagger";
});

app.MapOpenApi();

// POST /api/mutations: serves rows from the "mutations" table.
// The JSON body carries an optional list of filters (field + operator + operand, combined with
// AND) and mandatory pagination (skip/take). Operators are validated against the declared type
// of the column (e.g. GREATER_THAN is rejected on text columns, LIKE on numeric columns).
// The date column "Date mutation" accepts all operators: range operators compare chronologically
// (operands in "DD/MM/YYYY" or "YYYY-MM-DD" format).
// The response is a standard pagination envelope: the rows of the requested page plus the total
// row count for the current filters, the current page, the page size and the total number of
// pages (all derived from skip/take).
// Invalid requests (missing pagination, unknown field, operator not allowed for the column type,
// operand of the wrong kind) return 400 with an "error" message.
app.MapPost("/api/mutations", async (MutationsQueryRequest request) =>
{
    try
    {
        var response = await MutationsQuery.QueryAsync(databasePath, request);
        return TypedResults.Ok(response);
    }
    catch (MutationsQueryException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
// Declared in the OpenAPI document (the endpoint's mixed return types do not allow inference).
.Produces<MutationsQueryResponse>();

app.Run();
