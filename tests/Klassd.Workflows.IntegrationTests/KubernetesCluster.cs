using Docker.DotNet;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Images;
using Testcontainers.K3s;

namespace Klassd.Workflows.IntegrationTests;

/// <summary>
/// A throwaway single-node Kubernetes cluster for the executor tests, all through Testcontainers
/// (no kind/docker/kubectl CLI): start a K3s container, build the worker image from its Dockerfile,
/// and side-load it into the cluster's containerd. A registry-qualified image name plus
/// <c>imagePullPolicy=Never</c> means the kubelet uses the imported image verbatim and never tries
/// to pull it.
/// </summary>
internal sealed class KubernetesCluster : IAsyncDisposable
{
    public const string WorkerImage = "klassd.test/worker:it";

    private K3sContainer _container = null!;

    public string KubeConfigPath { get; private set; } = "";

    public static async Task<KubernetesCluster> StartAsync(CancellationToken ct = default)
    {
        var cluster = new KubernetesCluster();
        await cluster.InitAsync(ct);
        return cluster;
    }

    private async Task InitAsync(CancellationToken ct)
    {
        _container = new K3sBuilder("rancher/k3s:v1.29.4-k3s1").Build();
        await _container.StartAsync(ct);

        KubeConfigPath = Path.Combine(Path.GetTempPath(), $"k3s-{Guid.NewGuid():n}.kubeconfig");
        await File.WriteAllTextAsync(KubeConfigPath, await _container.GetKubeconfigAsync(), ct);

        await BuildAndLoadWorkerImageAsync(ct);
    }

    private async Task BuildAndLoadWorkerImageAsync(CancellationToken ct)
    {
        var repoRoot = FindRepoRoot();

        // Build the worker image from the solution-root context (the Dockerfile COPYs the repo
        // and `dotnet publish`es the worker + referenced job assemblies).
        IFutureDockerImage image = new ImageFromDockerfileBuilder()
            .WithName(WorkerImage)
            .WithDockerfileDirectory(repoRoot)
            .WithDockerfile("src/Klassd.Workflows.Worker/Dockerfile")
            .WithCleanUp(false)
            .WithDeleteIfExists(true)
            .Build();
        await image.CreateAsync(ct);

        // Export it from the host daemon and import it into the K3s node's containerd (k8s.io ns).
        // The Docker client targets Testcontainers' own resolved endpoint.
        var endpoint = TestcontainersSettings.OS.DockerEndpointAuthConfig.Endpoint;
        using var docker = new DockerClientBuilder().WithEndpoint(endpoint).Build();
        await using var tar = await docker.Images.SaveImageAsync(WorkerImage, ct);
        await using var buffer = new MemoryStream();
        await tar.CopyToAsync(buffer, ct);

        const string tarPath = "/tmp/worker-image.tar";
        await _container.CopyAsync(buffer.ToArray(), tarPath, ct: ct);

        var result = await _container.ExecAsync(["ctr", "-n", "k8s.io", "images", "import", tarPath], ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Importing the worker image into K3s failed (exit {result.ExitCode}).\n{result.Stdout}\n{result.Stderr}");
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        try { if (File.Exists(KubeConfigPath)) File.Delete(KubeConfigPath); }
        catch { /* best-effort cleanup of the temp kubeconfig */ }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Klassd.Workflows.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root (Klassd.Workflows.slnx).");
    }
}
