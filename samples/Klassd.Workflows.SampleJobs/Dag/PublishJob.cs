using Klassd.Workflows.Abstractions;

namespace Klassd.Workflows.SampleJobs.Dag;

/// <summary>
/// Demonstrates retries: fails on the first attempt and succeeds on the next.
/// The orchestrator injects "__attempt" (0-based) for each retry.
/// </summary>
public sealed class PublishJob : IJob
{
    public async Task RunAsync(IJobContext context)
    {
        var attempt = int.Parse(context.Arguments.GetValueOrDefault("__attempt", "0"));
        context.Log($"Publish attempt #{attempt + 1}");
        await Task.Delay(TimeSpan.FromSeconds(1), context.CancellationToken);

        if (attempt == 0)
            throw new InvalidOperationException("Simulated transient publish failure — will be retried.");

        context.ReportProgress(100, "published");
        context.Log("Publish succeeded.");
    }
}
