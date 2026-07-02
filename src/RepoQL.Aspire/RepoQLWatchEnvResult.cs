namespace RepoQL.Aspire;

/// <summary>
/// The registered watch run returned by <c>rql watch env</c>.
/// </summary>
internal sealed record RepoQLWatchEnvResult(
    IReadOnlyDictionary<string, string> Environment,
    string RunId,
    Uri BaseUrl);
