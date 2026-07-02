using Aspire.Hosting.ApplicationModel;
using RepoQL.Aspire;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for streaming an Aspire application's telemetry through a RepoQL workspace host.
/// </summary>
public static class RepoQLBuilderExtensions
{
    /// <summary>
    /// Adds a RepoQL resource to the application and streams every OTLP-enabled resource's telemetry
    /// through the RepoQL workspace host for this application's directory.
    /// </summary>
    /// <remarks>
    /// The host is discovered (or launched, detached) via the <c>rql</c> CLI. When no host can be
    /// reached the resource reports the failure and the application runs with stock Aspire telemetry
    /// wiring — adding RepoQL never introduces a new way for the application to fail.
    /// </remarks>
    public static IResourceBuilder<RepoQLResource> AddRepoQL(this IDistributedApplicationBuilder builder, string name = "repoql")
    {
        ArgumentNullException.ThrowIfNull(builder);

        var resource = new RepoQLResource(name);
        var resourceBuilder = builder.AddResource(resource)
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "RepoQL",
                State = KnownResourceStates.Waiting,
                Properties = [],
            })
            .WithIconName("Eye")
            .ExcludeFromManifest();

        if (builder.ExecutionContext.IsRunMode)
        {
            var appHostDirectory = builder.AppHostDirectory;
            var runName = builder.Environment.ApplicationName;
            builder.Eventing.Subscribe<BeforeStartEvent>((evt, cancellationToken) =>
                RepoQLWiring.WireAsync(resource, appHostDirectory, runName, evt, cancellationToken));
        }

        return resourceBuilder;
    }
}
