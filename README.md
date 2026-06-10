# Klassd.Workflows

A Hangfire-style background-job system whose **executor runs each job in its own
Kubernetes pod**. Jobs are plain C# classes implementing a small interface; a
web dashboard shows live progress/console output, lets you start/stop jobs, and
recurring jobs are registered through code with cron expressions.

> ⚠️ **Beta.** Klassd.Workflows is in public beta (`0.x`). It works and is tested, but the API
> surface may change between releases until `1.0`. Pin your versions when upgrading.

## Yes — and it's already running

This repo is a working skeleton. Out of the box it runs jobs as **local
processes** (no cluster needed) and can switch to the **Kubernetes executor**
with one config setting.

## Install

The engine ships as NuGet packages — install the core plus the adapters you need (add
`--prerelease` while in beta):

```bash
dotnet add package Klassd.Workflows.Core --prerelease
dotnet add package Klassd.Workflows.Storage.Postgres --prerelease   # durable store (or .Storage.MongoDb)
dotnet add package Klassd.Workflows.Kubernetes --prerelease         # K8s executor (omit for local only)
dotnet add package Klassd.Workflows.Artifacts.S3 --prerelease       # artifact store (or .Artifacts.Gcs)
```

| Package | NuGet |
|---|---|
| `Klassd.Workflows.Abstractions` | The contract: `IJob`, `IJobContext`, `IArtifactStore`, worker protocol (no deps). |
| `Klassd.Workflows.Core` | Scheduler, in-memory store, cron loop, DAG orchestrator, local-process executor. |
| `Klassd.Workflows.Kubernetes` | `KubernetesJobExecutor` — one `batch/v1` Job per execution. |
| `Klassd.Workflows.Storage.Postgres` / `.Storage.MongoDb` | Durable `IJobStore` adapters. |
| `Klassd.Workflows.Artifacts.S3` / `.Artifacts.Gcs` | `IArtifactStore` adapters (large payloads). |

The core carries **no** Kubernetes/AWS/Google/Mongo/Npgsql dependency — each adapter keeps its SDK
isolated, so you only pull in what you wire up. The `Dashboard` and `Worker` projects in this repo
are reference hosts (not shipped as packages): copy them, or build the worker into your own image.

## Projects

| Project | Role |
|---|---|
| `Klassd.Workflows.Abstractions` | The contract jobs implement: `IJob`, `IJobContext`, and the worker stdout protocol. |
| `Klassd.Workflows.Core` | Scheduler, in-memory store, cron recurring loop (Cronos), job catalog, and the **local-process executor**. |
| `Klassd.Workflows.Kubernetes` | `KubernetesJobExecutor` — creates a K8s `Job` per run and tails the pod logs. |
| `Klassd.Workflows.Worker` | The executor-pod entrypoint. Loads the `IJob` by name, runs it, streams log/progress/state to stdout. |
| `Klassd.Workflows.Dashboard` | Blazor Server UI: live job list, per-job console + progress, recurring jobs, DAG runs. Hosts the scheduler. |
| `Klassd.Workflows.Storage.Postgres` | Durable `IJobStore` on PostgreSQL (jsonb documents + append-only logs). |
| `Klassd.Workflows.Storage.MongoDb` | Durable `IJobStore` on MongoDB. |
| `Klassd.Workflows.Artifacts.Gcs` | `IArtifactStore` on Google Cloud Storage (provider name `gcs`). |
| `Klassd.Workflows.Artifacts.S3` | `IArtifactStore` on S3 / S3-compatible stores (provider name `s3`). |
| `samples/Klassd.Workflows.SampleJobs` | Example jobs + the `catalog-integration` DAG. |

## How it works

```
Dashboard (scheduler)                         Executor pod / process
─────────────────────                         ──────────────────────
IJobScheduler.Enqueue ──► IJobExecutor.Start ─► Worker loads IJob by type name
        ▲                                         │ runs RunAsync(context)
        │  store updates                          │ writes ##LOG##/##PROGRESS##/##STATE##
        └── tails stdout / pod logs ◄─────────────┘   to stdout
```

The **same worker** runs locally and in Kubernetes — only the executor that
launches it differs. Communication is a line protocol on stdout
(`WorkerProtocol`), so Kubernetes pod logs are the transport for free.

### Defining a job

```csharp
public sealed class MyJob : IJob
{
    public async Task RunAsync(IJobContext ctx)
    {
        ctx.Log("starting");
        ctx.ReportProgress(50, "halfway");
        await Task.Delay(1000, ctx.CancellationToken);
        ctx.Log("done");
    }
}
```

### Scheduling through code

```csharp
scheduler.AddOrUpdateRecurring<MyJob>("nightly", "0 2 * * *");   // cron
await scheduler.EnqueueAsync<MyJob>();                            // fire now
```

### Pod resources (requests / limits)

Set them two ways; they merge **field-by-field**, lowest to highest precedence:

1. `DefaultResources` — global fallback in appsettings.
2. `[JobResources]` attribute — the baseline a job author ships with the code.
3. `Klassd.Workflows:Resources:<key>` — per-job appsettings override (key = full type
   name, short name also matched). **Config wins**, so Vault-injected settings
   retune any field without a recompile.

```csharp
[JobResources(CpuRequest = "250m", CpuLimit = "1", MemoryRequest = "256Mi", MemoryLimit = "512Mi")]
public sealed class ReportGenerationJob : IJob { /* ... */ }
```

```json
// appsettings.json — override just the memory limit, keep the attribute's CPU
"Klassd.Workflows": {
  "DefaultResources": { "CpuRequest": "100m", "CpuLimit": "500m", "MemoryRequest": "128Mi", "MemoryLimit": "256Mi" },
  "Resources": {
    "Klassd.Workflows.SampleJobs.ReportGenerationJob": { "MemoryLimit": "1Gi" }
  }
}
```

For `ReportGenerationJob` above the resolved pod resources become
`cpu 250m/1`, `memory 256Mi/1Gi`. The values land on the worker container's
`resources.requests`/`resources.limits`.

#### Running with no limits

`null`/absent means "inherit the lower layer". To *clear* a value a lower layer
set, use the sentinel `"none"` (or `""`):

```jsonc
"Resources": {
  // keep a CPU request but drop the CPU limit (a recommended K8s pattern)
  "MyJobs.LatencySensitiveJob": { "CpuLimit": "none" },

  // no requests or limits at all, even though a global default exists
  "MyJobs.BestEffortJob": { "Unconstrained": true }
}
```

Or on the job itself:

```csharp
[JobResources(Unconstrained = true)]            // never constrained
[JobResources(CpuRequest = "250m", CpuLimit = "none")]  // request only, no limit
```

When everything resolves to empty, no `resources` block is emitted at all — the
pod runs unconstrained.

## Workflows (DAGs)

Jobs can be composed into a DAG that fans out, waits on dependencies, and passes
data between nodes — the same shape as the Argo `CatalogIntegration` workflow.
The **orchestrator runs in the scheduler**; each node runs as a normal worker
pod/process via the executor, so every node has its own live console.

Define a DAG in code:

```csharp
registry.Register(new WorkflowBuilder("catalog-integration")
    .Add<MarketFinderJob>("markets")                       // root: emits "market_ids" (JSON array)
    .Add<DataProxyJob>("data-proxy")                       // parallel root: writes a dataset artifact
    .Add<IntegrationJob>("integration", n => n
        .DependsOn("markets", "data-proxy")
        .FanOutOver("markets", "market_ids", itemArgument: "market"))  // one pod per market
    .Add<PublishJob>("publish", n => n
        .DependsOn("integration")
        .WithRetries(2))                                   // retry on failure
    .Add<FinalizerJob>("finalizer", n => n
        .DependsOn("publish", "data-proxy")
        .BindInput("dataset_ref", "data-proxy", "dataset_ref"))  // reads the artifact
    .Add<NotifyJob>("notify",   n => n.DependsOn("data-proxy").When("data-proxy", "status", "ok"))
    .Add<RollbackJob>("rollback", n => n.DependsOn("data-proxy").When("data-proxy", "status", "failed"))
    .Build());
```

Jobs publish outputs and read inputs through the same `IJobContext`:

```csharp
context.SetOutput("market_ids", JsonSerializer.Serialize(markets));   // upstream
var market = context.Arguments.GetValueOrDefault("market");           // fanned-out item
```

Semantics (mirroring Argo):

- **dependencies** — a node starts only once all its dependencies are *satisfied*
  (succeeded or benignly omitted); if a dependency fails, dependents are
  *skipped* and the run fails.
- **fan-out (`withParam`)** — `FanOutOver(source, outputKey, item)` reads the
  source node's output as a JSON array and starts one execution per element,
  exposed as the `item` argument. The node joins when all fan-out executions finish.
- **inputs (`BindInput`)** — bind an argument to an upstream node's output
  (`"sourceNode.outputKey"`).
- **retries** — `WithRetries(n)` re-runs a failed execution up to `n` times
  (per fan-out item). Each attempt gets its 0-based index in the `__attempt`
  argument. The graph marks retried nodes with `↻`.
- **conditions (`when`)** — `When(node, key, expected)` or `When(outputs => …)`
  runs the node only if the predicate over upstream outputs holds; otherwise the
  node is **omitted** (benign — dependents still run, the run can still succeed).
- **artifacts** — large payloads go through `context.Artifacts` (an
  `IArtifactStore`) instead of env-sized outputs. A node saves an artifact,
  publishes the small reference as an output, and a downstream node loads it.
  The filesystem store works locally and on a shared RWX volume; swap in an
  object-storage implementation for production. Configure the directory via the
  executor's `ArtifactDir`.
- **recurring workflows** — `scheduler.AddOrUpdateRecurringWorkflow(id, name, cron)`
  fires a DAG run on a schedule, alongside recurring single jobs.

Run it from the **Workflows** page (Run button) and watch the run on
`/workflows/runs/{id}`: a server-rendered SVG DAG laid out by dependency level,
status-colored nodes, and a click on any node shows its execution(s) — including
each fanned-out pod — with live console output.

Start a run from code with `scheduler.EnqueueWorkflowAsync("catalog-integration")`.

## Storage & database adapters

Two pluggable seams. Both ship built-in implementations and are open for more.

### Database (the `IJobStore`)

Holds executions, recurring entries and workflow runs. Selected on the scheduler
via the builder returned by `AddKlassdWorkflowsCore()`:

```csharp
var workflows = builder.Services.AddKlassdWorkflowsCore();
workflows.UsePostgres("Host=…;Database=…;Username=…;Password=…");  // Klassd.Workflows.Storage.Postgres
// or
workflows.UseMongo("mongodb://…", database: "klassd_workflows");    // Klassd.Workflows.Storage.MongoDb
```

Or by config in the dashboard (`appsettings` / Vault):

```json
"Klassd.Workflows": {
  "Store": "postgres",
  "Postgres": { "ConnectionString": "Host=…;Database=…;Username=…;Password=…" }
}
```

Default is in-memory. Both DB adapters store each entity as a JSON document and
keep logs in an append-only table/collection; change events are raised in-process
(single scheduler instance — bridge to LISTEN/NOTIFY / change streams for HA).

### Storage (the `IArtifactStore`)

Holds large payloads passed between nodes. The **worker** selects a provider by
name at runtime (config-driven), discovering every `IArtifactStoreProvider` on
its load path — so the choice is per-deployment, not compiled in:

```jsonc
// Kubernetes executor options (bound from the Klassd.Workflows config section)
"ArtifactProvider": "gcs",
"ArtifactSettings": { "bucket": "my-bucket", "prefix": "klassd/" }
// s3:   { "bucket": "...", "region": "eu-north-1" }  (or "serviceUrl" for MinIO)
// file: { "dir": "/mnt/artifacts" }  (default; needs a shared RWX volume in K8s)
```

Built-in providers: `file` (Core), `gcs`, `s3`. Credentials come from the
platform's default chain (workload identity / IRSA / env).

## Extending with your own adapters

- **Custom database**: implement `IJobStore`, then `workflows.UseJobStore<MyStore>()`
  (or `UseJobStore(sp => …)`). Optionally ship a `UseMyDb(this WorkflowsBuilder)`
  extension method, exactly like the Postgres/Mongo packages do.
- **Custom storage**: implement `IArtifactStore` plus an `IArtifactStoreProvider`
  with a unique `Name`, in any assembly the worker references. Select it by setting
  `ArtifactProvider` to that name — no worker changes. (This is how `gcs`/`s3` work.)

## Run locally (no cluster)

```bash
dotnet build
dotnet run --project src/Klassd.Workflows.Dashboard
# open the printed URL — Jobs + Recurring pages
```

The dashboard's local executor launches `Klassd.Workflows.Worker.dll` as a child
process per job. (Build the solution first so the worker output exists.)

## Run on Kubernetes

```bash
# 1. Build & push the worker image (from repo root)
docker build -f src/Klassd.Workflows.Worker/Dockerfile -t <registry>/klassd-workflows-worker:latest .
docker push <registry>/klassd-workflows-worker:latest

# 2. If the dashboard runs in-cluster, grant it RBAC
kubectl apply -f deploy/rbac.yaml

# 3. Point the dashboard at the K8s executor (config / env)
#    Klassd.Workflows__Executor=kubernetes
#    Klassd.Workflows__WorkerImage=<registry>/klassd-workflows-worker:latest
#    Klassd.Workflows__Namespace=default
#    Klassd.Workflows__InCluster=true        # when the dashboard itself runs in K8s
```

Each enqueued job becomes a `batch/v1` Job (one pod, `restartPolicy: Never`,
`backoffLimit: 0`), garbage-collected via `ttlSecondsAfterFinished`. Stopping a
job deletes its Job; SIGTERM cancels the worker's `CancellationToken`.

## Status / next steps

Working: interfaces, scheduler, cron, workflow DAGs (dependencies, fan-out,
output passing, retries, conditional `when` nodes, artifacts, cron-triggered
runs) with an SVG run view, durable stores (in-memory / Postgres / MongoDB),
object-storage artifacts (file / GCS / S3), user-pluggable store + storage
adapters, local + Kubernetes executors, live dashboard. Production hardening to
add later:

- Distributed locking so multiple scheduler replicas don't double-fire cron,
  and bridging store change-events to LISTEN/NOTIFY / change streams for HA.
- Retry backoff/delay (retries are immediate today).
- AuthN/AuthZ on the dashboard.

## License

[MIT](LICENSE) © Mark Lonquist
