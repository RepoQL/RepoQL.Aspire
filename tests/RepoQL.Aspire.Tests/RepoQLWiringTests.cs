using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;

namespace RepoQL.Aspire.Tests;

public class RepoQLWiringTests
{
    private const string Disabled = "RepoQL telemetry streaming is disabled: ";

    private static RepoQLWatchEnvResult Run(string runId = "run123")
        => new(new Dictionary<string, string>(), runId, new Uri("http://127.0.0.1:63333/"));

    private static ContainerResource OtlpResource(string name = "api")
    {
        var resource = new ContainerResource(name);
        resource.Annotations.Add(new OtlpExporterAnnotation());
        return resource;
    }

    /// <summary>A built (not started) application with a RepoQL resource and one OTLP-enabled resource.</summary>
    private sealed class TestApp : IDisposable
    {
        private readonly DistributedApplication _app;

        public TestApp()
        {
            var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { DisableDashboard = true });
            builder.Services.AddLogging(logging => logging.AddProvider(Logs));
            RepoQL = builder.AddRepoQL().Resource;
            Api = builder.AddContainer("api", "example/api").WithOtlpExporter().Resource;
            _app = builder.Build();
        }

        public LogCapture Logs { get; } = new();

        public RepoQLResource RepoQL { get; }

        public IResource Api { get; }

        public Task WireAsync(RepoQLWiring.RunRegistrar registerRun, CancellationToken cancellationToken = default)
            => RepoQLWiring.WireAsync(
                RepoQL,
                "/app",
                "app",
                new BeforeStartEvent(_app.Services, _app.Services.GetRequiredService<DistributedApplicationModel>()),
                registerRun,
                cancellationToken);

        public string? RepoQLState
            => _app.Services.GetRequiredService<ResourceNotificationService>().TryGetCurrentState(RepoQL.Name, out var current)
                ? current.Snapshot.State?.Text
                : null;

        public void Dispose() => _app.Dispose();
    }

    [Test]
    public async Task WireAsync_WhenRegistrationTimesOut_FallsBackToStockWiring()
    {
        using var app = new TestApp();
        var apiCallbacks = app.Api.Annotations.OfType<EnvironmentCallbackAnnotation>().Count();

        await app.WireAsync((_, _, _, _) => Task.FromException<RepoQLWatchEnvResult>(
            new TimeoutException("'rql watch env' did not finish within 60 seconds and was stopped.")));

        app.RepoQLState.Should().Be(KnownResourceStates.FailedToStart);
        app.Logs.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning)
            .Which.Message.Should().Be(
                Disabled + "'rql watch env' did not finish within 60 seconds and was stopped. " +
                "The application runs with stock Aspire telemetry wiring.");
        app.Api.Annotations.OfType<EnvironmentCallbackAnnotation>().Should().HaveCount(apiCallbacks);
        app.RepoQL.Annotations.OfType<ResourceUrlAnnotation>().Should().BeEmpty();
    }

    [Test]
    public async Task WireAsync_WhenACancellationTheApplicationDidNotAskForEscapes_FallsBackToStockWiring()
    {
        // 0.1.2 let an internal timeout escape as TaskCanceledException, failing the AppHost's start.
        using var app = new TestApp();
        var apiCallbacks = app.Api.Annotations.OfType<EnvironmentCallbackAnnotation>().Count();

        await app.WireAsync((_, _, _, _) => Task.FromCanceled<RepoQLWatchEnvResult>(new CancellationToken(canceled: true)));

        app.RepoQLState.Should().Be(KnownResourceStates.FailedToStart);
        app.Logs.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Warning)
            .Which.Message.Should().StartWith(Disabled);
        app.Api.Annotations.OfType<EnvironmentCallbackAnnotation>().Should().HaveCount(apiCallbacks);
    }

    [Test]
    public async Task WireAsync_WhenTheApplicationCancels_PropagatesTheCancellation()
    {
        using var app = new TestApp();
        using var cancellation = new CancellationTokenSource();

        var wiring = app.WireAsync(
            async (_, _, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new UnreachableException();
            },
            cancellation.Token);
        await cancellation.CancelAsync();

        var act = () => wiring;
        await act.Should().ThrowAsync<OperationCanceledException>();
        app.RepoQLState.Should().NotBe(KnownResourceStates.FailedToStart);
        app.Logs.Entries.Should().NotContain(entry => entry.Message.StartsWith(Disabled));
    }

    [Test]
    public async Task AnnotateDashboardLink_AddsAUrlAnnotationTheOrchestratorWillSurface()
    {
        var repoql = new RepoQLResource("repoql");

        RepoQLWiring.AnnotateDashboardLink(repoql, "http://127.0.0.1:63333/#k=not-a-real-key");

        repoql.TryGetAnnotationsOfType<ResourceUrlAnnotation>(out var annotations).Should().BeTrue();
        var url = annotations!.Should().ContainSingle().Subject;
        url.Url.Should().Be("http://127.0.0.1:63333/#k=not-a-real-key");
        url.DisplayText.Should().Be("RepoQL dashboard");
        await Task.CompletedTask;
    }

    [Test]
    public async Task RedirectTelemetry_PointsOtlpResourceAtCollector()
    {
        var api = OtlpResource();
        var repoql = new RepoQLResource("repoql");

        RepoQLWiring.RedirectTelemetry([api, repoql], repoql, Run());

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(api);
        env["OTEL_EXPORTER_OTLP_ENDPOINT"].Should().Be("http://127.0.0.1:63333/api/otel");
        env["OTEL_EXPORTER_OTLP_PROTOCOL"].Should().Be("http/protobuf");
        env["OTEL_EXPORTER_OTLP_HEADERS"].Should().Be("repoql-watch-run-id=run123");
        env["OTEL_RESOURCE_ATTRIBUTES"].Should().Be("repoql.watch.run_id=run123");
    }

    [Test]
    public async Task RedirectTelemetry_OverwritesDashboardWiringAndDropsItsApiKey()
    {
        var api = OtlpResource();
        api.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
        {
            // Stands in for Aspire's built-in callback pointing at the dashboard.
            context.EnvironmentVariables["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:19200";
            context.EnvironmentVariables["OTEL_EXPORTER_OTLP_HEADERS"] = "x-otlp-api-key=secret";
        }));
        var repoql = new RepoQLResource("repoql");

        RepoQLWiring.RedirectTelemetry([api, repoql], repoql, Run());

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(api);
        env["OTEL_EXPORTER_OTLP_ENDPOINT"].Should().Be("http://127.0.0.1:63333/api/otel");
        env["OTEL_EXPORTER_OTLP_HEADERS"].Should().Be("repoql-watch-run-id=run123");
        env.Values.Should().NotContain(value => value.Contains("secret"));
    }

    [Test]
    public async Task RedirectTelemetry_AppendsRunAttributePreservingServiceInstanceId()
    {
        var api = OtlpResource();
        api.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
            context.EnvironmentVariables["OTEL_RESOURCE_ATTRIBUTES"] = "service.instance.id=abc"));
        var repoql = new RepoQLResource("repoql");

        RepoQLWiring.RedirectTelemetry([api, repoql], repoql, Run());

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(api);
        env["OTEL_RESOURCE_ATTRIBUTES"].Should().Be("service.instance.id=abc,repoql.watch.run_id=run123");
    }

    [Test]
    public async Task RedirectTelemetry_LeavesResourcesWithoutOtlpExporterUntouched()
    {
        var plain = new ContainerResource("plain");
        var repoql = new RepoQLResource("repoql");

        RepoQLWiring.RedirectTelemetry([plain, repoql], repoql, Run());

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(plain);
        env.Should().NotContainKey("OTEL_EXPORTER_OTLP_ENDPOINT");
    }

    [Test]
    public void ResolveDashboardForward_UsesTheHttpIngestionUrlAndApiKey()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"] = "http://localhost:19201",
            ["AppHost:OtlpApiKey"] = "secret",
        }).Build();

        var forward = RepoQLWiring.ResolveDashboardForward(configuration, NullLogger.Instance);

        forward.Should().NotBeNull();
        forward!.Url.Should().Be("http://localhost:19201");
        forward.Headers.Should().Be("x-otlp-api-key=secret");
    }

    [Test]
    public void ResolveDashboardForward_WithoutAnApiKey_SendsNoHeaders()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"] = "http://localhost:19201",
        }).Build();

        var forward = RepoQLWiring.ResolveDashboardForward(configuration, NullLogger.Instance);

        forward.Should().NotBeNull();
        forward!.Headers.Should().BeNull();
    }

    [Test]
    public void ResolveDashboardForward_WithoutTheHttpEndpoint_ReturnsNullEvenIfGrpcIsConfigured()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            // The gRPC ingestion URL cannot receive the OTLP/HTTP forward leg.
            ["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = "http://localhost:19200",
        }).Build();

        RepoQLWiring.ResolveDashboardForward(configuration, NullLogger.Instance).Should().BeNull();
    }
}
