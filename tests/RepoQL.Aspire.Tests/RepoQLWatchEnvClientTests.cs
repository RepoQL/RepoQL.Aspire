using AwesomeAssertions;
using TUnit.Core;

namespace RepoQL.Aspire.Tests;

public class RepoQLWatchEnvClientTests
{
    private const string ValidOutput =
        """
        {
          "OTEL_EXPORTER_OTLP_ENDPOINT": "http://127.0.0.1:63333/api/otel",
          "OTEL_EXPORTER_OTLP_PROTOCOL": "http/protobuf",
          "OTEL_EXPORTER_OTLP_HEADERS": "repoql-watch-run-id=6c9eab94180a49349e13424e80ab9f1a",
          "REPOQL_WATCH_RUN_ID": "6c9eab94180a49349e13424e80ab9f1a"
        }
        """;

    [Test]
    public void Parse_ReturnsRunIdBaseUrlAndEnvironment()
    {
        var result = RepoQLWatchEnvClient.Parse(ValidOutput);

        result.RunId.Should().Be("6c9eab94180a49349e13424e80ab9f1a");
        result.BaseUrl.Should().Be(new Uri("http://127.0.0.1:63333/"));
        result.Environment.Should().ContainKey("OTEL_EXPORTER_OTLP_PROTOCOL")
            .WhoseValue.Should().Be("http/protobuf");
    }

    [Test]
    public void Parse_WithoutRunId_ThrowsActionably()
    {
        var act = () => RepoQLWatchEnvClient.Parse("""{ "OTEL_EXPORTER_OTLP_ENDPOINT": "http://127.0.0.1:63333/api/otel" }""");

        act.Should().Throw<InvalidOperationException>().WithMessage("*REPOQL_WATCH_RUN_ID*");
    }

    [Test]
    public void Parse_WithoutCollectorEndpoint_ThrowsActionably()
    {
        var act = () => RepoQLWatchEnvClient.Parse("""{ "REPOQL_WATCH_RUN_ID": "abc" }""");

        act.Should().Throw<InvalidOperationException>().WithMessage("*api/otel*");
    }

    [Test]
    public void Parse_WithNonObjectOutput_ThrowsActionably()
    {
        var act = () => RepoQLWatchEnvClient.Parse("[]");

        act.Should().Throw<InvalidOperationException>().WithMessage("*JSON object*");
    }

    [Test]
    public void FailureReason_SkipsLaunchProgressToReportTheFailure()
    {
        const string stderr =
            """
            [rql] Launching host: /home/dev/.local/bin/rql serve --path "/src/app" --implicit-start
            [host 08:20:55] Loading index
            Watch env failed: Host did not become healthy within 120000ms (socket: /tmp/repoql.sock).

            """;

        RepoQLWatchEnvClient.FailureReason(stderr)
            .Should().Be("Watch env failed: Host did not become healthy within 120000ms (socket: /tmp/repoql.sock).");
    }

    [Test]
    public void FailureReason_ReportsTheFirstLineOfAMultiLineFailure()
    {
        const string stderr =
            """
            '/src/app' is not a RepoQL workspace — it has no .git directory and no .repoql marker.

            How RepoQL chose this directory:
              • it started from the current working directory: '/src/app'
            """;

        RepoQLWatchEnvClient.FailureReason(stderr)
            .Should().Be("'/src/app' is not a RepoQL workspace — it has no .git directory and no .repoql marker.");
    }

    [Test]
    public void FailureReason_FallsBackToTheLastProgressLine()
    {
        RepoQLWatchEnvClient.FailureReason("[rql] Launching host: rql serve\r\n[host 08:20:55] Loading index\r\n")
            .Should().Be("[host 08:20:55] Loading index");
        RepoQLWatchEnvClient.FailureReason(string.Empty).Should().BeEmpty();
    }

    [Test]
    public void BuildArguments_WithoutForward_RegistersAPlainRun()
    {
        RepoQLWatchEnvClient.BuildArguments("my-app", forward: null)
            .Should().Equal("watch", "env", "--name", "my-app", "--format", "json");
    }

    [Test]
    public void BuildArguments_WithForward_PassesUrlAndHeaders()
    {
        var forward = new RepoQLForwardTarget("http://localhost:19201", "x-otlp-api-key=secret");

        RepoQLWatchEnvClient.BuildArguments("my-app", forward)
            .Should().Equal(
                "watch", "env", "--name", "my-app", "--format", "json",
                "--forward", "http://localhost:19201",
                "--forward-headers", "x-otlp-api-key=secret");
    }

    [Test]
    public void BuildArguments_WithForwardButNoHeaders_OmitsTheHeadersFlag()
    {
        var forward = new RepoQLForwardTarget("http://localhost:19201", Headers: null);

        RepoQLWatchEnvClient.BuildArguments("my-app", forward)
            .Should().Equal(
                "watch", "env", "--name", "my-app", "--format", "json",
                "--forward", "http://localhost:19201");
    }
}
