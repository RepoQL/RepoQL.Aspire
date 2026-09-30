namespace RepoQL.Aspire;

/// <summary>
/// Fetches the RepoQL dashboard's authenticated link by invoking <c>rql dashboard --url</c>.
/// </summary>
/// <remarks>
/// The host keeps the dashboard's access key out of SQL, logs, and command results; <c>rql dashboard --url</c> is
/// the local surface that hands the keyed link to scripts. The key rides in the link's fragment, so this package
/// never logs the link. Fetching it is best effort: on any failure the resource links to the host's plain origin,
/// where the dashboard asks for a link from <c>rql dashboard</c>.
/// </remarks>
internal static class RepoQLDashboardLinkClient
{
    internal delegate Task<(string Stdout, string Stderr, int ExitCode)> CliRunner(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);

    internal static readonly IReadOnlyList<string> Arguments = ["dashboard", "--url"];

    // The link is a convenience, so a slow CLI must not hold up the application's start for long.
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public static Task<string?> TryGetLinkAsync(string workingDirectory, Uri origin, CancellationToken cancellationToken)
        => TryGetLinkAsync(RepoQLWatchEnvClient.RunAsync, workingDirectory, origin, DefaultTimeout, cancellationToken);

    /// <summary>
    /// Returns the keyed dashboard link for the host at <paramref name="origin"/>, or null when it cannot be fetched.
    /// Only the application's own cancellation propagates.
    /// </summary>
    internal static async Task<string?> TryGetLinkAsync(
        CliRunner run,
        string workingDirectory,
        Uri origin,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var (stdout, _, exitCode) = await run(workingDirectory, Arguments, deadline.Token).ConfigureAwait(false);
            return exitCode == 0 ? Parse(stdout, origin) : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Accepts the last line of <c>rql dashboard --url</c> output when it is an http(s) link to the same host as
    /// <paramref name="origin"/> — the host this application's telemetry streams to — and nothing else.
    /// </summary>
    internal static string? Parse(string stdout, Uri origin)
    {
        var line = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        if (line is null || !Uri.TryCreate(line, UriKind.Absolute, out var link))
            return null;
        if (link.Scheme != Uri.UriSchemeHttp && link.Scheme != Uri.UriSchemeHttps)
            return null;
        return string.Equals(
            link.GetLeftPart(UriPartial.Authority),
            origin.GetLeftPart(UriPartial.Authority),
            StringComparison.OrdinalIgnoreCase)
            ? line
            : null;
    }
}
