namespace Klassd.Workflows.Core.Model;

/// <summary>A DAG of job nodes, registered in code via <c>WorkflowBuilder</c>.</summary>
public sealed class WorkflowDefinition
{
    public string Name { get; init; } = "";
    public IReadOnlyList<WorkflowNode> Nodes { get; init; } = Array.Empty<WorkflowNode>();

    public WorkflowNode? Node(string name) => Nodes.FirstOrDefault(n => n.Name == name);
}

/// <summary>
/// One node in the DAG. Runs <see cref="JobTypeName"/> once it's unblocked,
/// or once per item when fanning out. Mirrors an Argo dag task.
/// </summary>
public sealed class WorkflowNode
{
    public string Name { get; init; } = "";

    /// <summary>The <c>IJob</c> type to run (worker image). Empty when <see cref="Container"/> is set.</summary>
    public string JobTypeName { get; init; } = "";

    /// <summary>When set, this node runs an arbitrary container image instead of an <c>IJob</c>.</summary>
    public ContainerSpec? Container { get; init; }

    /// <summary>
    /// Init containers run (to completion, in order) before this node's pod starts — for both
    /// <c>IJob</c> and container nodes. Combined with any container-level
    /// (<see cref="Model.ContainerSpec.InitContainers"/>) and executor-wide init containers.
    /// </summary>
    public IReadOnlyList<InitContainerSpec> InitContainers { get; init; } = Array.Empty<InitContainerSpec>();

    /// <summary>Pod-level volumes for this node's pod (any node kind). Combined with container-level and executor-wide volumes.</summary>
    public IReadOnlyList<VolumeSpec> Volumes { get; init; } = Array.Empty<VolumeSpec>();

    /// <summary>Volumes mounted into this node's main container (the worker, or the container image). Combined with executor-wide mounts.</summary>
    public IReadOnlyList<VolumeMountSpec> VolumeMounts { get; init; } = Array.Empty<VolumeMountSpec>();

    /// <summary>Container security context for this node's main container; null falls back to the executor-wide default.</summary>
    public SecurityContextSpec? SecurityContext { get; init; }

    /// <summary>Pod security context for this node's pod; null falls back to the executor-wide default.</summary>
    public PodSecurityContextSpec? PodSecurityContext { get; init; }

    /// <summary>ConfigMaps/Secrets imported as environment variables into this node's main container.</summary>
    public IReadOnlyList<EnvFromSpec> EnvFrom { get; init; } = Array.Empty<EnvFromSpec>();

    /// <summary>Node-label selector for this node's pod; merged over the executor-wide default.</summary>
    public IReadOnlyDictionary<string, string> NodeSelector { get; init; } = new Dictionary<string, string>();

    /// <summary>Tolerations for this node's pod; combined with the executor-wide ones.</summary>
    public IReadOnlyList<TolerationSpec> Tolerations { get; init; } = Array.Empty<TolerationSpec>();

    /// <summary>Affinity for this node's pod; overrides the executor-wide default when set.</summary>
    public AffinitySpec? Affinity { get; init; }

    /// <summary>Declarative file outputs: read after the node runs and published as node outputs (with defaults).</summary>
    public IReadOnlyList<OutputSpec> FileOutputs { get; init; } = Array.Empty<OutputSpec>();

    /// <summary>
    /// A long-running "service" (daemon) node: it starts, becomes ready, and keeps running while
    /// dependents use it; the engine tears it down once the rest of the run finishes. Readiness
    /// (not exit) satisfies dependents. Mirrors an Argo <c>daemon</c> template.
    /// </summary>
    public bool IsService { get; init; }

    /// <summary>Node names that must succeed before this one starts (Argo <c>dependencies</c>).</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();

    /// <summary>Static arguments passed to every execution of this node.</summary>
    public IReadOnlyDictionary<string, string> Arguments { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Maps this node's argument name → "sourceNode.outputKey". Resolved from the
    /// upstream node's outputs at start time (Argo <c>inputs.parameters</c>).
    /// </summary>
    public IReadOnlyDictionary<string, string> InputBindings { get; init; } = new Dictionary<string, string>();

    /// <summary>When set, the node fans out into one execution per item (Argo <c>withParam</c>).</summary>
    public FanOutSpec? FanOut { get; init; }

    /// <summary>Failed executions are retried up to this many times (Argo <c>retryStrategy.limit</c>).</summary>
    public int MaxRetries { get; init; }

    /// <summary>
    /// Optional gate evaluated against upstream outputs (Argo <c>when</c>). If it
    /// returns false the node is <see cref="NodeRunStatus.Omitted"/> — benignly
    /// skipped, and its dependents still run.
    /// </summary>
    public Func<IWorkflowOutputs, bool>? Condition { get; init; }

    public int MaxAttempts => MaxRetries + 1;
}

/// <summary>
/// Fan-out: read <see cref="SourceNode"/>'s output <see cref="OutputKey"/> as a
/// JSON array and start one execution per element, exposing each element as the
/// argument named <see cref="ItemArgument"/>. <see cref="MaxParallelism"/> caps how many
/// of those executions run at once (0 = unlimited) so a large list doesn't spawn N pods at once.
/// </summary>
public sealed record FanOutSpec(string SourceNode, string OutputKey, string ItemArgument, int MaxParallelism = 0);

/// <summary>Read-only view of completed nodes' outputs, passed to a node's <c>when</c> condition.</summary>
public interface IWorkflowOutputs
{
    /// <summary>The named output of a node, or null if absent.</summary>
    string? Get(string node, string key);
}
