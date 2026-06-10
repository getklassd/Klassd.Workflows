using System.Collections.Concurrent;
using Klassd.Workflows.Core.Abstractions;
using Klassd.Workflows.Core.Model;

namespace Klassd.Workflows.Core.Storage;

/// <summary>Default thread-safe in-memory store. Replace with SQL/Redis for durability.</summary>
public sealed class InMemoryJobStore : IJobStore
{
    private readonly ConcurrentDictionary<string, JobExecution> _executions = new();
    private readonly ConcurrentDictionary<string, RecurringJob> _recurring = new();
    private readonly ConcurrentDictionary<string, WorkflowRun> _workflowRuns = new();

    public event Action<JobExecution>? ExecutionChanged;
    public event Action<WorkflowRun>? WorkflowChanged;

    public Task<JobExecution> CreateAsync(JobDescriptor descriptor, string executorName)
    {
        var exec = new JobExecution
        {
            JobName = descriptor.JobName,
            JobTypeName = descriptor.JobTypeName,
            Arguments = new Dictionary<string, string>(descriptor.Arguments),
            ExecutorName = executorName,
            Status = JobStatus.Enqueued
        };
        _executions[exec.Id] = exec;
        ExecutionChanged?.Invoke(exec);
        return Task.FromResult(exec);
    }

    public Task<JobExecution?> GetAsync(string id) =>
        Task.FromResult(_executions.GetValueOrDefault(id));

    public Task<IReadOnlyList<JobExecution>> ListAsync(int limit = 200)
    {
        IReadOnlyList<JobExecution> list = _executions.Values
            .OrderByDescending(e => e.CreatedAt)
            .Take(limit)
            .ToList();
        return Task.FromResult(list);
    }

    public Task UpdateAsync(JobExecution execution)
    {
        _executions[execution.Id] = execution;
        ExecutionChanged?.Invoke(execution);
        return Task.CompletedTask;
    }

    public Task AppendLogAsync(string id, string line)
    {
        if (_executions.TryGetValue(id, out var exec))
        {
            lock (exec.Logs) exec.Logs.Add(line);
            ExecutionChanged?.Invoke(exec);
        }
        return Task.CompletedTask;
    }

    public Task UpsertRecurringAsync(RecurringJob job)
    {
        _recurring[job.Id] = job;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RecurringJob>> ListRecurringAsync()
    {
        IReadOnlyList<RecurringJob> list = _recurring.Values.OrderBy(r => r.Id).ToList();
        return Task.FromResult(list);
    }

    public Task RemoveRecurringAsync(string id)
    {
        _recurring.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task SaveWorkflowRunAsync(WorkflowRun run)
    {
        _workflowRuns[run.Id] = run;
        WorkflowChanged?.Invoke(run);
        return Task.CompletedTask;
    }

    public Task<WorkflowRun?> GetWorkflowRunAsync(string id) =>
        Task.FromResult(_workflowRuns.GetValueOrDefault(id));

    public Task<IReadOnlyList<WorkflowRun>> ListWorkflowRunsAsync(int limit = 100)
    {
        IReadOnlyList<WorkflowRun> list = _workflowRuns.Values
            .OrderByDescending(r => r.CreatedAt)
            .Take(limit)
            .ToList();
        return Task.FromResult(list);
    }
}
