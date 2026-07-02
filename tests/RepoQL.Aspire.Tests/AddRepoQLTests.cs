using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AwesomeAssertions;
using TUnit.Core;

namespace RepoQL.Aspire.Tests;

public class AddRepoQLTests
{
    [Test]
    public void AddRepoQL_AddsASingleWaitingResourceExcludedFromTheManifest()
    {
        var builder = DistributedApplication.CreateBuilder();

        var repoql = builder.AddRepoQL();

        repoql.Resource.Name.Should().Be("repoql");
        builder.Resources.OfType<RepoQLResource>().Should().ContainSingle();
        repoql.Resource.HasAnnotationOfType<ResourceSnapshotAnnotation>().Should().BeTrue();
        repoql.Resource.HasAnnotationOfType<ManifestPublishingCallbackAnnotation>().Should().BeTrue();
    }

    [Test]
    public void AddRepoQL_HonorsACustomName()
    {
        var builder = DistributedApplication.CreateBuilder();

        var repoql = builder.AddRepoQL("telemetry-brain");

        repoql.Resource.Name.Should().Be("telemetry-brain");
    }
}
