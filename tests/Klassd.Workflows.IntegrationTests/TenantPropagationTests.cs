using Klassd.Workflows.Core.Execution;
using Klassd.Workflows.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klassd.Workflows.IntegrationTests;

/// <summary>
/// The tenant given at enqueue must reach the execution the executor starts — that's what later
/// becomes the worker's <c>KLASSD_TENANT</c> env var and drives tenant-scoped configuration.
/// </summary>
public class TenantPropagationTests
{
    private static JobScheduler NewScheduler(out RecordingExecutor executor, out InMemoryJobStore store)
    {
        store = new InMemoryJobStore();
        executor = new RecordingExecutor();
        return new JobScheduler(store, executor, new NoopOrchestrator(), NullLogger<JobScheduler>.Instance);
    }

    [Test]
    public async Task Enqueue_with_tenant_sets_it_on_the_started_execution()
    {
        var scheduler = NewScheduler(out var executor, out _);

        await scheduler.EnqueueAsync("Acme.Reports", tenant: "acme");

        await Assert.That(executor.Started).HasSingleItem();
        await Assert.That(executor.Started[0].Tenant).IsEqualTo("acme");
    }

    [Test]
    public async Task Enqueue_without_tenant_leaves_it_null()
    {
        var scheduler = NewScheduler(out var executor, out _);

        await scheduler.EnqueueAsync("Acme.Reports");

        await Assert.That(executor.Started[0].Tenant).IsNull();
    }
}
