using Klassd.Workflows.Core.Execution;
using Klassd.Workflows.Core.Model;
using Klassd.Workflows.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klassd.Workflows.IntegrationTests;

/// <summary>Standalone container jobs (run an arbitrary image as a job, no IJob port).</summary>
public class ContainerJobSchedulerTests
{
    private static JobScheduler NewScheduler(InMemoryJobStore store, RecordingExecutor exec) =>
        new(store, exec, new NoopOrchestrator(), NullLogger<JobScheduler>.Instance);

    [Test]
    public async Task EnqueueContainer_sets_spec_on_execution_and_dispatches()
    {
        var store = new InMemoryJobStore();
        var exec = new RecordingExecutor();
        var sched = NewScheduler(store, exec);

        var id = await sched.EnqueueContainerAsync("go-importer",
            new ContainerSpec { Image = "ghcr.io/acme/importer:1.4", Args = new[] { "--full" } },
            new() { ["MODE"] = "nightly" });

        var stored = await store.GetAsync(id);
        await Assert.That(stored!.Container).IsNotNull();
        await Assert.That(stored.Container!.Image).IsEqualTo("ghcr.io/acme/importer:1.4");
        await Assert.That(stored.JobName).IsEqualTo("go-importer");
        await Assert.That(stored.Arguments["MODE"]).IsEqualTo("nightly");

        // It was actually handed to the executor, carrying the container spec.
        await Assert.That(exec.Started.Any(e => e.Id == id && e.Container is not null)).IsTrue();
    }

    [Test]
    public async Task AddOrUpdateRecurringContainer_persists_container_kind()
    {
        var store = new InMemoryJobStore();
        var sched = NewScheduler(store, new RecordingExecutor());

        sched.AddOrUpdateRecurringContainer("nightly", "go-importer",
            new ContainerSpec { Image = "ghcr.io/acme/importer:1.4" }, "0 3 * * *");

        var recurring = await store.ListRecurringAsync();
        var entry = recurring.Single(r => r.Id == "nightly");
        await Assert.That(entry.Kind).IsEqualTo(RecurringKind.Container);
        await Assert.That(entry.JobTypeName).IsEqualTo("go-importer");
        await Assert.That(entry.Container!.Image).IsEqualTo("ghcr.io/acme/importer:1.4");
        await Assert.That(entry.Cron).IsEqualTo("0 3 * * *");
    }
}
