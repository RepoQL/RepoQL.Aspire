<img src="https://raw.githubusercontent.com/RepoQL/RepoQL.Aspire/main/src/RepoQL.Aspire/icon.png" width="96" alt="RepoQL" align="right" />

# RepoQL.Aspire

Stream every Aspire resource's OpenTelemetry into a [RepoQL](https://repoql.com) workspace host with one call. Agents can query each run with SQL while you investigate, and the Aspire dashboard keeps its live view.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddRepoQL();

builder.AddProject<Projects.MyApi>("api");

builder.Build().Run();
```

That's the whole integration. On startup, every resource that would have exported OTLP to the Aspire dashboard exports to RepoQL instead; RepoQL records the stream and relays it byte-for-byte back to the dashboard. Both audiences see the same telemetry. The dashboard shows it live, and RepoQL keeps each run queryable for about six hours after the AppHost stops.

## What you get

- **A record that outlives the dashboard.** The Aspire dashboard holds telemetry in memory and forgets it on restart. RepoQL writes every span, log, and metric to a per-workspace DuckDB file. Restart the AppHost after a fix, and the previous run is still there for your agent to compare with the new one.
- **SQL over your telemetry.** `rql query "SELECT * FROM watch.summary()"` — or errors, span statistics, trace trees, and raw payloads. Ask `watch.surface` what's available.
- **Agent-ready.** Any agent with the RepoQL MCP server (or the `rql` CLI) can interrogate the run: what failed, what was slow, what changed between runs.
- **The dashboard keeps working.** Forwarding is a byte-verbatim OTLP/HTTP relay. Traces, structured logs, and metrics appear in the Aspire dashboard exactly as if the apps exported directly.
- **A resource tile.** `AddRepoQL()` adds a `repoql` resource to the dashboard with the run id and a link that opens the RepoQL dashboard. The link carries the dashboard's access key, fetched with `rql dashboard --url`, so treat it like the link `rql dashboard` opens. The package never logs it.

## Requirements

- [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/) 13.4+
- The `rql` CLI on `PATH` (or point `REPOQL_CLI_PATH` at the binary) — install it from [github.com/RepoQL/RepoQL](https://github.com/RepoQL/RepoQL). The AppHost adopts the workspace's running RepoQL host, or launches one automatically, as `rql query` does. The AppHost's directory must sit inside a git repository or a directory where you have run `rql init`.

Some rql releases, 1.7.9 among them, cannot launch a host for the AppHost. They report "No host is running for this repository", and a host that no RepoQL client is using can stop partway through a run. If you see that message, update rql, or run `rql serve` in the workspace before you start the AppHost.

If `rql` is missing or no host can be reached, the `repoql` resource reports the failure and the application starts normally with stock Aspire telemetry wiring — adding RepoQL never introduces a new way for your application to fail.

## How long the host stays up

The AppHost shares the workspace's RepoQL host with any agent that uses it. While your resources export telemetry, the host stays up. After they stop, a host that `rql` launched exits once it has been idle for its idle window — 15 minutes when the AppHost launched it — unless an agent is still connected. A host you started with `rql serve` stays up until you stop it.

## How it works

Everything happens in run mode only — local development. `aspire publish` output is untouched: the `repoql` resource is excluded from the manifest and no deployed environment is rewired.

At `BeforeStartEvent`, the package runs `rql watch env` to register a telemetry run against the workspace host, then rewrites each OTLP-enabled resource's environment:

| Variable | Value |
|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | the RepoQL collector |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `http/protobuf` |
| `OTEL_EXPORTER_OTLP_HEADERS` | the run-routing header |
| `OTEL_RESOURCE_ATTRIBUTES` | `repoql.watch.run_id=<id>` appended |

The host captures every payload, then forwards it to the dashboard's OTLP/HTTP endpoint. Capture is sacred; forwarding is best-effort — if the dashboard is down, capture continues and the failure is counted, never silent:

```sql
SELECT * FROM watch.forward_stats;
-- run_id · target_url · forwarded · dropped · failed · last_error
```

### Dashboard forwarding needs the HTTP OTLP endpoint

Aspire's default dashboard OTLP endpoint speaks gRPC, which cannot receive the HTTP relay. Give the dashboard an HTTP ingestion endpoint in the AppHost's `launchSettings.json`:

```json
"ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL": "http://localhost:19201"
```

Without it, telemetry still streams to RepoQL — the package logs what to add and the dashboard's telemetry panes stay empty.

## Querying a run

```bash
rql query "SELECT * FROM watch.summary()"          # every run, newest first
rql query "SELECT * FROM watch.errors('<run-id>')" # grouped exceptions
rql query "SELECT * FROM watch.span_stats('<run-id>')"
rql query "SELECT * FROM watch.surface"            # everything queryable
```

## How long RepoQL keeps a run

The workspace host deletes old telemetry when it starts and once an hour after that. These rules decide what it deletes:

- **About six hours after the AppHost stops.** When the AppHost shuts down, the package marks its run complete. The host deletes a run once it has been complete for six hours.
- **The same if the AppHost dies without shutting down.** A debugger stop, `kill -9`, or a crash leaves the run open. The host expires an open run six hours after its last telemetry and deletes it in the same sweep.
- **No age limit while telemetry flows.** A run stays open while its resources keep sending telemetry, however long the AppHost runs. If every resource goes quiet for six hours, the host expires the run even though the AppHost is still running. After that, RepoQL refuses the run's telemetry, so the dashboard stops receiving it too. Restart the AppHost to start a new run.
- **A 512 MiB cap on the whole store.** If the store is still larger than 512 MiB after expired runs are deleted, the host moves it aside and starts an empty one. Queries no longer see telemetry recorded before then, even for a run that is still going.

## License

MIT
