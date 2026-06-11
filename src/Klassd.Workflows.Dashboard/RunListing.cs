using Klassd.Workflows.Core.Abstractions;
using Klassd.Workflows.Core.Model;

namespace Klassd.Workflows.Dashboard;

/// <summary>Coarse status bucket shown across Home and the Runs views.</summary>
public enum RunState { Pending, Running, Success, Failed }

/// <summary>
/// A single row in a run list — either a standalone job execution or a whole
/// workflow run (a workflow counts as one "job" here). Workflow node executions
/// are rolled up into their run and never appear on their own.
/// </summary>
public sealed record RunRow(
    string Id,
    string Name,
    string Href,
    RunState State,
    string StatusText,
    int Progress,
    string? Detail,
    string Executor,
    DateTimeOffset CreatedAt,
    bool CanStop)
{
    public string Badge => State switch
    {
        RunState.Success => "badge-success",
        RunState.Failed => "badge-error",
        RunState.Running => "badge-primary",
        _ => "badge-ghost",
    };

    public string ProgressClass => State switch
    {
        RunState.Success => "progress-success",
        RunState.Failed => "progress-error",
        RunState.Running => "progress-primary",
        _ => "",
    };
}

/// <summary>Builds the unified run list from the store.</summary>
public static class RunListing
{
    public static async Task<IReadOnlyList<RunRow>> LoadAsync(IJobStore store)
    {
        var executions = await store.ListAsync();
        var runs = await store.ListWorkflowRunsAsync();

        var jobRows = executions
            .Where(e => e.WorkflowRunId is null) // node executions roll up into their run
            .Select(FromExecution);

        var workflowRows = runs.Select(FromWorkflow);

        return jobRows.Concat(workflowRows)
            .OrderByDescending(r => r.CreatedAt)
            .ToList();
    }

    public static RunState StateOf(JobStatus s) => s switch
    {
        JobStatus.Succeeded => RunState.Success,
        JobStatus.Failed or JobStatus.Stopped => RunState.Failed,
        JobStatus.Running => RunState.Running,
        _ => RunState.Pending, // Enqueued, Starting
    };

    public static RunState StateOf(WorkflowRunStatus s) => s switch
    {
        WorkflowRunStatus.Succeeded => RunState.Success,
        WorkflowRunStatus.Failed => RunState.Failed,
        _ => RunState.Running,
    };

    private static RunRow FromExecution(JobExecution e) => new(
        e.Id, e.JobName, $"/jobs/{e.Id}", StateOf(e.Status), e.Status.ToString(),
        e.Progress, e.ProgressMessage, e.ExecutorName, e.CreatedAt, !e.IsTerminal);

    private static RunRow FromWorkflow(WorkflowRun r)
    {
        var done = r.Nodes.Count(n => n.IsTerminal);
        var progress = r.Nodes.Count == 0 ? 0 : (int)Math.Round(100.0 * done / r.Nodes.Count);
        return new RunRow(
            r.Id, r.DefinitionName, $"/workflows/runs/{r.Id}", StateOf(r.Status), r.Status.ToString(),
            progress, $"{done}/{r.Nodes.Count} nodes", "workflow", r.CreatedAt, CanStop: false);
    }
}
