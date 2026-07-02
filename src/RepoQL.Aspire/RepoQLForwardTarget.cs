namespace RepoQL.Aspire;

/// <summary>
/// A downstream OTLP/HTTP endpoint the RepoQL host relays this run's telemetry to.
/// </summary>
/// <param name="Url">Absolute http(s) URL of the receiver (payloads go to its /v1/* paths).</param>
/// <param name="Headers">Headers for forwarded requests as "key=value;key2=value2", or null.</param>
internal sealed record RepoQLForwardTarget(string Url, string? Headers);
