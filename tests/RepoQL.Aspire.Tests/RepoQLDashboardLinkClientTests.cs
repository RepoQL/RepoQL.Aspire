using AwesomeAssertions;
using TUnit.Core;

namespace RepoQL.Aspire.Tests;

public class RepoQLDashboardLinkClientTests
{
    private static readonly Uri Origin = new("http://127.0.0.1:63333/");
    private const string KeyedLink = "http://127.0.0.1:63333/#k=not-a-real-key";

    private static RepoQLDashboardLinkClient.CliRunner Answers(string stdout, int exitCode = 0)
        => (_, _, _) => Task.FromResult((stdout, "", exitCode));

    [Test]
    public void Parse_AcceptsTheKeyedLinkOnTheRunsHost()
    {
        RepoQLDashboardLinkClient.Parse(KeyedLink + "\n", Origin).Should().Be(KeyedLink);
    }

    [Test]
    public void Parse_TakesTheLastLine()
    {
        RepoQLDashboardLinkClient.Parse("a note first\n" + KeyedLink + "\n", Origin).Should().Be(KeyedLink);
    }

    [Test]
    public void Parse_RejectsALinkToAnotherHost()
    {
        RepoQLDashboardLinkClient.Parse("http://127.0.0.1:9999/#k=abc", Origin).Should().BeNull();
    }

    [Test]
    public void Parse_RejectsAnythingButHttp()
    {
        RepoQLDashboardLinkClient.Parse("file:///etc/passwd", Origin).Should().BeNull();
        RepoQLDashboardLinkClient.Parse("javascript:alert(1)", Origin).Should().BeNull();
    }

    [Test]
    public void Parse_RejectsEmptyOrUnreadableOutput()
    {
        RepoQLDashboardLinkClient.Parse("", Origin).Should().BeNull();
        RepoQLDashboardLinkClient.Parse("Dashboard not available", Origin).Should().BeNull();
    }

    [Test]
    public void Arguments_AskForTheLinkWithoutOpeningABrowser()
    {
        RepoQLDashboardLinkClient.Arguments.Should().Equal("dashboard", "--url");
    }

    [Test]
    public async Task TryGetLink_ReturnsTheLink()
    {
        var link = await RepoQLDashboardLinkClient.TryGetLinkAsync(Answers(KeyedLink + "\n"), "/app", Origin, TimeSpan.FromSeconds(5), CancellationToken.None);

        link.Should().Be(KeyedLink);
    }

    [Test]
    public async Task TryGetLink_WhenTheCliFails_ReturnsNull()
    {
        var link = await RepoQLDashboardLinkClient.TryGetLinkAsync(Answers("", exitCode: 1), "/app", Origin, TimeSpan.FromSeconds(5), CancellationToken.None);

        link.Should().BeNull();
    }

    [Test]
    public async Task TryGetLink_WhenTheCliIsMissing_ReturnsNull()
    {
        RepoQLDashboardLinkClient.CliRunner missing = (_, _, _) => throw new InvalidOperationException("The 'rql' CLI was not found on PATH.");

        var link = await RepoQLDashboardLinkClient.TryGetLinkAsync(missing, "/app", Origin, TimeSpan.FromSeconds(5), CancellationToken.None);

        link.Should().BeNull();
    }

    [Test]
    public async Task TryGetLink_WhenTheCliHangs_GivesUpAtTheDeadline()
    {
        RepoQLDashboardLinkClient.CliRunner hangs = async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return ("", "", 0);
        };

        var link = await RepoQLDashboardLinkClient.TryGetLinkAsync(hangs, "/app", Origin, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        link.Should().BeNull();
    }

    [Test]
    public async Task TryGetLink_WhenTheApplicationStops_Propagates()
    {
        using var stopping = new CancellationTokenSource();
        RepoQLDashboardLinkClient.CliRunner hangs = async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return ("", "", 0);
        };

        var fetch = RepoQLDashboardLinkClient.TryGetLinkAsync(hangs, "/app", Origin, TimeSpan.FromSeconds(30), stopping.Token);
        await stopping.CancelAsync();

        var act = async () => await fetch;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
