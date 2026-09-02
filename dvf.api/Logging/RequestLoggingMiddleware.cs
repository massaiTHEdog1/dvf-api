using Serilog.Context;

namespace DvfApi.Logging;

/// <summary>
/// Associates a request id with every Serilog event emitted while the request is being
/// processed, and logs request start / completion with method, path, status code, query
/// string, client IP and duration.
///
/// The request id is the incoming "X-Request-Id" header value when present (so upstream
/// proxies / clients can correlate), otherwise a new GUID is generated. The same value is
/// written back in the "X-Request-Id" response header.
///
/// The request id is pushed on Serilog's AsyncLocal log context and rendered through the
/// {CorrelationId} token in the log output template.
/// </summary>
public sealed class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger _logger;

    private const string RequestIdHeader = "X-Request-Id";

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Reuse the caller's request id when provided; generate one otherwise.
        var requestId = context.Request.Headers[RequestIdHeader].FirstOrDefault();
        if (string.IsNullOrEmpty(requestId))
        {
            requestId = Guid.NewGuid().ToString();
        }

        context.Response.Headers[RequestIdHeader] = requestId;

        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "-";

        using (LogContext.PushProperty("CorrelationId", requestId))
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            _logger.LogInformation(
                "Request started: {Method} {Path} (query: {QueryString}) from {ClientIp}",
                context.Request.Method, context.Request.Path, context.Request.QueryString, clientIp);

            try
            {
                await _next(context);
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                _logger.LogError(exception,
                    "Request failed after {ElapsedMs:F1} ms: {Method} {Path} -> {StatusCode}",
                    stopwatch.ElapsedMilliseconds, context.Request.Method, context.Request.Path,
                    context.Response.StatusCode);
                throw;
            }

            stopwatch.Stop();
            _logger.LogInformation(
                "Request completed in {ElapsedMs:F1} ms: {Method} {Path} -> {StatusCode}",
                stopwatch.ElapsedMilliseconds, context.Request.Method, context.Request.Path,
                context.Response.StatusCode);
        }
    }
}
