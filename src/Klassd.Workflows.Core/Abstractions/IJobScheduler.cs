using Klassd.Workflows.Abstractions;
using Klassd.Workflows.Core.Model;

namespace Klassd.Workflows.Core.Abstractions;

/// <summary>The public API apps use to enqueue and schedule jobs from code.</summary>
public interface IJobScheduler
{
    Task<string> EnqueueAsync(string jobTypeName, Dictionary<string, string>? args = null, string? tenant = null, IReadOnlyList<InitContainerSpec>? initContainers = null);
    Task<string> EnqueueAsync<TJob>(Dictionary<string, string>? args = null, string? tenant = null, IReadOnlyList<InitContainerSpec>? initContainers = null) where TJob : IJob;

    void AddOrUpdateRecurring(string id, string jobTypeName, string cron, Dictionary<string, string>? args = null, string? tenant = null, IReadOnlyList<InitContainerSpec>? initContainers = null);
    void AddOrUpdateRecurring<TJob>(string id, string cron, Dictionary<string, string>? args = null, string? tenant = null, IReadOnlyList<InitContainerSpec>? initContainers = null) where TJob : IJob;

    /// <summary>
    /// Enqueue a standalone job that runs an arbitrary container image (not an <c>IJob</c>) — e.g. a
    /// legacy Go tool — to completion. <paramref name="name"/> is the display name. Returns the execution id.
    /// </summary>
    Task<string> EnqueueContainerAsync(string name, ContainerSpec container, Dictionary<string, string>? args = null, string? tenant = null);

    /// <summary>Schedule a container-backed standalone job on a cron expression.</summary>
    void AddOrUpdateRecurringContainer(string id, string name, ContainerSpec container, string cron, Dictionary<string, string>? args = null, string? tenant = null);

    Task StopAsync(string executionId);

    /// <summary>Start a run of a registered workflow DAG. Returns the run id.</summary>
    Task<string> EnqueueWorkflowAsync(string workflowName, string? tenant = null);

    /// <summary>Schedule a workflow DAG to run on a cron expression.</summary>
    void AddOrUpdateRecurringWorkflow(string id, string workflowName, string cron, string? tenant = null);
}
