using Klassd.Workflows.Core.Model;

namespace Klassd.Workflows.Core.Abstractions;

/// <summary>
/// Runs a job somewhere and feeds its log/progress/state back into the store.
/// Implementations: <c>LocalProcessJobExecutor</c> (dev) and
/// <c>KubernetesJobExecutor</c> (spins up a pod per job).
/// </summary>
public interface IJobExecutor
{
    /// <summary>Display name, e.g. "kubernetes" or "local".</summary>
    string Name { get; }

    /// <summary>Start the run. Returns once the work has been dispatched (not when it finishes).</summary>
    Task StartAsync(JobExecution execution, CancellationToken ct = default);

    /// <summary>Stop/kill a running job (delete pod / kill process).</summary>
    Task StopAsync(JobExecution execution, CancellationToken ct = default);
}
