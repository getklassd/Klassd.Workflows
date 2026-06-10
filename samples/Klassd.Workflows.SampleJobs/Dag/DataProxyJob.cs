using Klassd.Workflows.Abstractions;

namespace Klassd.Workflows.SampleJobs.Dag;

/// <summary>
/// A second root that runs in parallel with the market finder. Produces a large
/// dataset stored as an artifact (only the small reference flows downstream) and
/// a "status" output used by conditional nodes.
/// </summary>
public sealed class DataProxyJob : IJob
{
    public async Task RunAsync(IJobContext context)
    {
        context.Log("Warming the data proxy...");
        for (var i = 1; i <= 3; i++)
        {
            context.ReportProgress(i * 33, $"warming {i}/3");
            await Task.Delay(TimeSpan.FromSeconds(1), context.CancellationToken);
        }

        // Large payload → artifact store, not an env-sized output.
        var dataset = string.Join('\n', Enumerable.Range(0, 5000).Select(i => $"row {i},value-{i}"));
        var reference = await context.Artifacts.SaveTextAsync("dataset.csv", dataset, context.CancellationToken);
        context.SetOutput("dataset_ref", reference);
        context.SetOutput("status", "ok");

        context.Log($"Data proxy ready. Dataset artifact: {reference} ({dataset.Length} bytes)");
    }
}
