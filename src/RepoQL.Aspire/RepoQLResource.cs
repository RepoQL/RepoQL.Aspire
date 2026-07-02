namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents the RepoQL workspace host that receives the application's OpenTelemetry stream.
/// </summary>
/// <remarks>
/// The rql host is shared workspace infrastructure with a lifetime independent of the AppHost:
/// it is adopted if already running, launched detached if not, and never stopped when the
/// application shuts down.
/// </remarks>
public sealed class RepoQLResource(string name) : Resource(name)
{
}
