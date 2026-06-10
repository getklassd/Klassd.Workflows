using Klassd.Workflows.Abstractions;
using Klassd.Workflows.Core.Model;

namespace Klassd.Workflows.Core.Abstractions;

/// <summary>The public API apps use to enqueue and schedule jobs from code.</summary>
public interface IJobScheduler
{
    Task<string> EnqueueAsync(string jobTypeName, Dictionary<string, string>? args = null);
    Task<string> EnqueueAsync<TJob>(Dictionary<string, string>? args = null) where TJob : IJob;

    void AddOrUpdateRecurring(string id, string jobTypeName, string cron, Dictionary<string, string>? args = null);
    void AddOrUpdateRecurring<TJob>(string id, string cron, Dictionary<string, string>? args = null) where TJob : IJob;

    Task StopAsync(string executionId);

    /// <summary>Start a run of a registered workflow DAG. Returns the run id.</summary>
    Task<string> EnqueueWorkflowAsync(string workflowName);

    /// <summary>Schedule a workflow DAG to run on a cron expression.</summary>
    void AddOrUpdateRecurringWorkflow(string id, string workflowName, string cron);
}
