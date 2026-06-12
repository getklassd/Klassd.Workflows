using System.Diagnostics;
using Klassd.Workflows.Core.Abstractions;
using Klassd.Workflows.Core.Model;

namespace Klassd.Workflows.IntegrationTests;

/// <summary>Executor stub that drives service execs to ready (staying up) and other execs to success.</summary>
internal sealed class ServiceAwareExecutor(IJobStore store) : IJobExecutor
{
    public const string Address = "10.0.0.5:5432";
    public List<string> Stopped { get; } = new();
    public string Name => "stub";

    public async Task StartAsync(JobExecution exec, CancellationToken ct = default)
    {
        if (exec.IsService)
        {
            exec.Outputs["address"] = Address;
            exec.Status = JobStatus.Running;
            exec.Ready = true;
            exec.ReadyAt = DateTimeOffset.UtcNow;
            await store.UpdateAsync(exec);
        }
        else
        {
            exec.Status = JobStatus.Succeeded;
            exec.Progress = 100;
            exec.FinishedAt = DateTimeOffset.UtcNow;
            await store.UpdateAsync(exec);
        }
    }

    public Task StopAsync(JobExecution exec, CancellationToken ct = default)
    {
        Stopped.Add(exec.Id);
        exec.Status = JobStatus.Stopped;
        exec.FinishedAt = DateTimeOffset.UtcNow;
        return store.UpdateAsync(exec);
    }
}

/// <summary>Executor that only records what it was asked to start (leaves execs untouched).</summary>
internal sealed class RecordingExecutor : IJobExecutor
{
    public List<JobExecution> Started { get; } = new();
    public string Name => "rec";
    public Task StartAsync(JobExecution exec, CancellationToken ct = default) { Started.Add(exec); return Task.CompletedTask; }
    public Task StopAsync(JobExecution exec, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class NoopOrchestrator : IWorkflowOrchestrator
{
    public Task<string> StartAsync(string definitionName) => Task.FromResult("noop");
}

/// <summary>
/// Executor for the fan-out parallelism test: completes the seed node immediately (publishing the
/// array the fan-out reads), and holds every other execution "running" briefly before completing it
/// while tracking the peak number running at once — so a fan-out's MaxParallelism cap is observable.
/// </summary>
internal sealed class SeedingProbeExecutor(IJobStore store, string seedNode, (string key, string value) seedOutput)
    : IJobExecutor
{
    private int _running;
    public int MaxObserved;
    public string Name => "probe";

    public async Task StartAsync(JobExecution exec, CancellationToken ct = default)
    {
        if (exec.NodeName == seedNode)
        {
            exec.Outputs[seedOutput.key] = seedOutput.value;
            exec.Status = JobStatus.Succeeded;
            exec.Progress = 100;
            exec.FinishedAt = DateTimeOffset.UtcNow;
            await store.UpdateAsync(exec);
            return;
        }

        var now = Interlocked.Increment(ref _running);
        MaxObserved = Math.Max(MaxObserved, now);
        exec.Status = JobStatus.Running;
        await store.UpdateAsync(exec);
        _ = Task.Run(async () =>
        {
            await Task.Delay(60);
            Interlocked.Decrement(ref _running);
            exec.Status = JobStatus.Succeeded;
            exec.Progress = 100;
            exec.FinishedAt = DateTimeOffset.UtcNow;
            await store.UpdateAsync(exec);   // raises the change event → orchestrator starts the next held-back task
        });
    }

    public Task StopAsync(JobExecution exec, CancellationToken ct = default) => Task.CompletedTask;
}

internal static class TestWait
{
    public static async Task<WorkflowRun> RunTerminalAsync(IJobStore store, string runId, int timeoutMs = 15_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var run = await store.GetWorkflowRunAsync(runId);
            if (run is { IsTerminal: true }) return run;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Run {runId} did not finish within {timeoutMs}ms.");
    }
}
