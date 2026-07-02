using Aspire.Hosting.ApplicationModel;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;

namespace RepoQL.Aspire.Tests;

public class RepoQLWiringTests
{
    private static RepoQLWatchEnvResult Run(string runId = "run123")
        => new(new Dictionary<string, string>(), runId, new Uri("http://127.0.0.1:63333/"));

    private static ContainerResource OtlpResource(string name = "api")
    {
        var resource = new ContainerResource(name);
        resource.Annotations.Add(new OtlpExporterAnnotation());
        return resource;
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
