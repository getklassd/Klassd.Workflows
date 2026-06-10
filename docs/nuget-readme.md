# Klassd.Workflows

A **code-first, NuGet-distributed background-job and workflow engine** for .NET. Jobs are plain
**C# classes** implementing a small interface; the scheduler runs them as **local processes** in
dev and as **one Kubernetes pod per execution** in production — the same worker either way. Compose
jobs into **DAG workflows** (dependencies, fan-out, conditions, retries, artifact passing) and watch
them live in a Blazor dashboard.

> ⚠️ **Beta.** Klassd.Workflows is in public beta (`0.x`). It works and is tested, but the API
> surface may change between releases until `1.0`. Pin your versions and read the release notes
> when upgrading.

## Install

Install the core plus the adapters you need (add `--prerelease` while in beta):

```bash
dotnet add package Klassd.Workflows.Core --prerelease
dotnet add package Klassd.Workflows.Storage.Postgres --prerelease   # durable store (optional)
dotnet add package Klassd.Workflows.Kubernetes --prerelease         # K8s executor (optional)
```

```csharp
// Define a job
public sealed class MyJob : IJob
{
    public async Task RunAsync(IJobContext ctx)
    {
        ctx.Log("starting");
        ctx.ReportProgress(50, "halfway");
        await Task.Delay(1000, ctx.CancellationToken);
    }
}

// Wire it up
var workflows = builder.Services.AddKlassdWorkflowsCore();
workflows.UsePostgres("Host=…;Database=…;Username=…;Password=…");   // or .UseMongo(...) / in-memory
builder.Services.AddKubernetesExecutor(builder.Configuration);      // or AddLocalExecutor(...)
```

## Packages

| Package | Purpose |
|---------|---------|
| `Klassd.Workflows.Abstractions` | `IJob` / `IJobContext`, the artifact-store seam, worker protocol (no deps) |
| `Klassd.Workflows.Core` | Scheduler, job catalog, cron loop, in-memory store, DAG orchestrator, local executor |
| `Klassd.Workflows.Kubernetes` | `KubernetesJobExecutor` — one `batch/v1` Job per execution |
| `Klassd.Workflows.Storage.Postgres` / `.Storage.MongoDb` | Durable `IJobStore` adapters |
| `Klassd.Workflows.Artifacts.S3` / `.Artifacts.Gcs` | `IArtifactStore` adapters for large payloads between nodes |

The core carries **no** Kubernetes/AWS/Google/Mongo/Npgsql dependency — each adapter keeps its SDK
isolated, so you only pull in what you wire up.

## Documentation

Full quickstart, the workflow/DAG model, pod-resource tuning, storage/artifact configuration, and
running locally vs. on Kubernetes are on the project's GitHub repository:
**https://github.com/getklassd/Klassd.Workflows**

## License

[MIT](https://github.com/getklassd/Klassd.Workflows/blob/main/LICENSE) © Mark Lonquist
