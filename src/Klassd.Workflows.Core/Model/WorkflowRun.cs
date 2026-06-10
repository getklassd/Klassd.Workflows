namespace Klassd.Workflows.Core.Model;

public enum WorkflowRunStatus { Running, Succeeded, Failed }

/// <summary>
/// Pending → Running → (Succeeded | Failed). <c>Skipped</c> = blocked by a failed
/// dependency (run fails). <c>Omitted</c> = a <c>when</c> condition was false
/// (benign; dependents still run).
/// </summary>
public enum NodeRunStatus { Pending, Running, Succeeded, Failed, Skipped, Omitted }

/// <summary>A single execution of a <see cref="WorkflowDefinition"/>.</summary>
public sealed class WorkflowRun
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");
    public string DefinitionName { get; init; } = "";
    public WorkflowRunStatus Status { get; set; } = WorkflowRunStatus.Running;
    public List<NodeRun> Nodes { get; init; } = new();

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }

    public bool IsTerminal => Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed;

    public NodeRun? Node(string name) => Nodes.FirstOrDefault(n => n.Name == name);
}

/// <summary>
/// Per-node state within a run. A node owns one <see cref="NodeTask"/> normally,
/// or one per item when fanning out. Each task can have several attempts (retries).
/// </summary>
public sealed class NodeRun
{
    public string Name { get; init; } = "";
    public string JobTypeName { get; init; } = "";
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
    public bool IsFanOut { get; init; }

    public NodeRunStatus Status { get; set; } = NodeRunStatus.Pending;

    public List<NodeTask> Tasks { get; init; } = new();

    /// <summary>All execution ids across all tasks and attempts (newest last).</summary>
    public IEnumerable<string> ExecutionIds => Tasks.SelectMany(t => t.Attempts);

    public bool IsTerminal =>
        Status is NodeRunStatus.Succeeded or NodeRunStatus.Failed
            or NodeRunStatus.Skipped or NodeRunStatus.Omitted;

    /// <summary>True once this node satisfies a dependency (succeeded or benignly omitted).</summary>
    public bool SatisfiesDependents =>
        Status is NodeRunStatus.Succeeded or NodeRunStatus.Omitted;
}

/// <summary>One unit of work within a node (a fan-out item, or the sole task), with its retry attempts.</summary>
public sealed class NodeTask
{
    public Dictionary<string, string> Arguments { get; init; } = new();

    /// <summary>Execution ids for each attempt; the last entry is the current attempt.</summary>
    public List<string> Attempts { get; init; } = new();

    public string? Current => Attempts.Count > 0 ? Attempts[^1] : null;
}
