using Klassd.Workflows.Core.Model;

namespace Klassd.Workflows.Core.Abstractions;

/// <summary>
/// Persistence for executions and recurring definitions. The in-memory
/// implementation is the default; swap for SQL/Redis later without touching
/// the scheduler or UI.
/// </summary>
public interface IJobStore
{
    Task<JobExecution> CreateAsync(JobDescriptor descriptor, string executorName);
    Task<JobExecution?> GetAsync(string id);
    Task<IReadOnlyList<JobExecution>> ListAsync(int limit = 200);
    Task UpdateAsync(JobExecution execution);
    Task AppendLogAsync(string id, string line);

    Task UpsertRecurringAsync(RecurringJob job);
    Task<IReadOnlyList<RecurringJob>> ListRecurringAsync();
    Task RemoveRecurringAsync(string id);

    // --- workflow runs (DAGs) ---
    Task SaveWorkflowRunAsync(WorkflowRun run);
    Task<WorkflowRun?> GetWorkflowRunAsync(string id);
    Task<IReadOnlyList<WorkflowRun>> ListWorkflowRunsAsync(int limit = 100);

    /// <summary>Raised whenever an execution changes; the UI subscribes for live updates.</summary>
    event Action<JobExecution>? ExecutionChanged;

    /// <summary>Raised whenever a workflow run changes.</summary>
    event Action<WorkflowRun>? WorkflowChanged;
}
