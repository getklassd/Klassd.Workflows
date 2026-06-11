using Klassd.Workflows.Abstractions;

namespace Klassd.Workflows.SampleJobs;

/// <summary>Counts to 10, reporting progress — handy to watch in the live console.</summary>
[JobInput("name", Label = "Name to greet", Default = "world",
    Description = "Shown in the first log line.")]
public sealed class HelloWorldJob : IJob
{
    public async Task RunAsync(IJobContext context)
    {
        var name = context.Arguments.GetValueOrDefault("name", "world");
        const int steps = 10;
        context.Log($"Hello, {name}! Counting to {steps}…");

        for (var i = 1; i <= context.WithProgress(steps); i++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(1), context.CancellationToken);
            // Report intermediate progress only — the engine sets 100% when the job completes,
            // so we don't claim 100% before the final log line.
            if (i < steps) context.ReportProgress(i * 100 / steps, $"Processed {i}/{steps}");
        }

        context.Log("Done.");
    }
}