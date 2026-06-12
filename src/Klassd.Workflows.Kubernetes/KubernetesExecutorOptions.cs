using Klassd.Workflows.Abstractions;
using Klassd.Workflows.Core.Model;

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

    /// <summary>
    /// Hard ceiling (<c>activeDeadlineSeconds</c>) applied to long-running service/daemon Jobs only.
    /// The orchestrator normally tears a service down when the run ends; this is the safety backstop
    /// so an orphaned daemon (e.g. the scheduler died mid-run) is still reaped by Kubernetes after at
    /// most this long. Set null to disable. Preferred over an owner reference, which would kill all
    /// running daemons on every scheduler rolling-restart. Default 6 hours.
    /// </summary>
    public int? ServiceActiveDeadlineSeconds { get; set; } = 21_600;

    /// <summary>Optional service account for the worker pod.</summary>
    public string? ServiceAccountName { get; set; }

    /// <summary>
    /// Extra annotations stamped onto every job/proxy <b>pod</b> (the pod template, not the Job).
    /// This is the seam for sidecar injectors that key off pod annotations — e.g. the Vault agent
    /// (<c>vault.hashicorp.com/agent-inject: "true"</c>, <c>.../role</c>, <c>.../agent-inject-secret-*</c>)
    /// writing <c>/secrets/*.json</c> into the pod. Bound from <c>Klassd.Workflows:PodAnnotations</c>.
    /// </summary>
    public Dictionary<string, string> PodAnnotations { get; set; } = new();

    /// <summary>
    /// Extra labels stamped onto every job/proxy pod, merged with the engine's own
    /// (<c>app</c>, <c>klassd-workflows/execution</c>); the engine's labels win on a key clash.
    /// Bound from <c>Klassd.Workflows:PodLabels</c>.
    /// </summary>
    public Dictionary<string, string> PodLabels { get; set; } = new();

    /// <summary>
    /// Init containers prepended to <b>every</b> job/proxy pod the engine creates (before any
    /// node- or container-level init containers). The cross-cutting seam for pre-flight steps on
    /// all pods — e.g. a secrets-fetch or schema-check container. Bound from
    /// <c>Klassd.Workflows:InitContainers</c>.
    /// </summary>
    public List<InitContainerSpec> InitContainers { get; set; } = new();

    /// <summary>
    /// Volumes added to <b>every</b> job/proxy pod (combined with node- and container-level volumes).
    /// Bound from <c>Klassd.Workflows:Volumes</c>.
    /// </summary>
    public List<VolumeSpec> Volumes { get; set; } = new();

    /// <summary>
    /// Volume mounts added to every pod's <b>main</b> container (worker or container image). Init
    /// containers mount via their own <see cref="InitContainerSpec.VolumeMounts"/>. Bound from
    /// <c>Klassd.Workflows:VolumeMounts</c>.
    /// </summary>
    public List<VolumeMountSpec> VolumeMounts { get; set; } = new();

    /// <summary>
    /// Default pod security context for every pod; overridden per node/container. Bound from
    /// <c>Klassd.Workflows:PodSecurityContext</c>.
    /// </summary>
    public PodSecurityContextSpec? PodSecurityContext { get; set; }

    /// <summary>
    /// Default container security context for every container (main + init); overridden per
    /// container/init/node. Bound from <c>Klassd.Workflows:ContainerSecurityContext</c>.
    /// </summary>
    public SecurityContextSpec? ContainerSecurityContext { get; set; }

    /// <summary>
    /// ConfigMaps/Secrets imported as environment variables into every pod's <b>main</b> container.
    /// Init containers import via their own <see cref="InitContainerSpec.EnvFrom"/>. Bound from
    /// <c>Klassd.Workflows:EnvFrom</c>.
    /// </summary>
    public List<EnvFromSpec> EnvFrom { get; set; } = new();

    /// <summary>Default node selector merged into every pod (per node/container additions win). Bound from <c>Klassd.Workflows:NodeSelector</c>.</summary>
    public Dictionary<string, string> NodeSelector { get; set; } = new();

    /// <summary>Tolerations added to every pod (combined with per node/container ones). Bound from <c>Klassd.Workflows:Tolerations</c>.</summary>
    public List<TolerationSpec> Tolerations { get; set; } = new();

    /// <summary>Default pod affinity; overridden per node/container. Bound from <c>Klassd.Workflows:Affinity</c>.</summary>
    public AffinitySpec? Affinity { get; set; }

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
