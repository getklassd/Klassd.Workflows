using Klassd.Workflows.Abstractions;

namespace Klassd.Workflows.SampleJobs;

/// <summary>Always throws — handy for exercising the failure path end-to-end.</summary>
public sealed class FailingJob : IJob
{
    public async Task RunAsync(IJobContext context)
    {
        context.Log("About to fail on purpose…");
        await Task.Delay(TimeSpan.FromMilliseconds(200), context.CancellationToken);
        throw new InvalidOperationException("Boom — this job always fails.");
    }
}
