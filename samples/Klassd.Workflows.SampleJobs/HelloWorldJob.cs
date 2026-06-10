using Klassd.Workflows.Abstractions;

namespace Klassd.Workflows.SampleJobs;

/// <summary>Counts to 10, reporting progress — handy to watch in the live console.</summary>
public sealed class HelloWorldJob : IJob
{
    public async Task RunAsync(IJobContext context)
    {
        var name = context.Arguments.GetValueOrDefault("name", "world");
        context.Log($"Hello, {name}!");

        for (var i = 1; i <= 10; i++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            context.Log($"Step {i} of 10");
            context.ReportProgress(i * 10, $"Processed {i}/10");
            await Task.Delay(TimeSpan.FromSeconds(1), context.CancellationToken);
        }

        context.Log("Done.");
    }
}
