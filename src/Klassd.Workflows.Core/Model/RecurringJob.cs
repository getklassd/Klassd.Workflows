namespace Klassd.Workflows.Core.Model;

public enum RecurringKind { Job, Workflow, Container }

/// <summary>A cron-scheduled job, container job, or workflow registered through code.</summary>
public sealed class RecurringJob
{
    public string Id { get; set; } = "";
    public RecurringKind Kind { get; set; } = RecurringKind.Job;

    /// <summary>For <see cref="RecurringKind.Job"/>: the job type to enqueue. For
    /// <see cref="RecurringKind.Container"/>: the job's display name.</summary>
    public string JobTypeName { get; set; } = "";

    /// <summary>For <see cref="RecurringKind.Workflow"/>: the workflow definition to run.</summary>
    public string WorkflowName { get; set; } = "";

    /// <summary>For <see cref="RecurringKind.Container"/>: the image to run.</summary>
    public ContainerSpec? Container { get; set; }

    public string Cron { get; set; } = "";
    public Dictionary<string, string> Arguments { get; set; } = new();
    public bool Enabled { get; set; } = true;

    public DateTimeOffset? LastRun { get; set; }

    /// <summary>Last job execution id, or workflow run id for workflow kind.</summary>
    public string? LastExecutionId { get; set; }

    public string Target => Kind == RecurringKind.Workflow ? WorkflowName : JobTypeName;
}
