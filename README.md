<img src="https://raw.githubusercontent.com/RepoQL/RepoQL.Aspire/main/src/RepoQL.Aspire/icon.png" width="96" alt="RepoQL" align="right" />

# RepoQL.Aspire

Stream every Aspire resource's OpenTelemetry into a [RepoQL](https://repoql.com) workspace host with one call — durable, SQL-queryable telemetry for agents, while the Aspire dashboard keeps its live view.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddRepoQL();

builder.AddProject<Projects.MyApi>("api");

builder.Build().Run();
```

That's the whole integration. On startup, every resource that would have exported OTLP to the Aspire dashboard exports to RepoQL instead; RepoQL records the stream durably and relays it byte-for-byte back to the dashboard. Both audiences see the same telemetry — the dashboard live, RepoQL forever.

## What you get

- **A durable record.** The Aspire dashboard holds telemetry in memory and forgets on restart. RepoQL writes every span, log, and metric to a per-workspace DuckDB you can query days later.
- **SQL over your telemetry.** `rql query "SELECT * FROM watch.summary()"` — or errors, span statistics, trace trees, and raw payloads. Ask `watch.surface` what's available.
- **Agent-ready.** Any agent with the RepoQL MCP server (or the `rql` CLI) can interrogate the run: what failed, what was slow, what changed between runs.
- **The dashboard keeps working.** Forwarding is a byte-verbatim OTLP/HTTP relay. Traces, structured logs, and metrics appear in the Aspire dashboard exactly as if the apps exported directly.
- **A resource tile.** `AddRepoQL()` adds a `repoql` resource to the dashboard with the run id and a link to the RepoQL dashboard.

## Requirements

- [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/) 13.4+
- The `rql` CLI on `PATH` (or point `REPOQL_CLI_PATH` at the binary) — install it from [repoql.com](https://repoql.com). The AppHost adopts the workspace's running RepoQL host, or launches one automatically — the same discovery the RepoQL MCP client uses.

If `rql` is missing or no host can be reached, the `repoql` resource reports the failure and the application starts normally with stock Aspire telemetry wiring — adding RepoQL never introduces a new way for your application to fail.

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

When the AppHost stops, the run completes cleanly and remains queryable.

## License

MIT
