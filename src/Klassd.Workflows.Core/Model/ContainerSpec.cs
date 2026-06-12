using Klassd.Workflows.Abstractions;

namespace Klassd.Workflows.Core.Model;

/// <summary>
/// Describes an arbitrary container image to run as a DAG node (instead of one of our
/// <c>IJob</c> classes). Used e.g. to run a <c>cloud-sql-proxy</c> sidecar as a long-running
/// service node whose address is forwarded to dependents. The node's resolved arguments
/// (static + bound from upstream outputs) are passed to the container as environment variables.
/// </summary>
public sealed class ContainerSpec
{
    public string Image { get; init; } = "";

    /// <summary>Entrypoint override (container <c>command</c>); empty = use the image's entrypoint.</summary>
    public IReadOnlyList<string> Command { get; init; } = Array.Empty<string>();

    /// <summary>Container <c>args</c>.</summary>
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

    /// <summary>Static environment variables for the container (merged with the node's resolved arguments).</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Port the container serves on. When set, the engine publishes <c>address</c> = <c>&lt;ip&gt;:&lt;port&gt;</c>
    /// (and <c>ip</c>) as node outputs so dependents can connect. Required for a useful service node.
    /// </summary>
    public int? ServicePort { get; init; }

    /// <summary>
    /// If set, the node is considered ready only once this TCP port accepts connections (implemented
    /// as a Kubernetes <c>tcpSocket</c> readiness probe / a TCP poll under the local docker shim).
    /// Defaults to <see cref="ServicePort"/> behaviour of "ready when Running" when null.
    /// </summary>
    public int? ReadyTcpPort { get; init; }

    /// <summary>Container imagePullPolicy ("Always" | "IfNotPresent" | "Never"); null leaves K8s' default.</summary>
    public string? ImagePullPolicy { get; init; }

    /// <summary>
    /// Init containers run (to completion, in order) before this container starts. Applies to
    /// standalone container jobs, recurring container jobs, and container DAG nodes. Combined with
    /// any node-level (<see cref="WorkflowNode.InitContainers"/>) and executor-wide init containers.
    /// </summary>
    public IReadOnlyList<InitContainerSpec> InitContainers { get; init; } = Array.Empty<InitContainerSpec>();

    /// <summary>Pod-level volumes for this container's pod. Combined with node-level and executor-wide volumes.</summary>
    public IReadOnlyList<VolumeSpec> Volumes { get; init; } = Array.Empty<VolumeSpec>();

    /// <summary>Volumes mounted into this (main) container. Combined with node-level and executor-wide mounts.</summary>
    public IReadOnlyList<VolumeMountSpec> VolumeMounts { get; init; } = Array.Empty<VolumeMountSpec>();

    /// <summary>Container security context for this (main) container; null falls back to the node-level then executor-wide default.</summary>
    public SecurityContextSpec? SecurityContext { get; init; }

    /// <summary>Pod security context for this container's pod (standalone container jobs); null falls back to the executor-wide default.</summary>
    public PodSecurityContextSpec? PodSecurityContext { get; init; }

    /// <summary>CPU/memory requests+limits for this (main) container; overlaid on the executor's <c>DefaultResources</c>.</summary>
    public JobResourceRequirements? Resources { get; init; }

    /// <summary>ConfigMaps/Secrets imported as environment variables into this (main) container.</summary>
    public IReadOnlyList<EnvFromSpec> EnvFrom { get; init; } = Array.Empty<EnvFromSpec>();

    /// <summary>Node-label selector for this container's pod (standalone container jobs); merged over the executor-wide default.</summary>
    public IReadOnlyDictionary<string, string> NodeSelector { get; init; } = new Dictionary<string, string>();

    /// <summary>Tolerations for this container's pod; combined with the executor-wide ones.</summary>
    public IReadOnlyList<TolerationSpec> Tolerations { get; init; } = Array.Empty<TolerationSpec>();

    /// <summary>Affinity for this container's pod; overrides the executor-wide default when set.</summary>
    public AffinitySpec? Affinity { get; init; }
}
