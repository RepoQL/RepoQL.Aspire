using System.Runtime.ExceptionServices;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging.Abstractions;

namespace RepoQL.Aspire.Tests;

/// <summary>
/// Evaluates a resource's environment callbacks the way the orchestrator would
/// (mirrors Aspire.Hosting.TestUtilities' evaluator).
/// </summary>
internal static class EnvironmentVariableEvaluator
{
    public static async ValueTask<Dictionary<string, string>> GetEnvironmentVariablesAsync(
        IResource resource,
        DistributedApplicationOperation applicationOperation = DistributedApplicationOperation.Run)
    {
        var executionContext = new DistributedApplicationExecutionContext(
            new DistributedApplicationExecutionContextOptions(applicationOperation));

        var executionConfiguration = await ExecutionConfigurationBuilder.Create(resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(executionContext, NullLogger.Instance, CancellationToken.None);

        if (executionConfiguration.Exception is not null)
        {
            ExceptionDispatchInfo.Throw(executionConfiguration.Exception);
        }

        return executionConfiguration.EnvironmentVariables.ToDictionary();
    }
}
