using System.Text.Json.Serialization;

namespace Klassd.Workflows.Core.Model;

/// <summary>A single run of a job, with its live log buffer and progress.</summary>
public sealed class JobExecution
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");
    public string JobName { get; set; } = "";
    public string JobTypeName { get; set; } = "";
    public Dictionary<string, string> Arguments { get; set; } = new();

    public JobStatus Status { get; set; } = JobStatus.Enqueued;
    public int Progress { get; set; }
    public string? ProgressMessage { get; set; }
    public string? Error { get; set; }

    /// <summary>Executor-specific handle: pod name (K8s) or process id (local).</summary>
    public string? ExecutorHandle { get; set; }
    public string ExecutorName { get; set; } = "";

    /// <summary>
    /// Live log buffer. Persisted separately (append-only) by durable stores, so
    /// it is excluded from the serialized execution document.
    /// </summary>
    [JsonIgnore]
    public List<string> Logs { get; } = new();

    /// <summary>Named outputs the job published via <c>IJobContext.SetOutput</c>.</summary>
    public Dictionary<string, string> Outputs { get; set; } = new();

    /// <summary>
    /// True once a long-running service job has signalled it's up (<c>IJobContext.SignalReady</c>).
    /// The execution stays <see cref="JobStatus.Running"/>; the DAG uses this to unblock dependents.
    /// </summary>
    public bool Ready { get; set; }
    public DateTimeOffset? ReadyAt { get; set; }

    /// <summary>Long-running service (daemon) node — the executor keeps it running and the engine tears it down.</summary>
    public bool IsService { get; set; }

    /// <summary>When set, this execution runs an arbitrary container image instead of an <c>IJob</c> via the worker.</summary>
    public ContainerSpec? Container { get; set; }

    /// <summary>
    /// Node-level init containers for this execution (from <see cref="WorkflowNode.InitContainers"/>).
    /// The executor also adds any container-level (<see cref="ContainerSpec.InitContainers"/>) and
    /// executor-wide init containers; this carries the node-level ones.
    /// </summary>
    public List<InitContainerSpec> InitContainers { get; set; } = new();

    /// <summary>Node-level pod volumes (from <see cref="WorkflowNode.Volumes"/>).</summary>
    public List<VolumeSpec> Volumes { get; set; } = new();

    /// <summary>Node-level main-container volume mounts (from <see cref="WorkflowNode.VolumeMounts"/>).</summary>
    public List<VolumeMountSpec> VolumeMounts { get; set; } = new();

    /// <summary>Node-level main-container security context (from <see cref="WorkflowNode.SecurityContext"/>).</summary>
    public SecurityContextSpec? SecurityContext { get; set; }

    /// <summary>Node-level pod security context (from <see cref="WorkflowNode.PodSecurityContext"/>).</summary>
    public PodSecurityContextSpec? PodSecurityContext { get; set; }

    /// <summary>Node-level envFrom sources for the main container (from <see cref="WorkflowNode.EnvFrom"/>).</summary>
    public List<EnvFromSpec> EnvFrom { get; set; } = new();

    /// <summary>Node-level node selector (from <see cref="WorkflowNode.NodeSelector"/>).</summary>
    public Dictionary<string, string> NodeSelector { get; set; } = new();

    /// <summary>Node-level tolerations (from <see cref="WorkflowNode.Tolerations"/>).</summary>
    public List<TolerationSpec> Tolerations { get; set; } = new();

    /// <summary>Node-level affinity (from <see cref="WorkflowNode.Affinity"/>).</summary>
    public AffinitySpec? Affinity { get; set; }

    /// <summary>Declarative file outputs to capture after this execution runs (from the node / container spec).</summary>
    public List<OutputSpec> FileOutputs { get; set; } = new();

    // --- DAG linkage (null for standalone jobs) ---
    public string? WorkflowRunId { get; set; }
    public string? NodeName { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    public bool IsTerminal =>
        Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Stopped;
}
