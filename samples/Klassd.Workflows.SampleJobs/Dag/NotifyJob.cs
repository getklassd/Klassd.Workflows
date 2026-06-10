using Klassd.Workflows.Abstractions;

namespace Klassd.Workflows.SampleJobs.Dag;

/// <summary>Runs only when the data proxy reported status == "ok" (a true when-condition).</summary>
public sealed class NotifyJob : IJob
{
    public async Task RunAsync(IJobContext context)
    {
        context.Log("Sending success notification to the team channel...");
        await Task.Delay(TimeSpan.FromSeconds(1), context.CancellationToken);
        context.Log("Notified.");
    }
}

/// <summary>Compensation node, gated on status == "failed" — omitted on a healthy run.</summary>
public sealed class RollbackJob : IJob
{
    public Task RunAsync(IJobContext context)
    {
        context.Log("Rolling back (should not run on a healthy run).");
        return Task.CompletedTask;
    }
}
