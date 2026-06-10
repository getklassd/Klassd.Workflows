using Klassd.Workflows.Core;
using Klassd.Workflows.Core.Abstractions;
using Klassd.Workflows.Core.Workflows;
using Klassd.Workflows.Dashboard.Components;
using Klassd.Workflows.Kubernetes;
using Klassd.Workflows.SampleJobs;
using Klassd.Workflows.SampleJobs.Dag;
using Klassd.Workflows.Storage.MongoDb;
using Klassd.Workflows.Storage.Postgres;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// --- Klassd.Workflows wiring -------------------------------------------------
var workflows = builder.Services.AddKlassdWorkflowsCore();

// Durable store selection (default in-memory). Set Klassd.Workflows:Store and a
// connection string to switch — or call workflows.UseJobStore<T>() with your own.
switch ((builder.Configuration["Klassd.Workflows:Store"] ?? "inmemory").ToLowerInvariant())
{
    case "postgres":
        workflows.UsePostgres(builder.Configuration["Klassd.Workflows:Postgres:ConnectionString"]!);
        break;
    case "mongo" or "mongodb":
        workflows.UseMongo(builder.Configuration["Klassd.Workflows:Mongo:ConnectionString"]!,
            builder.Configuration["Klassd.Workflows:Mongo:Database"] ?? "klassd_workflows");
        break;
    // "inmemory" → keep the default registered by AddKlassdWorkflowsCore.
}

// Pick the executor. Default is "local" (runs each job as a local process) so
// the dashboard works without a cluster. Set Klassd.Workflows:Executor=kubernetes to
// spin up a real pod per job.
var executor = builder.Configuration["Klassd.Workflows:Executor"] ?? "local";
if (executor.Equals("kubernetes", StringComparison.OrdinalIgnoreCase))
{
    // Binds WorkerImage, Namespace, InCluster, KubeConfigPath, DefaultResources
    // and per-job Resources straight from the Klassd.Workflows config section.
    builder.Services.AddKubernetesExecutor(builder.Configuration.GetSection("Klassd.Workflows"));
}
else
{
    builder.Services.AddLocalExecutor(ResolveWorkerDllPath(builder));
}
// ------------------------------------------------------------------------

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Register cron jobs through code — the "cron-setup through code" requirement.
using (var scope = app.Services.CreateScope())
{
    var scheduler = scope.ServiceProvider.GetRequiredService<IJobScheduler>();
    scheduler.AddOrUpdateRecurring<HelloWorldJob>("hello-every-minute", "* * * * *",
        new() { ["name"] = "cron" });
    scheduler.AddOrUpdateRecurring<ReportGenerationJob>("report-every-5-min", "*/5 * * * *");

    // Register a workflow DAG through code (mirrors the Argo CatalogIntegration shape).
    // Shows dependencies, fan-out, retries, conditional (when) nodes and artifacts:
    //   markets ─┐
    //            ├─► integration (fan-out/market) ─► publish (retries) ─► finalizer (reads artifact)
    //   data-proxy┤
    //            ├─► notify   (when status == ok    → runs)
    //            └─► rollback (when status == failed → omitted)
    var registry = scope.ServiceProvider.GetRequiredService<IWorkflowRegistry>();
    registry.Register(new WorkflowBuilder("catalog-integration")
        .Add<MarketFinderJob>("markets")
        .Add<DataProxyJob>("data-proxy")
        .Add<IntegrationJob>("integration", n => n
            .DependsOn("markets", "data-proxy")
            .FanOutOver("markets", "market_ids", itemArgument: "market"))
        .Add<PublishJob>("publish", n => n
            .DependsOn("integration")
            .WithRetries(2))
        .Add<FinalizerJob>("finalizer", n => n
            .DependsOn("publish", "data-proxy")
            .BindInput("dataset_ref", "data-proxy", "dataset_ref"))
        .Add<NotifyJob>("notify", n => n
            .DependsOn("data-proxy")
            .When("data-proxy", "status", "ok"))
        .Add<RollbackJob>("rollback", n => n
            .DependsOn("data-proxy")
            .When("data-proxy", "status", "failed"))
        .Build());

    // A workflow can also run on a cron schedule (recurring DAG).
    scheduler.AddOrUpdateRecurringWorkflow("catalog-integration-hourly", "catalog-integration", "0 * * * *");
}

app.Run();

// Default worker dll path for the local executor: the sibling Worker project's
// build output for the current configuration. Override with Klassd.Workflows:WorkerDllPath.
static string ResolveWorkerDllPath(WebApplicationBuilder builder)
{
    var configured = builder.Configuration["Klassd.Workflows:WorkerDllPath"];
    if (!string.IsNullOrWhiteSpace(configured)) return configured;

    const string tfm = "net10.0";
#if DEBUG
    const string cfg = "Debug";
#else
    const string cfg = "Release";
#endif
    return Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath, "..", "Klassd.Workflows.Worker",
        "bin", cfg, tfm, "Klassd.Workflows.Worker.dll"));
}
