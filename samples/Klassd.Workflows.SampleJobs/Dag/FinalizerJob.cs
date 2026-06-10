using Klassd.Workflows.Abstractions;

namespace Klassd.Workflows.SampleJobs.Dag;

/// <summary>
/// Join node: runs once after every fanned-out integration has finished. Reads
/// the dataset artifact produced by the data proxy via an input binding —
/// demonstrating large-payload passing between pods.
/// </summary>
public sealed class FinalizerJob : IJob
{
    public async Task RunAsync(IJobContext context)
    {
        context.Log("Finalizing: publishing go-live + reindexing...");

        if (context.Arguments.TryGetValue("dataset_ref", out var reference))
        {
            var dataset = await context.Artifacts.LoadTextAsync(reference, context.CancellationToken);
            context.Log($"Loaded dataset artifact {reference}: {dataset.Length} bytes, {dataset.Split('\n').Length} rows");
        }

        await Task.Delay(TimeSpan.FromSeconds(2), context.CancellationToken);
        context.ReportProgress(100, "published");
        context.Log("Catalog integration complete.");
    }
}
