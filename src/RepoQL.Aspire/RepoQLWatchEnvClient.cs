using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace RepoQL.Aspire;

/// <summary>
/// Registers a telemetry run against the workspace's RepoQL host by invoking <c>rql watch env</c>.
/// </summary>
/// <remarks>
/// <c>rql watch env</c> owns discovery end to end: it finds the host for the working directory,
/// auto-launches one when none is running (the same path the MCP client uses), registers the run,
/// and prints the composed OTLP environment as JSON on stdout.
/// </remarks>
internal static class RepoQLWatchEnvClient
{
    private const string CliPathVariable = "REPOQL_CLI_PATH";
    private const string CollectorPathSuffix = "/api/otel";

    public static async Task<RepoQLWatchEnvResult> RegisterRunAsync(
        string workingDirectory,
        string runName,
        CancellationToken cancellationToken)
    {
        var (stdout, stderr, exitCode) = await RunAsync(workingDirectory, runName, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"'rql watch env' exited with code {exitCode}. {FirstLine(stderr)}");
        }

        return Parse(stdout);
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

    private static async Task<(string Stdout, string Stderr, int ExitCode)> RunAsync(
        string workingDirectory,
        string runName,
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
                process = Process.Start(CreateStartInfo(executable, workingDirectory, runName));
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

    private static ProcessStartInfo CreateStartInfo(string executable, string workingDirectory, string runName)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("watch");
        info.ArgumentList.Add("env");
        info.ArgumentList.Add("--name");
        info.ArgumentList.Add(runName);
        info.ArgumentList.Add("--format");
        info.ArgumentList.Add("json");
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

    private static string FirstLine(string text)
    {
        var trimmed = text.AsSpan().TrimStart();
        var newline = trimmed.IndexOfAny('\r', '\n');
        return (newline < 0 ? trimmed : trimmed[..newline]).ToString();
    }
}
