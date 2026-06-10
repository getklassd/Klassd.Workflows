using System.Text.Json;
using Klassd.Workflows.Abstractions;

namespace Klassd.Workflows.SampleJobs.Dag;

/// <summary>
/// Root node. Produces the list of markets to process and publishes it as the
/// "market_ids" output — the downstream integration node fans out over it.
/// Mirrors Argo's market-id-fetcher.
/// </summary>
public sealed class MarketFinderJob : IJob
{
    public async Task RunAsync(IJobContext context)
    {
        context.Log("Finding markets...");
        await Task.Delay(TimeSpan.FromSeconds(1), context.CancellationToken);

        var markets = new[] { "DK", "SE", "NO", "DE" };
        context.ReportProgress(100, $"Found {markets.Length} markets");
        context.SetOutput("market_ids", JsonSerializer.Serialize(markets));
        context.Log($"Markets: {string.Join(", ", markets)}");
    }
}
