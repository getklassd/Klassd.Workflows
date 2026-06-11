using System.IO.Pipes;

namespace Klassd.Workflows.IntegrationTests;

/// <summary>
/// Skips the Kubernetes-executor tests unless opted into. They build a worker image and spin up
/// a real single-node cluster (K3s, via Testcontainers) — minutes of work — so a normal
/// <c>dotnet run</c> skips them. Enable with <c>KLASSD_K8S_IT=1</c> and a reachable Docker daemon.
/// </summary>
public sealed class RequiresKubernetesAttribute() : SkipAttribute("Set KLASSD_K8S_IT=1 with Docker available to run")
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(!KubernetesGate.Enabled);
}

internal static class KubernetesGate
{
    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("KLASSD_K8S_IT") == "1" && DockerAvailable();

    private static bool DockerAvailable()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                return File.Exists("/var/run/docker.sock")
                       || Environment.GetEnvironmentVariable("DOCKER_HOST") is { Length: > 0 };
            using var pipe = new NamedPipeClientStream(".", "docker_engine", PipeDirection.InOut);
            pipe.Connect(500);
            return true;
        }
        catch { return false; }
    }
}
