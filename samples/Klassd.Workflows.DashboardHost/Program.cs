using Klassd.Workflows.Auth;
using Klassd.Workflows.Auth.OpenIdConnect;
using Klassd.Workflows.Core;
using Klassd.Workflows.Core.Abstractions;
using Klassd.Workflows.Core.Model;
using Klassd.Workflows.Core.Workflows;
using Klassd.Workflows.Dashboard;
using Klassd.Workflows.Kubernetes;
using Klassd.Workflows.SampleJobs;
using Klassd.Workflows.SampleJobs.Dag;
using Klassd.Workflows.Storage.MongoDb;
using Klassd.Workflows.Storage.Postgres;
using Klassd.Workflows.Storage.Sqlite;

var builder = WebApplication.CreateBuilder(args);

// The dashboard reads the theme cookie during SSR.
builder.Services.AddHttpContextAccessor();

// --- Authentication ---------------------------------------------------------
// Registered first so the durable store selection below can attach the matching Klassd.Auth user
// store to it. Users admin + email/password sign-in, mirroring Klassd CMS. Loopback (local dev +
// `kubectl port-forward`) is bypassed, so neither needs a login. A seed admin is created on first
// run from config so a fresh deployment isn't locked out. Optionally add OIDC SSO under "Oidc"
// (SSO identities are linked to an existing user by email, or auto-provisioned).
builder.Services.AddKlassdWorkflowsAuth(o =>
{
    o.SigningKey = builder.Configuration["Auth:SigningKey"];
    o.SeedAdminEmail = builder.Configuration["Auth:SeedAdmin:Email"];
    o.SeedAdminPassword = builder.Configuration["Auth:SeedAdmin:Password"];
});
if (!string.IsNullOrWhiteSpace(builder.Configuration["Oidc:Authority"]))
    builder.Services.AddKlassdWorkflowsOpenIdConnect("Company SSO", builder.Configuration.GetSection("Oidc"));

// --- Klassd.Workflows wiring -------------------------------------------------
var workflows = builder.Services.AddKlassdWorkflowsCore();

// The jobs this scheduler knows about (catalog "Run" buttons, workflow-node validation). Same
// shared registration the worker exe runs, so both sides agree on the dispatch keys.
workflows.AddJobs(SampleJobsRegistration.Register);

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
    case "sqlite":
        workflows.UseSqlite(builder.Configuration["Klassd.Workflows:Sqlite:ConnectionString"]
            ?? "Data Source=klassd-workflows.db");
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

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseKlassdWorkflowsAuth();   // authentication + loopback bypass + authorization
app.UseAntiforgery();

app.MapKlassdWorkflowsDashboard();

// Dev-only helpers to trigger a workflow and inspect a run without the UI (handy for demos/tests).
if (app.Environment.IsDevelopment())
{
    app.MapPost("/dev/run/{name}", async (string name, IWorkflowOrchestrator wf) =>
        Results.Ok(new { runId = await wf.StartAsync(name) })).AllowAnonymous();

    app.MapPost("/dev/container/{name}", async (string name, IContainerJobRegistry reg, IJobScheduler sched) =>
        reg.Get(name) is { } def
            ? Results.Ok(new { execId = await sched.EnqueueContainerAsync(def.Name, def.Container) })
            : Results.NotFound()).AllowAnonymous();

    app.MapGet("/dev/exec/{id}", async (string id, IJobStore store) =>
    {
        var e = await store.GetAsync(id);
        return e is null
            ? Results.NotFound()
            : Results.Ok(new { status = e.Status.ToString(), e.Ready, e.Outputs, e.Error, logs = e.Logs.ToArray() });
    }).AllowAnonymous();

    app.MapGet("/dev/run/{id}", async (string id, IJobStore store) =>
    {
        var run = await store.GetWorkflowRunAsync(id);
        if (run is null) return Results.NotFound();
        var nodes = new List<object>();
        foreach (var n in run.Nodes)
        {
            var execs = new List<object>();
            foreach (var eid in n.ExecutionIds)
            {
                var e = await store.GetAsync(eid);
                if (e is not null)
                    execs.Add(new { e.Status, e.Ready, e.Outputs, e.Arguments, logs = e.Logs.ToArray() });
            }
            nodes.Add(new { n.Name, n.IsService, n.Ready, status = n.Status.ToString(), execs });
        }
        return Results.Ok(new { status = run.Status.ToString(), nodes });
    }).AllowAnonymous();
}

// Register cron jobs and a sample workflow through code (the "cron-setup through code" demo).
var scheduler = app.Services.GetRequiredService<IJobScheduler>();
scheduler.AddOrUpdateRecurring<HelloWorldJob>("hello-every-minute", "* * * * *", new() { ["name"] = "cron" });
scheduler.AddOrUpdateRecurring<ReportGenerationJob>("report-every-5-min", "*/5 * * * *");

var registry = app.Services.GetRequiredService<IWorkflowRegistry>();
registry.Register(new WorkflowBuilder("catalog-integration")
    .Add<MarketFinderJob>("markets")
    .Add<DataProxyJob>("data-proxy")
    // Long-running service node: comes up, publishes its address, stays up while the rest of the
    // run connects through it, then the engine tears it down at the end.
    .Add<SqlProxyServiceJob>("sql-proxy", n => n.AsService())
    .Add<IntegrationJob>("integration", n => n
        .DependsOn("markets", "data-proxy")
        .BindServiceAddress("db_host", "sql-proxy")     // forwarded service address (also adds the dependency)
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

// Run an arbitrary container image as a standalone job — no IJob port needed (e.g. a legacy Go tool).
// Registered so it shows in the Jobs catalog with a Run button; also schedulable on a cron.
var containerJobs = app.Services.GetRequiredService<IContainerJobRegistry>();
containerJobs.Register(new ContainerJobDefinition
{
    Name = "legacy-importer",
    Description = "Example of running an existing container image as a job.",
    Container = new ContainerSpec
    {
        Image = "alpine:latest",
        Command = ["sh", "-c"],
        Args = ["echo 'importing catalog...'; sleep 2; echo 'done'"]
    }
});
scheduler.AddOrUpdateRecurringContainer("legacy-importer-nightly", "legacy-importer",
    new ContainerSpec { Image = "alpine:latest", Command = ["sh", "-c"], Args = ["echo nightly import; sleep 1"] },
    "0 3 * * *");

// Dev-only: exercise the container-node path with a real public image (nginx) as a service node.
if (app.Environment.IsDevelopment())
    registry.Register(new WorkflowBuilder("container-shim-test")
        .AddContainer("proxy", "nginx:alpine", c => c.ServicePort(80).ReadyOnTcp(80).AsService())
        .Add<IntegrationJob>("consumer", n => n
            .BindServiceAddress("db_host", "proxy")
            .WithArgument("market", "test"))
        .Build());

// Same pattern with a REAL container image instead of an IJob: run the official cloud-sql-proxy as a
// long-running service node, forward its address to the integration job. Registered (not scheduled);
// running it needs Docker (local executor) or a cluster (Kubernetes executor) plus a real instance.
registry.Register(new WorkflowBuilder("cloud-sql-integration")
    .AddContainer("sql-proxy", "gcr.io/cloud-sql-connectors/cloud-sql-proxy:2.11.0", c => c
        .WithArgs("--address=0.0.0.0", "--port=5432", "my-project:region:instance")
        .ServicePort(5432)
        .ReadyOnTcp(5432)
        .AsService())
    .Add<IntegrationJob>("integration", n => n
        .BindServiceAddress("db_host", "sql-proxy")
        .WithArgument("market", "default"))
    .Build());

app.Run();

// Default worker dll path for the local executor: the sample worker's build output for the
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
        builder.Environment.ContentRootPath, "..", "Klassd.Workflows.SampleWorker",
        "bin", cfg, tfm, "Klassd.Workflows.SampleWorker.dll"));
}
