using System.Text;
using System.Text.Json.Nodes;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RepoQL.Aspire;

/// <summary>
/// Wires the application model to a RepoQL watch run when the application starts.
/// </summary>
internal static class RepoQLWiring
{
    internal const string RunIdHeaderName = "repoql-watch-run-id";
    internal const string RunIdResourceAttribute = "repoql.watch.run_id";

    /// <summary>Registers a watch run; <see cref="RepoQLWatchEnvClient.RegisterRunAsync"/> in production.</summary>
    internal delegate Task<RepoQLWatchEnvResult> RunRegistrar(
        string workingDirectory,
        string runName,
        RepoQLForwardTarget? forward,
        CancellationToken cancellationToken);

    public static Task WireAsync(
        RepoQLResource resource,
        string appHostDirectory,
        string runName,
        BeforeStartEvent evt,
        CancellationToken cancellationToken)
        => WireAsync(resource, appHostDirectory, runName, evt, RepoQLWatchEnvClient.RegisterRunAsync, cancellationToken);

    /// <summary>
    /// Wires the application model to the run <paramref name="registerRun"/> registers, or leaves it on stock Aspire
    /// wiring when registration fails. Only the application's own cancellation propagates.
    /// </summary>
    internal static async Task WireAsync(
        RepoQLResource resource,
        string appHostDirectory,
        string runName,
        BeforeStartEvent evt,
        RunRegistrar registerRun,
        CancellationToken cancellationToken)
    {
        var logger = evt.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RepoQL.Aspire");
        var notifications = evt.Services.GetRequiredService<ResourceNotificationService>();
        var forward = ResolveDashboardForward(evt.Services.GetRequiredService<IConfiguration>(), logger);

        RepoQLWatchEnvResult run;
        try
        {
            run = await registerRun(appHostDirectory, runName, forward, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A cancellation the application did not ask for, such as an internal timeout, is a failure like any other.
            logger.LogWarning(
                "RepoQL telemetry streaming is disabled: {Reason} The application runs with stock Aspire telemetry wiring.",
                ex.Message);
            await notifications.PublishUpdateAsync(resource, snapshot => snapshot with
            {
                State = new ResourceStateSnapshot(KnownResourceStates.FailedToStart, KnownResourceStateStyles.Error),
            }).ConfigureAwait(false);
            return;
        }

        RedirectTelemetry(evt.Model.Resources, resource, run);
        RegisterCompletionHook(evt.Services, run, logger);

        var dashboardLink = await RepoQLDashboardLinkClient.TryGetLinkAsync(appHostDirectory, run.BaseUrl, cancellationToken).ConfigureAwait(false);
        if (dashboardLink is null)
        {
            logger.LogInformation(
                "The RepoQL dashboard link could not be fetched, so the resource links to the host's origin. Run 'rql dashboard' to open the dashboard.");
        }

        AnnotateDashboardLink(resource, dashboardLink ?? run.BaseUrl.ToString().TrimEnd('/'));

        await notifications.PublishUpdateAsync(resource, snapshot => snapshot with
        {
            State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
            Properties = [new("repoql.run.id", run.RunId)],
        }).ConfigureAwait(false);

        logger.LogInformation(
            "RepoQL is streaming this application's telemetry — run {RunId}, collector {BaseUrl}",
            run.RunId, run.BaseUrl);
    }

    /// <summary>
    /// Adds the RepoQL dashboard link as a URL annotation so it survives URL processing.
    /// </summary>
    /// <remarks>
    /// The link must be a <see cref="ResourceUrlAnnotation"/>: when endpoints allocate, the
    /// orchestrator recomputes every resource's snapshot Urls from annotations and replaces the
    /// array wholesale — a URL published only on the snapshot is wiped before anyone sees it.
    /// The keyed link from <c>rql dashboard --url</c> carries the dashboard's access key in its
    /// fragment, so it is shown in the Aspire dashboard and never logged by this package.
    /// </remarks>
    internal static void AnnotateDashboardLink(RepoQLResource resource, string url)
    {
        resource.Annotations.Add(new ResourceUrlAnnotation
        {
            Url = url,
            DisplayText = "RepoQL dashboard",
        });
    }

    /// <summary>
    /// Resolves the Aspire dashboard's OTLP/HTTP ingestion endpoint as the run's forward target so
    /// the human's live view keeps working while RepoQL indexes the stream.
    /// </summary>
    /// <remarks>
    /// Config-first because BeforeStartEvent runs before the dashboard's endpoints allocate. The
    /// forward leg speaks OTLP/HTTP, so only the HTTP ingestion URL qualifies — the default
    /// ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL is gRPC and cannot receive it.
    /// </remarks>
    internal static RepoQLForwardTarget? ResolveDashboardForward(IConfiguration configuration, ILogger logger)
    {
        var httpUrl = configuration["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"];
        if (string.IsNullOrWhiteSpace(httpUrl))
        {
            logger.LogInformation(
                "No dashboard OTLP/HTTP endpoint configured — telemetry is indexed by RepoQL but will not appear in the Aspire dashboard. " +
                "Set ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL (e.g. in the AppHost's launchSettings.json) to light both up.");
            return null;
        }

        var apiKey = configuration["AppHost:OtlpApiKey"];
        return new RepoQLForwardTarget(
            httpUrl,
            string.IsNullOrWhiteSpace(apiKey) ? null : $"x-otlp-api-key={apiKey}");
    }

    /// <summary>
    /// Appends an environment callback to every OTLP-enabled resource that repoints its exporter
    /// at the RepoQL collector. Runs after Aspire's built-in callback, so assignments overwrite.
    /// </summary>
    internal static void RedirectTelemetry(IEnumerable<IResource> resources, RepoQLResource repoql, RepoQLWatchEnvResult run)
    {
        // The collector shares the host's single listener; SDKs append /v1/{signal} to this base.
        var collectorEndpoint = new HostUrl(new Uri(run.BaseUrl, "/api/otel").ToString().TrimEnd('/'));
        var runIdHeader = $"{RunIdHeaderName}={run.RunId}";

        foreach (var resource in resources)
        {
            if (ReferenceEquals(resource, repoql) || !resource.HasAnnotationOfType<OtlpExporterAnnotation>())
                continue;

            resource.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
            {
                if (context.ExecutionContext.IsPublishMode)
                    return;

                context.EnvironmentVariables["OTEL_EXPORTER_OTLP_ENDPOINT"] = collectorEndpoint;
                context.EnvironmentVariables["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";

                // The run-id routing header replaces the dashboard api key: resources talk to RepoQL
                // now, and the api key travels on the host's forward leg to the dashboard instead.
                context.EnvironmentVariables["OTEL_EXPORTER_OTLP_HEADERS"] = runIdHeader;

                // Append the run attribute, preserving Aspire's service.instance.id template.
                var attribute = $"{RunIdResourceAttribute}={run.RunId}";
                context.EnvironmentVariables["OTEL_RESOURCE_ATTRIBUTES"] =
                    context.EnvironmentVariables.TryGetValue("OTEL_RESOURCE_ATTRIBUTES", out var existing)
                    && existing is string current && current.Length > 0
                        ? $"{current},{attribute}"
                        : attribute;
            }));
        }
    }

    /// <summary>
    /// Marks the watch run complete when the application shuts down. Best effort — an abandoned
    /// run is retired by the host's retention sweep.
    /// </summary>
    private static void RegisterCompletionHook(IServiceProvider services, RepoQLWatchEnvResult run, ILogger logger)
    {
        var lifetime = services.GetService<IHostApplicationLifetime>();
        if (lifetime is null)
            return;

        lifetime.ApplicationStopping.Register(() =>
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var payload = new JsonObject { ["runId"] = run.RunId, ["exitCode"] = 0 };
                using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
                client.PostAsync(new Uri(run.BaseUrl, "/api/watch/complete"), content)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.LogDebug("RepoQL watch run {RunId} was not marked complete: {Reason}", run.RunId, ex.Message);
            }
        });
    }
}
