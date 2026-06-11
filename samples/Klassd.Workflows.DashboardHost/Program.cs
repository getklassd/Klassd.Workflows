using Klassd.Workflows.Core;
using Klassd.Workflows.Core.Abstractions;
using Klassd.Workflows.Core.Workflows;
using Klassd.Workflows.Dashboard;
using Klassd.Workflows.Kubernetes;
using Klassd.Workflows.SampleJobs;
using Klassd.Workflows.SampleJobs.Dag;
using Klassd.Workflows.Storage.MongoDb;
using Klassd.Workflows.Storage.Postgres;

var builder = WebApplication.CreateBuilder(args);

// The dashboard reads the theme cookie during SSR.
builder.Services.AddHttpContextAccessor();

// --- Klassd.Workflows wiring -------------------------------------------------
var workflows = builder.Services.AddKlassdWorkflowsCore();

// Durable store selection (default in-memory). Set Klassd.Workflows:Store + a connection string.
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

// Executor. Default "local" (a child process per job) so the dashboard works without a cluster.
var executor = builder.Configuration["Klassd.Workflows:Executor"] ?? "local";
if (executor.Equals("kubernetes", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddKubernetesExecutor(builder.Configuration.GetSection("Klassd.Workflows"));
else
    builder.Services.AddLocalExecutor(ResolveWorkerDllPath(builder));

// The dashboard UI (Blazor Interactive Server).
builder.Services.AddKlassdWorkflowsDashboard();
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

app.MapKlassdWorkflowsDashboard();

// Register cron jobs and a sample workflow through code (the "cron-setup through code" demo).
var scheduler = app.Services.GetRequiredService<IJobScheduler>();
scheduler.AddOrUpdateRecurring<HelloWorldJob>("hello-every-minute", "* * * * *", new() { ["name"] = "cron" });
scheduler.AddOrUpdateRecurring<ReportGenerationJob>("report-every-5-min", "*/5 * * * *");

var registry = app.Services.GetRequiredService<IWorkflowRegistry>();
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

scheduler.AddOrUpdateRecurringWorkflow("catalog-integration-hourly", "catalog-integration", "0 * * * *");

app.Run();

// Default worker dll path for the local executor: the Worker project's build output for the
// current configuration. Override with Klassd.Workflows:WorkerDllPath.
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
        builder.Environment.ContentRootPath, "..", "..", "src", "Klassd.Workflows.Worker",
        "bin", cfg, tfm, "Klassd.Workflows.Worker.dll"));
}
