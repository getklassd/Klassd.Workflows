using Klassd.Workflows.Abstractions;

namespace Klassd.Workflows.SampleJobs;

/// <summary>Simulates a longer multi-stage batch job.</summary>
/// <remarks>
/// Ships baseline pod resources via the attribute. Ops can override any field
/// from appsettings under Klassd.Workflows:Resources without recompiling.
/// </remarks>
[JobResources(CpuRequest = "250m", CpuLimit = "1", MemoryRequest = "256Mi", MemoryLimit = "512Mi")]
public sealed class ReportGenerationJob : IJob
{
    private static readonly string[] Stages =
        { "Collecting data", "Aggregating", "Rendering", "Uploading" };

    public async Task RunAsync(IJobContext context)
    {
        for (var i = 0; i < Stages.Length; i++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            context.Log($"{Stages[i]}...");
            context.ReportProgress((int)((i + 1) / (double)Stages.Length * 100), Stages[i]);
            await Task.Delay(TimeSpan.FromSeconds(2), context.CancellationToken);
        }

        context.Log("Report generated successfully.");
    }
}
