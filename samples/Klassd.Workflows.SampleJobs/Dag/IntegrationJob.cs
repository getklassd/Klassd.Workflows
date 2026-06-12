using Klassd.Workflows.Abstractions;

namespace Klassd.Workflows.SampleJobs.Dag;

/// <summary>
/// Fan-out node: one execution per market (the "market" argument), running only
/// after both the market finder and data proxy succeed. Mirrors Argo's
/// product-integration task with withParam.
/// </summary>
public sealed class IntegrationJob : IJob
{
    public async Task RunAsync(IJobContext context)
    {
        var market = context.Arguments.GetValueOrDefault("market", "?");
        context.Log($"Integrating products for market {market}");

        // Address forwarded from the long-running "sql-proxy" service node (bound via BindInput).
        if (context.Arguments.GetValueOrDefault("db_host") is { Length: > 0 } dbHost)
            context.Log($"Connecting through the SQL proxy at {dbHost}");

        for (var i = 1; i <= 4; i++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            context.ReportProgress(i * 25, $"{market}: batch {i}/4");
            await Task.Delay(TimeSpan.FromSeconds(1), context.CancellationToken);
        }

        context.SetOutput("product_count", (market.Length * 100).ToString());
        context.Log($"Done integrating {market}.");
    }
}
