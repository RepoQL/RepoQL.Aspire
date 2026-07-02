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
}
