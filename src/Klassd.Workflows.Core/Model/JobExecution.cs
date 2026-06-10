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

    // --- DAG linkage (null for standalone jobs) ---
    public string? WorkflowRunId { get; set; }
    public string? NodeName { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    public bool IsTerminal =>
        Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Stopped;
}
