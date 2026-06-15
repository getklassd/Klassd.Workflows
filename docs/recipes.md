# Klassd.Workflows — How-to recipes

Task-oriented recipes: *"I want behaviour X — what do I write?"* Each one is grounded in the
public API (`IJob`, `IJobContext`, `WorkflowBuilder`, `IJobScheduler`). For the conceptual
overview see the [README](../README.md); for the marketing/landing docs see
[getklassd.com/workflows/docs](https://getklassd.com/workflows/docs).

- [Give a job dependencies (per-execution DI)](#give-a-job-dependencies-per-execution-di)
- [Give every job the same dependencies (global DI)](#give-every-job-the-same-dependencies-global-di)
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

## Give a job dependencies (per-execution DI)

**Behaviour:** one job needs its own services (a typed client, options from config, a DB context)
without forcing a global startup class on the whole worker.

**What to write:** add a `Configure(IServiceCollection, IConfiguration)` method to the job. The
worker discovers it *by name and signature* — there is no interface to implement — and calls it on
**every execution**, including when the job runs as a workflow node. Jobs are then created with
`ActivatorUtilities`, so the registered services flow into the constructor.

```csharp
using Klassd.Workflows.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public sealed class ConfiguredGreetingJob : IJob
{
    private readonly GreetingOptions _options;

    // A parameterless ctor is required when Configure is an *instance* method, so the worker can
    // create the type to invoke the hook. ActivatorUtilities then re-creates it with the dependency.
    public ConfiguredGreetingJob() => _options = new GreetingOptions();

    public ConfiguredGreetingJob(GreetingOptions options) => _options = options;

    // Convention hook — discovered by name/signature, no interface to implement.
    public void Configure(IServiceCollection services, IConfiguration configuration)
    {
        var salutation = configuration["Greeting:Salutation"] ?? "Hello";
        services.AddSingleton(new GreetingOptions { Salutation = salutation });
    }

    public Task RunAsync(IJobContext context)
    {
        var name = context.Arguments.GetValueOrDefault("name", "world");
        context.Log($"{_options.Salutation}, {name}!");
        return Task.CompletedTask;
    }
}

public sealed class GreetingOptions
{
    public string Salutation { get; init; } = "Hello";
}
```

Set the value from any [configuration source](#where-configuration-comes-from), e.g. the env var
`Greeting__Salutation=Hej`, and the job picks it up — no recompile.

**Notes & gotchas:**

- **`static` is simpler.** If the hook doesn't need an instance, make it
  `public static void Configure(...)`. Then **no parameterless constructor is needed** — the worker
  invokes it without creating the type first. Prefer this unless you have a reason not to.
  ```csharp
  public static void Configure(IServiceCollection services, IConfiguration configuration)
      => services.AddSingleton(new GreetingOptions { Salutation = configuration["Greeting:Salutation"] ?? "Hello" });
  ```
- An **instance** hook without a parameterless ctor is **skipped** (the worker logs a `##LOG##`
  line and falls back) — it can't create the type to call the method. Use `static`, or add the ctor.
- If both a static and an instance `Configure` are present the **static one wins** (and a log line
  notes it). Don't define both.
- This runs **after** any global [`IWorkerStartup`](#give-every-job-the-same-dependencies-global-di),
  so a job can override or augment what the startup registered.
- This is the working `samples/Klassd.Workflows.SampleJobs/ConfiguredGreetingJob.cs` — run it from
  the dashboard and pass `name` as a `[JobInput]`.

## Give every job the same dependencies (global DI)

**Behaviour:** all jobs in the image share an `HttpClient`, a logger config, a DB connection, etc.

**What to write:** implement `IWorkerStartup` anywhere in (or beside) your job assembly. The worker
finds the single implementation on its load path automatically — no registration call — instantiates
it with its parameterless ctor, and calls `Configure` once to build the container that every job is
resolved from.

```csharp
using Klassd.Workflows.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public sealed class WorkerStartup : IWorkerStartup
{
    public void Configure(IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient();
        services.AddSingleton(new CatalogOptions { BaseUrl = configuration["Catalog:BaseUrl"]! });
        // ...register whatever your jobs take as constructor parameters
    }
}
```

Jobs then just declare what they need:

```csharp
public sealed class SyncCatalogJob(IHttpClientFactory http, CatalogOptions opts) : IJob
{
    public async Task RunAsync(IJobContext ctx) { /* use http, opts */ }
}
```

Exactly one `IWorkerStartup` is expected. Use the [per-job `Configure`](#give-a-job-dependencies-per-execution-di)
hook for things only one job needs. Jobs with a parameterless constructor need neither.

## Where configuration comes from

The `IConfiguration` handed to both DI hooks (and resolvable in any job, since the worker registers
it as a singleton) is composed in this order — **last source wins**:

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
