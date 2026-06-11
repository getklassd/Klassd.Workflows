using Klassd.Workflows.Abstractions;

namespace Klassd.Workflows.Kubernetes;

public sealed class KubernetesExecutorOptions
{
    /// <summary>Container image that contains the worker + job assemblies.</summary>
    public string WorkerImage { get; set; } = "klassd-workflows-worker:latest";

    /// <summary>Namespace the per-job Kubernetes Jobs are created in.</summary>
    public string Namespace { get; set; } = "default";

    /// <summary>
    /// Container imagePullPolicy ("Always" | "IfNotPresent" | "Never"). Null leaves it unset, so
    /// Kubernetes applies its default (Always for a <c>:latest</c> tag, IfNotPresent otherwise).
    /// Set "Never" when the image is pre-loaded onto the node (e.g. a local kind/K3s cluster).
    /// </summary>
    public string? ImagePullPolicy { get; set; }

    /// <summary>Use in-cluster service account config (true when the scheduler itself runs in K8s).</summary>
    public bool InCluster { get; set; }

    /// <summary>Explicit kubeconfig path. When null and not in-cluster, the default kubeconfig is used.</summary>
    public string? KubeConfigPath { get; set; }

    /// <summary>Seconds a finished Job lingers before Kubernetes garbage-collects it.</summary>
    public int TtlSecondsAfterFinished { get; set; } = 300;

    /// <summary>Optional service account for the worker pod.</summary>
    public string? ServiceAccountName { get; set; }

    /// <summary>
    /// Directory the "file" artifact provider uses. For cross-pod sharing this
    /// must be a ReadWriteMany volume mounted at the same path in every worker;
    /// for production prefer an object-storage provider (gcs/s3) instead.
    /// </summary>
    public string? ArtifactDir { get; set; }

    /// <summary>Artifact provider name the worker should use (file | gcs | s3 | custom).</summary>
    public string ArtifactProvider { get; set; } = "file";

    /// <summary>Provider-specific artifact settings (e.g. bucket, prefix, project).</summary>
    public Dictionary<string, string> ArtifactSettings { get; set; } = new();

    /// <summary>
    /// Fallback resources applied to every job that doesn't specify its own.
    /// Bound from appsettings, e.g. <c>Klassd.Workflows:DefaultResources</c>.
    /// </summary>
    public JobResourceRequirements? DefaultResources { get; set; }

    /// <summary>
    /// Per-job overrides keyed by the job's full type name (short name also
    /// matched as a fallback). Bound from <c>Klassd.Workflows:Resources</c>. These win
    /// over both the default and the job's <see cref="JobResourcesAttribute"/>,
    /// so secrets managers like Vault can retune resources without a rebuild.
    /// </summary>
    public Dictionary<string, JobResourceRequirements> Resources { get; set; } = new();
}
