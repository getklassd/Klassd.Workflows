# Klassd.Workflows — How-to recipes

Task-oriented recipes: *"I want behaviour X — what do I write?"* Each one is grounded in the
public API (`IJob`, `IJobContext`, `WorkflowBuilder`, `IJobScheduler`). For the conceptual
overview see the [README](../README.md); for the marketing/landing docs see
[getklassd.com/workflows/docs](https://getklassd.com/workflows/docs).

- [Register jobs and give them dependencies (DI)](#register-jobs-and-give-them-dependencies-di)
- [Where configuration comes from](#where-configuration-comes-from)
- [Accept manual-start inputs](#accept-manual-start-inputs)
- [Log and report progress](#log-and-report-progress)
- [Pass data between DAG nodes](#pass-data-between-dag-nodes)
- [Pass large payloads (artifacts)](#pass-large-payloads-artifacts)
- [Fan out over a list](#fan-out-over-a-list)
- [Run a node only sometimes (conditions)](#run-a-node-only-sometimes-conditions)
- [Retry a flaky step](#retry-a-flaky-step)
- [Run an existing container image](#run-an-existing-container-image)
- [Run a sidecar/service node (e.g. cloud-sql-proxy)](#run-a-sidecarservice-node-eg-cloud-sql-proxy)
- [Schedule jobs and workflows](#schedule-jobs-and-workflows)
- [Set pod CPU/memory](#set-pod-cpumemory)
- [React to cancellation / stop cleanly](#react-to-cancellation--stop-cleanly)

---

## Register jobs and give them dependencies (DI)

**Behaviour:** your worker runs a known set of jobs, and those jobs take constructor dependencies
(a typed client, options from config, a DB context).

**What to write:** each job declares its own dependencies by overriding the static `IJob.Configure`
— co-located with the job, and run **only when that job is dispatched**, so a worker image hosting
dozens of jobs never pays to register dependencies the invoked job doesn't use. Registration is then
just `j.Add<MyJob>()`. Both halves are reflection-free: `Configure` is dispatched through the
static-interface-member generic constraint (a compile-time virtual call), and the job is constructed by
a source-generated `new MyJob(sp.GetRequiredService<…>())` factory (the Klassd.Workflows generator
ships as an analyzer in `Klassd.Workflows.Abstractions`) — no `ActivatorUtilities`, trim/AOT-friendly.
Reserve the worker-wide `ConfigureServices` for genuinely cross-cutting services (e.g. `HttpClient`)
that every job shares; the generated factory pulls each constructor argument from the provider those
callbacks populate. Put the registration in a shared method the **dashboard host also calls**
(`AddJobs`), so both sides agree on the dispatch keys.

```csharp
using Klassd.Workflows.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public sealed class SyncCatalogJob(IHttpClientFactory http, CatalogOptions opts) : IJob
{
    // This job's own dependency — registered only when SyncCatalogJob is the one dispatched.
    public static void Configure(IServiceCollection services, IConfiguration config) =>
        services.AddSingleton(new CatalogOptions { BaseUrl = config["Catalog:BaseUrl"]! });

    public async Task RunAsync(IJobContext ctx) { /* use http, opts */ }
}

public sealed class GreetingOptions
{
    public string Salutation { get; init; } = "Hello";
}

public sealed class ConfiguredGreetingJob(GreetingOptions options) : IJob
{
    public static void Configure(IServiceCollection services, IConfiguration config) =>
        services.AddSingleton(new GreetingOptions { Salutation = config["Greeting:Salutation"] ?? "Hello" });

    public Task RunAsync(IJobContext context)
    {
        var name = context.Arguments.GetValueOrDefault("name", "world");
        context.Log($"{options.Salutation}, {name}!");
        return Task.CompletedTask;
    }
}

// The shared registration — referenced by both the worker exe and the dashboard host.
// Each job's Configure is picked up automatically; nothing to wire here.
public static class MyJobs
{
    public static void Register(JobRegistrationBuilder j) => j
        .Add<SyncCatalogJob>()
        .Add<ConfiguredGreetingJob>();
}
```

```csharp
// The worker exe (Program.cs):
return await WorkerHost.CreateBuilder(args)
    .ConfigureServices((services, config) => services.AddHttpClient())  // cross-cutting only
    .RegisterJobs(MyJobs.Register)                                      // per-job deps live here
    .RunAsync();
```

```csharp
// The dashboard host — register the same jobs so the catalog/workflow validation match:
builder.Services.AddKlassdWorkflowsCore().AddJobs(MyJobs.Register);
```

Set option values from any [configuration source](#where-configuration-comes-from), e.g. the env var
`Greeting__Salutation=Hej`, and the job picks it up — no recompile.

**Notes & gotchas:**

- **Keys default to the full type name**, matching what `EnqueueAsync<T>()`, recurring jobs and
  workflow nodes emit — so `j.Add<MyJob>()` "just works" with the rest of the API. Pass an explicit
  key (`j.Add<MyJob>("my-key")`) only if you want a stable key decoupled from the type name.
- **Construction is source-generated, not reflective.** `j.Add<MyJob>()` uses a compile-time
  `new MyJob(sp.GetRequiredService<…>())` factory the generator emits for every `IJob` (greediest
  public constructor, each argument resolved from the provider). The generator ships as an analyzer in
  the `Klassd.Workflows.Abstractions` NuGet, so it just works for consumers; if you reference the
  projects in-source instead, add the generator as an `OutputItemType="Analyzer"` reference. If no
  factory was generated for a type, `Add<T>()` throws and points you at the fix.
- **Need bespoke construction?** Use the factory overload:
  `j.Add("report", sp => new ReportJob(sp.GetRequiredService<IFoo>()))` — it bypasses the generated
  factory entirely. A factory returning a concrete `IJob` still records the type for the catalog; type
  it as `Func<IServiceProvider, IJob>` to register without type metadata.
- **Per-job DI runs only for the dispatched job.** A worker process runs exactly one job, so only the
  matched job's `Configure` executes — declare each job's dependencies on its own `Configure` and jobs
  that aren't invoked cost nothing. The worker-wide `ConfigureServices` runs first (for the
  cross-cutting services every job shares), then the job's `Configure`, then an optional call-site
  `configure:` argument on `Add<T>(...)`; all three feed the same provider the job is built from.
- `ConfiguredGreetingJob` is the working `samples/Klassd.Workflows.SampleJobs/ConfiguredGreetingJob.cs`
  — run it from the dashboard and pass `name` as a `[JobInput]`.

## Where configuration comes from

The `IConfiguration` handed to `ConfigureServices` (and resolvable in any job, since the worker
registers it as a singleton) is composed in this order — **last source wins**:

1. `appsettings.json`
2. `appsettings.{ENV}.json` (where `ENV` = `DOTNET_ENVIRONMENT` or `ASPNETCORE_ENVIRONMENT`)
3. every `/secrets/*.json` (ordinal order) — the Vault-agent drop directory, if it exists
4. environment variables

So `Greeting:Salutation` can be set as `"Greeting": { "Salutation": "Hej" }` in appsettings, dropped
in by a Vault sidecar at `/secrets/greeting.json`, or overridden with `Greeting__Salutation=Hej`
(env wins over both).

## Accept manual-start inputs

**Behaviour:** the dashboard should render a form when someone starts the job by hand.

**What to write:** decorate the job with `[JobInput]` (repeatable). `Default` pre-fills the field;
`Required` marks it mandatory. Read the values from `ctx.Arguments`.

```csharp
[JobInput("name", Default = "world")]
[JobInput("dryRun", Default = "false")]
public sealed class HelloJob : IJob
{
    public Task RunAsync(IJobContext ctx)
    {
        var who = ctx.Arguments.GetValueOrDefault("name", "world");
        var dryRun = bool.Parse(ctx.Arguments.GetValueOrDefault("dryRun", "false"));
        ctx.Log($"hello {who} (dryRun={dryRun})");
        return Task.CompletedTask;
    }
}
```

Arguments are also how you pass values when enqueuing from code:
`await scheduler.EnqueueAsync<HelloJob>(new() { ["name"] = "team" });`

## Log and report progress

`IJobContext` drives the live console:

```csharp
public async Task RunAsync(IJobContext ctx)
{
    ctx.Log("starting");                       // a console line
    ctx.ReportProgress(50, "halfway");         // the overall 0-100 percent bar + status

    // WithProgress(n) returns n and advances a single in-place bar each evaluation —
    // ideal as a loop bound so you don't spam a line per iteration:
    var items = await LoadItemsAsync();
    for (var i = 0; i < ctx.WithProgress(items.Count); i++)
        await ProcessAsync(items[i], ctx.CancellationToken);

    ctx.Log("done");
}
```

`Log` appends lines; `ReportProgress` drives the overall percentage; `WithProgress` animates a pinned
bar for a fixed-size loop. They're independent — use whichever fits.

## Pass data between DAG nodes

**Behaviour:** an upstream node produces a value a downstream node consumes.

**What to write:** the producer calls `SetOutput`; the consumer binds it with `BindInput` and reads
it from `Arguments`.

```csharp
// producer node
context.SetOutput("dataset_ref", reference);

// in the DAG
.Add<FinalizerJob>("finalizer", n => n
    .DependsOn("data-proxy")
    .BindInput("dataset_ref", "data-proxy", "dataset_ref"))   // arg <- sourceNode.outputKey

// consumer node
var datasetRef = context.Arguments.GetValueOrDefault("dataset_ref");
```

`BindInput` also adds the dependency for you. Outputs are env-sized strings — for anything large use
[artifacts](#pass-large-payloads-artifacts).

## Pass large payloads (artifacts)

**Behaviour:** a node produces a payload too big for an output string (a file, a big JSON blob).

**What to write:** save it through `context.Artifacts` (an `IArtifactStore`), publish the small
reference as an output, and load it downstream.

```csharp
// producer
var reference = await context.Artifacts.SaveAsync("dataset.json", bytes, context.CancellationToken);
context.SetOutput("dataset_ref", reference);

// consumer (after BindInput("dataset_ref", ...))
var bytes = await context.Artifacts.LoadAsync(context.Arguments["dataset_ref"], context.CancellationToken);
```

The provider is chosen per-deployment by name (`file`, `s3`, `gcs`) — see the README's
*Storage & database adapters*. The filesystem store works locally and on a shared RWX volume.

## Fan out over a list

**Behaviour:** run one pod per element of a list an upstream node produced.

**What to write:** emit a JSON array as an output, then `FanOutOver` it. Each execution sees its
element as the named argument.

```csharp
// root node emits the array
context.SetOutput("market_ids", JsonSerializer.Serialize(new[] { "dk", "se", "no" }));

// fan-out node: one pod per market, at most 5 at a time
.Add<IntegrationJob>("integration", n => n
    .DependsOn("markets")
    .FanOutOver("markets", "market_ids", itemArgument: "market", maxParallelism: 5))

// inside IntegrationJob
var market = context.Arguments.GetValueOrDefault("market");
```

`maxParallelism: 0` = unlimited. The node joins when all fan-out executions finish. To produce the
list from a container that just writes a file, use `WithFileOutput(name, path, default)`.

## Run a node only sometimes (conditions)

**Behaviour:** branch — run a notify node only when an upstream succeeded, a rollback node only when
it failed.

```csharp
.Add<NotifyJob>("notify",     n => n.DependsOn("data-proxy").When("data-proxy", "status", "ok"))
.Add<RollbackJob>("rollback", n => n.DependsOn("data-proxy").When("data-proxy", "status", "failed"))
```

Or a predicate over all upstream outputs: `.When(outputs => outputs["count"] != "0")`. A node whose
condition is false is **omitted** — benign, so its non-conditional siblings still run and the run can
still succeed.

## Retry a flaky step

```csharp
.Add<PublishJob>("publish", n => n.DependsOn("integration").WithRetries(2))   // up to 2 re-runs
```

Retries are per fan-out item. Each attempt gets its 0-based index in the `__attempt` argument; the
graph marks retried nodes with `↻`. (Retries are immediate today — no backoff yet.)

## Run an existing container image

**Behaviour:** run a legacy Go binary / vendor CLI as a first-class job — no `IJob` port.

**Standalone:**

```csharp
await scheduler.EnqueueContainerAsync("legacy-importer",
    new ContainerSpec { Image = "ghcr.io/acme/go-importer:1.4", Args = ["--full"] });
```

**As a DAG node:**

```csharp
.AddContainer("importer", "ghcr.io/acme/go-importer:1.4", c => c.WithArgs("--full"))
```

Under Kubernetes it runs as its own pod; under the local executor it runs via `docker run`, so the
same DAG works in dev.

## Run a sidecar/service node (e.g. cloud-sql-proxy)

**Behaviour:** a long-running helper that must be up *while* dependents run, then torn down.

```csharp
new WorkflowBuilder("cloud-sql-integration")
    .AddContainer("sql-proxy", "gcr.io/cloud-sql-connectors/cloud-sql-proxy:2.11.0", c => c
        .WithArgs("--address=0.0.0.0", "--port=5432", "my-project:region:instance")
        .ServicePort(5432).ReadyOnTcp(5432)
        .AsService())                                   // stays up; reaped at end of run
    .Add<IntegrationJob>("integration", n => n
        .BindServiceAddress("db_host", "sql-proxy"))    // {podIP}:5432 forwarded; also adds the dependency
    .Build();
```

`AsService()` makes the node satisfy dependents once its pod is **ready** (gated on `ReadyOnTcp`),
not when it exits. A **C# service job** does the same from code: publish outputs, call
`ctx.SignalReady()`, then `await ctx.CancellationToken` (treating cancellation as normal shutdown):

```csharp
public async Task RunAsync(IJobContext ctx)
{
    ctx.SetOutput("address", $"{ctx.PodIp}:5432");
    ctx.SignalReady();                          // unblock dependents; keep running
    await Task.Delay(Timeout.Infinite, ctx.CancellationToken);  // until torn down
}
```

## Schedule jobs and workflows

```csharp
var scheduler = app.Services.GetRequiredService<IJobScheduler>();

await scheduler.EnqueueAsync<HelloJob>(new() { ["name"] = "team" });   // fire now
scheduler.AddOrUpdateRecurring<HelloJob>("nightly", "0 2 * * *");      // cron (Cronos)

await scheduler.EnqueueWorkflowAsync("catalog-integration");          // run a DAG now
scheduler.AddOrUpdateRecurringWorkflow("nightly-catalog", "catalog-integration", "0 3 * * *");
```

There are non-generic overloads taking a job type name (`EnqueueAsync("HelloJob")`), plus
`EnqueueContainerAsync` / `AddOrUpdateRecurringContainer` for container-backed jobs.

## Set pod CPU/memory

**Behaviour:** give a job specific requests/limits, retunable from config without a recompile.

```csharp
[JobResources(CpuRequest = "250m", CpuLimit = "1", MemoryRequest = "256Mi", MemoryLimit = "512Mi")]
public sealed class ReportGenerationJob : IJob { /* ... */ }
```

Three layers merge field-by-field, **config wins**: `DefaultResources` (global) → `[JobResources]`
(the code baseline) → `Klassd.Workflows:Resources:<typeName>` (per-job appsettings/Vault override).
Use the sentinel `"none"` to *clear* a value a lower layer set (e.g. drop a CPU limit), or
`Unconstrained = true` for no requests/limits at all. See the README's *Pod resources* for the full
table.

## React to cancellation / stop cleanly

Stopping a job from the UI (or the rest of a run finishing, for a service node) signals
`ctx.CancellationToken` and, on Kubernetes, SIGTERMs the pod. Always thread the token through async
calls and treat cancellation as a normal shutdown:

```csharp
public async Task RunAsync(IJobContext ctx)
{
    try
    {
        await DoWorkAsync(ctx.CancellationToken);
    }
    catch (OperationCanceledException) when (ctx.CancellationToken.IsCancellationRequested)
    {
        ctx.Log("stopped — cleaning up");
        // flush/cleanup; let the method return so the pod exits
    }
}
```
