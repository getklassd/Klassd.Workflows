using Klassd.Workflows.Abstractions;
using Klassd.Workflows.Core.Abstractions;
using Klassd.Workflows.Core.Model;
using Microsoft.Extensions.Logging;

namespace Klassd.Workflows.Core.Execution;

public sealed class JobScheduler : IJobScheduler
{
    private readonly IJobStore _store;
    private readonly IJobExecutor _executor;
    private readonly IWorkflowOrchestrator _workflows;
    private readonly ILogger<JobScheduler> _logger;

    public JobScheduler(IJobStore store, IJobExecutor executor,
        IWorkflowOrchestrator workflows, ILogger<JobScheduler> logger)
    {
        _store = store;
        _executor = executor;
        _workflows = workflows;
        _logger = logger;
    }

    public async Task<string> EnqueueAsync(string jobTypeName, Dictionary<string, string>? args = null)
    {
        var name = jobTypeName.Contains('.') ? jobTypeName[(jobTypeName.LastIndexOf('.') + 1)..] : jobTypeName;
        var descriptor = new JobDescriptor(name, jobTypeName, args ?? new());
        var exec = await _store.CreateAsync(descriptor, _executor.Name);
        _logger.LogInformation("Enqueued {Job} as {Id}", name, exec.Id);
        await _executor.StartAsync(exec);
        return exec.Id;
    }

    public Task<string> EnqueueAsync<TJob>(Dictionary<string, string>? args = null) where TJob : IJob =>
        EnqueueAsync(typeof(TJob).FullName!, args);

    public void AddOrUpdateRecurring(string id, string jobTypeName, string cron, Dictionary<string, string>? args = null)
    {
        _store.UpsertRecurringAsync(new RecurringJob
        {
            Id = id,
            JobTypeName = jobTypeName,
            Cron = cron,
            Arguments = args ?? new()
        }).GetAwaiter().GetResult();
        _logger.LogInformation("Registered recurring job {Id} ({Cron})", id, cron);
    }

    public void AddOrUpdateRecurring<TJob>(string id, string cron, Dictionary<string, string>? args = null)
        where TJob : IJob =>
        AddOrUpdateRecurring(id, typeof(TJob).FullName!, cron, args);

    public async Task StopAsync(string executionId)
    {
        var exec = await _store.GetAsync(executionId);
        if (exec is null || exec.IsTerminal) return;
        await _executor.StopAsync(exec);
    }

    public Task<string> EnqueueWorkflowAsync(string workflowName) =>
        _workflows.StartAsync(workflowName);

    public void AddOrUpdateRecurringWorkflow(string id, string workflowName, string cron)
    {
        _store.UpsertRecurringAsync(new RecurringJob
        {
            Id = id,
            Kind = RecurringKind.Workflow,
            WorkflowName = workflowName,
            Cron = cron
        }).GetAwaiter().GetResult();
        _logger.LogInformation("Registered recurring workflow {Id} ({Cron})", id, cron);
    }
}
