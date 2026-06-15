using Klassd.Workflows.Abstractions;
using Klassd.Workflows.SampleJobs.Dag;

namespace Klassd.Workflows.SampleJobs;

/// <summary>
/// The shared job registration, referenced by both the worker exe (to construct jobs it runs) and
/// the dashboard host (to populate the job catalog). Keeping it in one place means both sides agree
/// on the dispatch keys. Keys default to each job's full type name, matching what the scheduler
/// emits for <c>EnqueueAsync&lt;T&gt;</c>, recurring jobs and workflow nodes.
/// </summary>
public static class SampleJobsRegistration
{
    public static void Register(JobRegistrationBuilder jobs) => jobs
        .Add<HelloWorldJob>()
        .Add<ReportGenerationJob>()
        .Add<FailingJob>()
        .Add<ConfiguredGreetingJob>()
        // DAG sample jobs.
        .Add<MarketFinderJob>()
        .Add<DataProxyJob>()
        .Add<SqlProxyServiceJob>()
        .Add<IntegrationJob>()
        .Add<PublishJob>()
        .Add<FinalizerJob>()
        .Add<NotifyJob>()
        .Add<RollbackJob>();
}
