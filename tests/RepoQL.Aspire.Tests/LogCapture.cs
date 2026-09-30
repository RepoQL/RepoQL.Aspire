using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace RepoQL.Aspire.Tests;

/// <summary>
/// Collects every log entry so tests can assert on what the package tells the user.
/// </summary>
internal sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyCollection<LogEntry> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new Logger(_entries, categoryName);

    public void Dispose()
    {
    }

    internal sealed record LogEntry(LogLevel Level, string Category, string Message);

    private sealed class Logger(ConcurrentQueue<LogEntry> entries, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => entries.Enqueue(new LogEntry(logLevel, category, formatter(state, exception)));
    }
}
