# Klassd.Workflows

A Hangfire-style background-job system whose **executor runs each job in its own
Kubernetes pod**. Jobs are plain C# classes implementing a small interface; a
web dashboard shows live progress/console output, lets you start/stop jobs, and
recurring jobs are registered through code with cron expressions.

> ⚠️ **Beta.** Klassd.Workflows is in public beta (`0.x`). It works and is tested, but the API
> surface may change between releases until `1.0`. Pin your versions when upgrading.

> 📖 **Recipes.** Looking for *"I want behaviour X — what do I write?"* See
> [`docs/recipes.md`](docs/recipes.md): per-execution & global DI, inputs, progress, output/artifact
> passing, fan-out, conditions, retries, container/service nodes, scheduling, resources, cancellation.

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
dotnet add package Klassd.Workflows.Dashboard --prerelease          # the live UI (Razor Class Library)
```

| Package | NuGet |
|---|---|
| `Klassd.Workflows.Abstractions` | The contract: `IJob`, `IJobContext`, `IArtifactStore`, worker protocol (no deps). |
| `Klassd.Workflows.Core` | Scheduler, in-memory store, cron loop, DAG orchestrator, local-process executor. |
| `Klassd.Workflows.Kubernetes` | `KubernetesJobExecutor` — one `batch/v1` Job per execution. |
| `Klassd.Workflows.Storage.Postgres` / `.Storage.MongoDb` | Durable `IJobStore` adapters. |
| `Klassd.Workflows.Artifacts.S3` / `.Artifacts.Gcs` | `IArtifactStore` adapters (large payloads). |
| `Klassd.Workflows.Worker` | The worker host: `WorkerHost.CreateBuilder(args).RegisterJobs(…).RunAsync()` constructs + runs the dispatched `IJob`. Reference it from your own exe to build a worker image. |
| `Klassd.Workflows.Dashboard` | The Blazor (Interactive Server) UI as a Razor Class Library — mount it into your host. |

The core carries **no** Kubernetes/AWS/Google/Mongo/Npgsql dependency — each adapter keeps its SDK
isolated, so you only pull in what you wire up. **`Klassd.Workflows.Worker` ships as a package**:
reference it from a thin exe, register the jobs it can run, and publish it as your own worker image
(see *Build your own worker image*). The **`Dashboard` also ships
as a package** (RCL) — mount it into any ASP.NET Core host:

```csharp
builder.Services.AddHttpContextAccessor();          // the dashboard reads a theme cookie during SSR
builder.Services.AddKlassdWorkflowsCore();           // scheduler + store
builder.Services.AddLocalExecutor(workerDllPath);    // or AddKubernetesExecutor(...)
builder.Services.AddKlassdWorkflowsDashboard();      // the UI

var app = builder.Build();
app.UseAntiforgery();
app.MapKlassdWorkflowsDashboard();                   // static assets + Razor component endpoints
app.Run();
```

The host needs `<RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>` in its csproj if it has no
`.razor` of its own (otherwise `_framework/blazor.web.js` 404s). `samples/Klassd.Workflows.DashboardHost`
is a complete, runnable example.

### Authentication

Dashboard sign-in is powered by the standalone [**Klassd.Auth**](https://github.com/getklassd/Klassd.Auth)
suite. Add it with two lines:

```csharp
builder.Services.AddKlassdWorkflowsAuth(o =>
{
    o.SeedAdminEmail = "admin@example.com";
    o.SeedAdminPassword = builder.Configuration["Seed:AdminPassword"];
    o.BypassOnLoopback = true;          // no login on localhost / kubectl port-forward (dev)
});
// ...
app.UseKlassdWorkflowsAuth();            // auth middleware + /auth/login, /auth/logout, /auth/external/{scheme}
```

Email/password admins are managed in the dashboard's **Users** area; SSO is added with
`AddKlassdWorkflowsOpenIdConnect(...)` (a thin wrapper over `Klassd.Auth.OpenIdConnect`). Users live
in the Klassd.Auth store, sharing your chosen storage adapter's database.

## Quickstart

A working jobs service with the live dashboard in four steps. (For dev you can stop after step 3 —
the local executor needs no cluster.)

**1. Define a job** — a plain class implementing `IJob`. Declare manual-start inputs with
`[JobInput]` and pod resources with `[JobResources]` (both optional):

```csharp
using Klassd.Workflows.Abstractions;

[JobInput("name", Default = "world")]
public sealed class HelloJob : IJob
{
    public async Task RunAsync(IJobContext ctx)
    {
        var who = ctx.Arguments.GetValueOrDefault("name", "world");
        for (var i = 1; i <= ctx.WithProgress(5); i++)   // inline progress bar in the console
        {
            ctx.Log($"hello {who} {i}/5");
            await Task.Delay(1000, ctx.CancellationToken);
        }
    }
}
```

**2. Host: wire the scheduler, an executor, and the dashboard** (`Program.cs`):

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();            // dashboard reads a theme cookie during SSR
var workflows = builder.Services.AddKlassdWorkflowsCore();
workflows.UsePostgres(builder.Configuration.GetConnectionString("Jobs")!);   // or .UseMongo(...) / in-memory

builder.Services.AddLocalExecutor(workerDllPath);     // dev: child process per job
// builder.Services.AddKubernetesExecutor(builder.Configuration.GetSection("Klassd.Workflows")); // prod
builder.Services.AddKlassdWorkflowsDashboard();

var app = builder.Build();
app.UseAntiforgery();
app.MapKlassdWorkflowsDashboard();

// schedule + enqueue from anywhere
var scheduler = app.Services.GetRequiredService<IJobScheduler>();
scheduler.AddOrUpdateRecurring<HelloJob>("nightly", "0 2 * * *");
await scheduler.EnqueueAsync<HelloJob>(new() { ["name"] = "team" });

app.Run();
```

Add `<RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>` to the host csproj (it has no `.razor`
of its own). Open the host URL → **Home / Jobs / Runs**.

**3. Run locally** — `dotnet run` your host. Jobs run as child processes; no cluster needed.

**4. Go to Kubernetes** — build a worker image containing your jobs assembly and switch the executor
to `AddKubernetesExecutor(...)` with `WorkerImage` pointing at it (see *Run on Kubernetes*). Same job
code, now one pod per execution.

## Projects

| Project | Role |
|---|---|
| `Klassd.Workflows.Abstractions` | The contract jobs implement: `IJob`, `IJobContext`, and the worker stdout protocol. |
| `Klassd.Workflows.Core` | Scheduler, in-memory store, cron recurring loop (Cronos), job catalog, and the **local-process executor**. |
| `Klassd.Workflows.Kubernetes` | `KubernetesJobExecutor` — creates a K8s `Job` per run and tails the pod logs. |
| `Klassd.Workflows.Worker` | The executor-pod entrypoint, packaged. `WorkerHost.CreateBuilder(args).RegisterJobs(…).RunAsync()` constructs the dispatched `IJob` from the registry, runs it, streams log/progress/state to stdout. `ConfigureServices` registers job dependencies; jobs are built with `ActivatorUtilities` or an explicit factory. |
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
        .FanOutOver("markets", "market_ids", itemArgument: "market", maxParallelism: 5))  // one pod per market, ≤5 at once
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
  Pass `maxParallelism: n` to cap how many run at once (0 = unlimited) so a large
  list doesn't spawn `n` pods simultaneously — the engine drip-feeds the rest.
- **inputs (`BindInput`)** — bind an argument to an upstream node's output
  (`"sourceNode.outputKey"`). For a service/container node's published address use
  `BindServiceAddress(arg, node)` / `BindServiceIp(arg, node)` (no need to know the
  `address`/`ip` key — Argo's `{{tasks.x.ip}}`); they also add the dependency.
- **file outputs (`valueFrom.path`)** — `WithFileOutput(name, path, default)`
  publishes a node output from a file the step writes (or the `default` if it's
  missing/empty) — for both `IJob` and arbitrary-container nodes. Handy to produce a
  fan-out list from a container that just writes a JSON file.
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
platform's default chain (workload identity / IRSA / env), or pass `accessKey` +
`secretKey` explicitly in `ArtifactSettings`. For S3-compatible stores set
`serviceUrl` (e.g. MinIO/Ceph); the `s3` provider then requests checksums only
`WHEN_REQUIRED`, since SDK v4's default checksums break many such stores.

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
dotnet run --project samples/Klassd.Workflows.DashboardHost
# open the printed URL — Home, Jobs catalog, Runs (status-filtered)
```

`DashboardHost` is the reference host that mounts the `Klassd.Workflows.Dashboard` RCL and wires the
sample jobs/workflow. Its local executor launches the sample worker
(`Klassd.Workflows.SampleWorker.dll`) as a child process per job. (Build the solution first so the
worker output exists.)

## Build your own worker image

The worker is a package, so your jobs live in **your** image. Reference `Klassd.Workflows.Worker`
from a thin exe, register the jobs it can run, and publish it:

```csharp
return await WorkerHost.CreateBuilder(args)
    // Dependencies your jobs take through their constructors:
    .ConfigureServices((svc, cfg) => svc.AddHttpClient().AddSingleton<IFoo, Foo>())
    .RegisterJobs(j =>
    {
        j.Add<GreetingJob>();                 // key defaults to the full type name
        j.Add<EmailJob>("send-email");        // explicit dispatch key
        j.Add("report", sp => new ReportJob(sp.GetRequiredService<IFoo>())); // explicit factory
    })
    // Artifact backends the worker supports, selected by name at runtime ("file" is built in):
    .AddArtifactProvider(new GcsArtifactStoreProvider())
    .RunAsync();
```

The scheduler launches this exe once per job, passing the **dispatch key** in the environment; the
worker looks the key up in the registry and constructs the matching job. Jobs are built with
`ActivatorUtilities`, so they can take constructor dependencies registered via `ConfigureServices`
(or use an explicit factory) — composing configuration from `appsettings[.{ENV}].json`,
`/secrets/*.json`, then environment variables.

The scheduler host registers the **same** jobs so the catalog and workflow-node validation agree on
the keys — put the registration in a shared method both reference:

```csharp
builder.Services.AddKlassdWorkflowsCore().AddJobs(MyJobs.Register);   // dashboard host
return await WorkerHost.CreateBuilder(args).RegisterJobs(MyJobs.Register).RunAsync();  // worker exe
```

The default `Add<T>()` key is the job's full type name, matching what `EnqueueAsync<T>()`, recurring
jobs and workflow nodes emit — so registering by type "just works" with the rest of the API. See
[`docs/recipes.md`](docs/recipes.md#give-a-job-dependencies-di) for worked examples.

## Run on Kubernetes

```bash
# 1. Build & push your worker image (see "Build your own worker image" above), e.g.
docker build -f path/to/your/Worker/Dockerfile -t <registry>/my-worker:latest .
docker push <registry>/my-worker:latest

# 2. If the dashboard runs in-cluster, grant it RBAC
kubectl apply -f deploy/rbac.yaml

# 3. Point the dashboard at the K8s executor (config / env)
#    Klassd.Workflows__Executor=kubernetes
#    Klassd.Workflows__WorkerImage=<registry>/my-worker:latest
#    Klassd.Workflows__Namespace=default
#    Klassd.Workflows__InCluster=true        # when the dashboard itself runs in K8s
```

Each enqueued job becomes a `batch/v1` Job (one pod, `restartPolicy: Never`,
`backoffLimit: 0`), garbage-collected via `ttlSecondsAfterFinished`. Stopping a
job deletes its Job; SIGTERM cancels the worker's `CancellationToken`.

### Shaping the pod

The Kubernetes executor lets you control the pod it creates at three scopes that combine:
**executor-wide** (`KubernetesExecutorOptions`), **per DAG node** (`NodeBuilder`), and **per
container/job** (`ContainerSpec` / `InitContainerSpec`):

- **Pod annotations & labels** (`PodAnnotations` / `PodLabels`) — stamped on every pod; the seam for
  sidecar injectors such as the Vault agent that key off pod annotations.
- **Init containers** — run to completion before the main container, combined in order
  executor-wide → node → container.
- **Volumes & mounts** — `emptyDir` / `secret` / `configMap` / PVC / `hostPath`, mounted into the
  main and init containers (e.g. an init container writes a shared `emptyDir` the job reads).
- **Security contexts** — pod-level (`runAsUser`/`fsGroup`/`seccomp`/…) and per-container
  (`readOnlyRootFilesystem`, drop capabilities, …).
- **Resources** — CPU/memory requests+limits on container nodes/jobs and init containers (worker
  jobs also honour `[JobResources]` and the `Resources`/`DefaultResources` options).
- **envFrom** — import a ConfigMap/Secret as environment variables.
- **Scheduling** — `nodeSelector`, `tolerations`, and `affinity` (node affinity + pod (anti-)affinity).

```csharp
new WorkflowBuilder("nightly")
    .AddContainer("sql-proxy", "gcr.io/.../cloud-sql-proxy:2.11", c => c.AsService().ServicePort(5432).ReadyOnTcp(5432))
    .Add<CleanupJob>("cleanup", n => n
        .BindServiceAddress("db_host", "sql-proxy")   // {podIP}:5432, and adds the dependency
        .WithInitContainer("migrate", "myorg/migrate:1", "--db", "$(db_host)")
        .WithEmptyDir("scratch").WithVolumeMount("scratch", "/scratch")
        .WithEnvFromSecret("db-creds")
        .WithNodeSelector("pool", "batch")
        .WithSecurityContext(new() { RunAsNonRoot = true, ReadOnlyRootFilesystem = true }));
```

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
- Fine-grained dashboard authorization (roles/permissions) — sign-in is done
  (via [Klassd.Auth](https://github.com/getklassd/Klassd.Auth)), per-action authz is not.

## License

[MIT](LICENSE) © Mark Lonquist
