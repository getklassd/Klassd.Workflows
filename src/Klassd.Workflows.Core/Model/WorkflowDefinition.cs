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
/// argument named <see cref="ItemArgument"/>.
/// </summary>
public sealed record FanOutSpec(string SourceNode, string OutputKey, string ItemArgument);

/// <summary>Read-only view of completed nodes' outputs, passed to a node's <c>when</c> condition.</summary>
public interface IWorkflowOutputs
{
    /// <summary>The named output of a node, or null if absent.</summary>
    string? Get(string node, string key);
}
