using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

var activitySource = new ActivitySource("Sample.Api");

builder.Logging.AddOpenTelemetry(options =>
{
    options.IncludeFormattedMessage = true;
    options.IncludeScopes = true;
});

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource(activitySource.Name))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation())
    .UseOtlpExporter(); // endpoint, protocol, and headers come from OTEL_EXPORTER_OTLP_* environment variables

var app = builder.Build();

app.MapGet("/", () => "RepoQL Aspire sample API");

app.MapGet("/work", (ILogger<Program> logger) =>
{
    using var activity = activitySource.StartActivity("sample.work");
    activity?.SetTag("sample.kind", "demo");
    logger.LogInformation("Doing sample work at {Timestamp}", DateTimeOffset.UtcNow);
    return Results.Ok(new { done = true });
});

app.MapGet("/fail", (ILogger<Program> logger) =>
{
    using var activity = activitySource.StartActivity("sample.fail");
    var error = new InvalidOperationException("Intentional sample failure");
    logger.LogError(error, "Sample failure requested");
    throw error;
});

app.Run();
