using Klassd.Workflows.Core;
using Klassd.Workflows.Core.Abstractions;
using Klassd.Workflows.Core.Workflows;
using Klassd.Workflows.Kubernetes;
using Klassd.Workflows.SampleJobs.Dag;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Klassd.Workflows.IntegrationTests;

/// <summary>
/// One shared K3s cluster + a fully-wired Klassd.Workflows host (core services, the Kubernetes
/// executor pointed at the cluster, and the running orchestrator/recurring hosted services) for
/// the whole test assembly. Set up once via the assembly hooks; the executor tests drive it.
/// </summary>
internal static class TestHost
{
    public const string SampleWorkflow = "catalog-integration";

    private static KubernetesCluster? _cluster;
    private static MinioStore? _minio;
    private static ServiceProvider? _provider;
    private static readonly List<IHostedService> Hosted = [];

    public static IJobScheduler Scheduler => _provider!.GetRequiredService<IJobScheduler>();
    public static IJobStore Store => _provider!.GetRequiredService<IJobStore>();

    public static async Task InitializeAsync(CancellationToken ct = default)
    {
        _cluster = await KubernetesCluster.StartAsync(ct);
        _minio = await MinioStore.StartAsync(ct);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddKlassdWorkflowsCore();
        services.AddKubernetesExecutor(o =>
        {
            o.WorkerImage = KubernetesCluster.WorkerImage;
            o.ImagePullPolicy = "Never";       // image is side-loaded into the node
            o.Namespace = "default";
            o.KubeConfigPath = _cluster.KubeConfigPath;
            o.InCluster = false;
            o.TtlSecondsAfterFinished = 60;

            // Real S3 artifact store (MinIO) so artifacts pass between pods.
            o.ArtifactProvider = "s3";
            o.ArtifactSettings = new()
            {
                ["bucket"] = MinioStore.Bucket,
                ["serviceUrl"] = _minio.InClusterServiceUrl,
                ["region"] = "us-east-1",
                ["accessKey"] = MinioStore.AccessKey,
                ["secretKey"] = MinioStore.SecretKey,
            };
        });

        _provider = services.BuildServiceProvider();
        RegisterSampleWorkflow(_provider.GetRequiredService<IWorkflowRegistry>());

        foreach (var hs in _provider.GetServices<IHostedService>())
        {
            await hs.StartAsync(ct);
            Hosted.Add(hs);
        }
    }

    public static async Task DisposeAsync()
    {
        foreach (var hs in Hosted)
            try { await hs.StopAsync(CancellationToken.None); } catch { /* best effort */ }
        if (_provider is not null) await _provider.DisposeAsync();
        if (_minio is not null) await _minio.DisposeAsync();
        if (_cluster is not null) await _cluster.DisposeAsync();
    }

    // The dashboard's full sample DAG: dependencies, fan-out, retries, when-gated nodes, and a
    // finalizer that reads the dataset artifact data-proxy wrote — exercised end-to-end across pods
    // via the MinIO-backed S3 artifact store wired above.
    private static void RegisterSampleWorkflow(IWorkflowRegistry registry) =>
        registry.Register(new WorkflowBuilder(SampleWorkflow)
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
}
