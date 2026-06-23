using Cronos;
using Klassd.Workflows.Core.Abstractions;
using Klassd.Workflows.Core.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Klassd.Workflows.Core.Execution;

/// <summary>
/// Background loop that fires recurring jobs when their cron expression is due.
/// Ticks once per <see cref="PollInterval"/> and enqueues anything whose next
/// occurrence has passed since its last run.
/// </summary>
public sealed class RecurringScheduler : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly IJobStore _store;
    private readonly IJobScheduler _scheduler;
    private readonly ILogger<RecurringScheduler> _logger;

    public RecurringScheduler(IJobStore store, IJobScheduler scheduler, ILogger<RecurringScheduler> logger)
    {
        _store = store;
        _scheduler = scheduler;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try { await TickAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "Recurring scheduler tick failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TickAsync()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var job in await _store.ListRecurringAsync())
        {
            if (!job.Enabled) continue;

            CronExpression expression;
            try { expression = CronExpression.Parse(job.Cron, CronFormat.IncludeSeconds); }
            catch
            {
                try { expression = CronExpression.Parse(job.Cron); }
                catch (Exception ex) { _logger.LogWarning(ex, "Invalid cron '{Cron}' on {Id}", job.Cron, job.Id); continue; }
            }

            // Compute the most recent scheduled time relative to last run.
            var after = (job.LastRun ?? now.AddMinutes(-1)).UtcDateTime;
            var next = expression.GetNextOccurrence(after, TimeZoneInfo.Utc);
            if (next is null || next > now.UtcDateTime) continue;

            job.LastRun = now;
            job.LastExecutionId = job.Kind switch
            {
                RecurringKind.Workflow => await _scheduler.EnqueueWorkflowAsync(job.WorkflowName, job.Tenant),
                RecurringKind.Container when job.Container is not null =>
                    await _scheduler.EnqueueContainerAsync(job.JobTypeName, job.Container, job.Arguments, job.Tenant),
                _ => await _scheduler.EnqueueAsync(job.JobTypeName, job.Arguments, job.Tenant)
            };
            await _store.UpsertRecurringAsync(job);
        }
    }
}
