using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core;
using TUnit.Core.Enums;

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

    [Test]
    [ExcludeOn(OS.Windows)] // the stand-in CLI is a POSIX shell script
    public async Task RunAsync_ReturnsTheCliOutputAndExitCode()
    {
        using var cli = StandInCli.Answers(stdout: ValidOutput, stderr: "[rql watch env] run: 6c9e", exitCode: 3);

        var (stdout, stderr, exitCode) = await RepoQLWatchEnvClient.RunAsync(
            [cli.Path], cli.WorkingDirectory, ["watch", "env"], TimeSpan.FromSeconds(60), new FakeTimeProvider(), CancellationToken.None);

        stdout.Should().Be(ValidOutput);
        stderr.Should().Be("[rql watch env] run: 6c9e");
        exitCode.Should().Be(3);
    }

    [Test]
    [ExcludeOn(OS.Windows)] // the stand-in CLI is a POSIX shell script
    public async Task RunAsync_WhenTheCliHangs_StopsItAndThrowsATimeoutThatNamesNoSecrets()
    {
        using var cli = StandInCli.Hangs();
        var time = new FakeTimeProvider();
        var arguments = RepoQLWatchEnvClient.BuildArguments("my-app", new RepoQLForwardTarget("http://localhost:19201", "x-otlp-api-key=secret"));

        var run = RepoQLWatchEnvClient.RunAsync(
            [cli.Path], cli.WorkingDirectory, arguments, TimeSpan.FromSeconds(60), time, CancellationToken.None);
        var pid = await cli.WaitUntilHangingAsync(run);
        time.Advance(TimeSpan.FromSeconds(59));
        run.IsCompleted.Should().BeFalse("the timeout has not elapsed yet");
        time.Advance(TimeSpan.FromSeconds(1));

        var act = () => run;
        var timeout = await act.Should().ThrowAsync<TimeoutException>();
        timeout.WithMessage("'rql watch env' did not finish within 60 seconds and was stopped.*")
            .Which.Message.Should().NotContain("secret");
        StandInCli.IsRunning(pid).Should().BeFalse();
    }

    [Test]
    [ExcludeOn(OS.Windows)] // the stand-in CLI is a POSIX shell script
    public async Task RunAsync_WhenTheCallerCancels_StopsTheCliAndPropagatesTheCancellation()
    {
        using var cli = StandInCli.Hangs();
        using var cancellation = new CancellationTokenSource();

        var run = RepoQLWatchEnvClient.RunAsync(
            [cli.Path], cli.WorkingDirectory, ["watch", "env"], TimeSpan.FromSeconds(60), new FakeTimeProvider(), cancellation.Token);
        var pid = await cli.WaitUntilHangingAsync(run);
        await cancellation.CancelAsync();

        var act = () => run;
        await act.Should().ThrowAsync<OperationCanceledException>();
        StandInCli.IsRunning(pid).Should().BeFalse();
    }

    [Test]
    [ExcludeOn(OS.Windows)] // the stand-in CLI is a POSIX shell script
    public async Task RunAsync_WhenTheCliHangs_LeavesAHostItLaunchedRunning()
    {
        using var cli = StandInCli.Hangs(launchesHost: true);
        var time = new FakeTimeProvider();

        var run = RepoQLWatchEnvClient.RunAsync(
            [cli.Path], cli.WorkingDirectory, ["watch", "env"], TimeSpan.FromSeconds(60), time, CancellationToken.None);
        var pid = await cli.WaitUntilHangingAsync(run);
        time.Advance(TimeSpan.FromSeconds(60));

        var act = () => run;
        await act.Should().ThrowAsync<TimeoutException>();
        StandInCli.IsRunning(pid).Should().BeFalse();
        var hostPid = cli.HostPid;
        hostPid.Should().NotBeNull();
        StandInCli.IsRunning(hostPid!.Value).Should().BeTrue(
            "the workspace host is shared infrastructure whose lifetime is independent of the AppHost");
    }
}
