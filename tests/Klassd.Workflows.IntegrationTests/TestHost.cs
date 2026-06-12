using Klassd.Workflows.Core;
using Klassd.Workflows.Core.Abstractions;
using Klassd.Workflows.Core.Model;
using Klassd.Workflows.Core.Workflows;
using Klassd.Workflows.Kubernetes;
using Klassd.Workflows.SampleJobs;
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
    public const string ContainerServiceWorkflow = "container-service";

    private static KubernetesCluster? _cluster;
    private static MinioStore? _minio;
    private static ServiceProvider? _provider;
    private static readonly List<IHostedService> Hosted = [];

    public static IJobScheduler Scheduler => _provider!.GetRequiredService<IJobScheduler>();
    public static IJobStore Store => _provider!.GetRequiredService<IJobStore>();

    /// <summary>Kubeconfig for the shared K3s cluster — lets tests create fixtures (e.g. a ConfigMap) directly.</summary>
    public static string KubeConfigPath => _cluster!.KubeConfigPath;
    public const string Namespace = "default";

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

            // Executor-wide init container stamped onto every pod (worker, container, service). A
            // no-op busybox that just has to succeed — so the whole suite passing proves executor-wide
            // init containers are accepted and don't break any pod type.
            o.InitContainers.Add(new InitContainerSpec
            {
                Name = "preflight",
                Image = KubernetesCluster.BusyboxImage,
                Command = new[] { "sh", "-c" },
                Args = new[] { "echo preflight-ok" },
                ImagePullPolicy = "IfNotPresent",   // pulled by the cluster, not side-loaded
            });

            // Executor-wide pod security context on every pod — a safe default (RuntimeDefault
            // seccomp doesn't break the worker/busybox), so the whole suite passing proves the
            // global path works on all pod types.
            o.PodSecurityContext = new PodSecurityContextSpec { SeccompProfileType = "RuntimeDefault" };
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
        var registry = _provider.GetRequiredService<IWorkflowRegistry>();
        RegisterSampleWorkflow(registry);
        RegisterContainerServiceWorkflow(registry);

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

    // An arbitrary-image (nginx) node run as a long-running SERVICE: the executor publishes its
    // address, readiness is gated on the TCP probe, a dependent IJob binds the forwarded address,
    // and the orchestrator tears the service down once the work node finishes. Exercises the real
    // K8s container-node path (BuildContainerJob + readinessProbe + FollowContainerAsync) end-to-end.
    private static void RegisterContainerServiceWorkflow(IWorkflowRegistry registry) =>
        registry.Register(new WorkflowBuilder(ContainerServiceWorkflow)
            .AddContainer("web", KubernetesCluster.NginxImage, c => c
                .AsService()
                .ServicePort(80)
                .ReadyOnTcp(80)
                .WithImagePullPolicy("IfNotPresent"))   // pulled by the cluster, override the host's global Never
            .Add<HelloWorldJob>("consumer", n => n
                .DependsOn("web")
                .BindInput("name", "web", "address"))
            .Build());
}
