using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace RepoQL.Aspire;

/// <summary>
/// Registers a telemetry run against the workspace's RepoQL host by invoking <c>rql watch env</c>.
/// </summary>
/// <remarks>
/// <c>rql watch env</c> owns discovery end to end: it finds the host for the working directory, launches one
/// when none is running (as <c>rql query</c> does), registers the run, and prints the composed OTLP environment
/// as JSON on stdout. Some rql releases, 1.7.9 among them, never launch a host from <c>rql watch env</c>. They
/// fail with "No host is running for this repository", and the AppHost falls back to stock telemetry wiring.
/// </remarks>
internal static class RepoQLWatchEnvClient
{
    private const string CliPathVariable = "REPOQL_CLI_PATH";
    private const string CollectorPathSuffix = "/api/otel";

    public static async Task<RepoQLWatchEnvResult> RegisterRunAsync(
        string workingDirectory,
        string runName,
        RepoQLForwardTarget? forward,
        CancellationToken cancellationToken)
    {
        var (stdout, stderr, exitCode) = await RunAsync(workingDirectory, BuildArguments(runName, forward), cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"'rql watch env' exited with code {exitCode}. {FailureReason(stderr)}");
        }

        return Parse(stdout);
    }

    internal static IReadOnlyList<string> BuildArguments(string runName, RepoQLForwardTarget? forward)
    {
        var arguments = new List<string> { "watch", "env", "--name", runName, "--format", "json" };
        if (forward is not null)
        {
            arguments.Add("--forward");
            arguments.Add(forward.Url);
            if (forward.Headers is { Length: > 0 })
            {
                arguments.Add("--forward-headers");
                arguments.Add(forward.Headers);
            }
        }

        return arguments;
    }

    internal static RepoQLWatchEnvResult Parse(string stdout)
    {
        if (JsonNode.Parse(stdout) is not JsonObject json)
            throw new InvalidOperationException("'rql watch env --format json' did not return a JSON object.");

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in json)
        {
            if (value is not null)
                environment[key] = value.GetValue<string>();
        }

        if (!environment.TryGetValue("REPOQL_WATCH_RUN_ID", out var runId) || string.IsNullOrWhiteSpace(runId))
            throw new InvalidOperationException("'rql watch env' output is missing REPOQL_WATCH_RUN_ID.");

        if (!environment.TryGetValue("OTEL_EXPORTER_OTLP_ENDPOINT", out var endpoint)
            || !endpoint.EndsWith(CollectorPathSuffix, StringComparison.Ordinal)
            || !Uri.TryCreate(endpoint[..^CollectorPathSuffix.Length], UriKind.Absolute, out var baseUrl))
        {
            throw new InvalidOperationException(
                "'rql watch env' output is missing a collector endpoint of the form <base>/api/otel.");
        }

        return new RepoQLWatchEnvResult(environment, runId, baseUrl);
    }

    internal static async Task<(string Stdout, string Stderr, int ExitCode)> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        // The first launch may bring up a workspace host; allow for that.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));

        Exception? lastError = null;
        foreach (var executable in CandidateExecutables())
        {
            Process? process;
            try
            {
                process = Process.Start(CreateStartInfo(executable, workingDirectory, arguments));
            }
            catch (Win32Exception ex)
            {
                lastError = ex;
                continue; // not found at this candidate — try the next
            }

            if (process is null)
            {
                continue;
            }

            using (process)
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return (await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false), process.ExitCode);
            }
        }

        throw new InvalidOperationException(
            $"The 'rql' CLI was not found on PATH, at ~/.local/bin/rql, or via {CliPathVariable}. " +
            "Install RepoQL (https://repoql.ai) or set REPOQL_CLI_PATH to the rql binary.",
            lastError);
    }

    private static ProcessStartInfo CreateStartInfo(string executable, string workingDirectory, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        return info;
    }

    private static IEnumerable<string> CandidateExecutables()
    {
        var overridePath = Environment.GetEnvironmentVariable(CliPathVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
            yield return overridePath;

        var binaryName = OperatingSystem.IsWindows() ? "rql.exe" : "rql";
        yield return binaryName; // PATH resolution

        // IDE-launched AppHosts often have a sparse PATH; probe the default install location.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            yield return Path.Combine(home, ".local", "bin", binaryName);
    }

    /// <summary>
    /// Picks the line of the CLI's stderr that says why it failed.
    /// </summary>
    /// <remarks>
    /// rql writes its failure last, after bracketed progress lines such as <c>[rql] Launching host: …</c> or the
    /// <c>[host …]</c> lines it relays from a host it is starting. A failure can span several lines, and its first
    /// line states the cause. So the reason is the first line that is not bracketed progress, or the last line when
    /// every line is progress.
    /// </remarks>
    internal static string FailureReason(string stderr)
    {
        var reason = string.Empty;
        foreach (var line in stderr.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            if (!trimmed.StartsWith('['))
                return trimmed;
            reason = trimmed;
        }

        return reason;
    }
}
