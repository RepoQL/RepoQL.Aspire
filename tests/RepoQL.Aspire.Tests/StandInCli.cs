using System.ComponentModel;
using System.Diagnostics;

namespace RepoQL.Aspire.Tests;

/// <summary>
/// A POSIX shell script that stands in for the <c>rql</c> CLI, so tests exercise real process handling.
/// </summary>
internal sealed class StandInCli : IDisposable
{
    private readonly string _directory;

    private StandInCli(string directory, string body)
    {
        _directory = directory;
        File.WriteAllText(Path, $"#!/bin/sh\nhere=\"$(dirname \"$0\")\"\n{body}\n".ReplaceLineEndings("\n"));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>The script to run in place of <c>rql</c>.</summary>
    public string Path => System.IO.Path.Combine(_directory, "rql");

    /// <summary>A scratch directory to run it in.</summary>
    public string WorkingDirectory => _directory;

    /// <summary>The pid of the stand-in workspace host, once a hanging CLI has launched one.</summary>
    public int? HostPid => ReadPid("host.pid");

    /// <summary>A CLI that prints the given output and exits with <paramref name="exitCode"/>.</summary>
    public static StandInCli Answers(string stdout, string stderr, int exitCode)
    {
        var directory = CreateDirectory();
        File.WriteAllText(System.IO.Path.Combine(directory, "stdout"), stdout);
        File.WriteAllText(System.IO.Path.Combine(directory, "stderr"), stderr);
        return new StandInCli(directory, $"""
            cat "$here/stdout"
            cat "$here/stderr" >&2
            exit {exitCode}
            """);
    }

    /// <summary>
    /// A CLI that records its pid and never finishes. With <paramref name="launchesHost"/> it first starts a
    /// long-lived child on its own stdio, the way <c>rql watch env</c> launches a workspace host.
    /// </summary>
    public static StandInCli Hangs(bool launchesHost = false)
        => new(CreateDirectory(), launchesHost
            ? """
              sleep 120 </dev/null >/dev/null 2>&1 &
              echo $! > "$here/host.pid.tmp" && mv "$here/host.pid.tmp" "$here/host.pid"
              echo $$ > "$here/cli.pid.tmp" && mv "$here/cli.pid.tmp" "$here/cli.pid"
              exec sleep 120
              """
            : """
              echo $$ > "$here/cli.pid.tmp" && mv "$here/cli.pid.tmp" "$here/cli.pid"
              exec sleep 120
              """);

    /// <summary>
    /// Waits until a hanging CLI has started and returns its pid; if <paramref name="run"/> ends first, surfaces why.
    /// </summary>
    public async Task<int> WaitUntilHangingAsync(Task run)
    {
        var waited = Stopwatch.StartNew();
        int? pid;
        while ((pid = ReadPid("cli.pid")) is null)
        {
            if (run.IsCompleted)
            {
                await run;
                throw new InvalidOperationException("The run completed before the stand-in CLI started.");
            }

            if (waited.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException("The stand-in CLI did not start within 30 seconds.");

            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        return pid.Value;
    }

    public static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no process has this pid
        }
    }

    public void Dispose()
    {
        foreach (var pid in new[] { ReadPid("host.pid"), ReadPid("cli.pid") })
        {
            if (pid is not { } id || !IsRunning(id))
                continue;

            try
            {
                using var process = Process.GetProcessById(id);
                process.Kill();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // It exited in the meantime.
            }
        }

        Directory.Delete(_directory, recursive: true);
    }

    private static string CreateDirectory() => Directory.CreateTempSubdirectory("repoql-aspire-cli-").FullName;

    private int? ReadPid(string name)
    {
        var file = System.IO.Path.Combine(_directory, name);
        return File.Exists(file) && int.TryParse(File.ReadAllText(file), out var pid) ? pid : null;
    }
}
