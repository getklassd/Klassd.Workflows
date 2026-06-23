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
    public async Task EnqueueAsync_with_initContainers_carries_them_on_execution()
    {
        var store = new InMemoryJobStore();
        var exec = new RecordingExecutor();
        var sched = NewScheduler(store, exec);

        var id = await sched.EnqueueAsync("Acme.MigrateThenRun",
            initContainers: new[] { new InitContainerSpec { Name = "migrate", Image = "ghcr.io/acme/migrator:2.0" } });

        var stored = await store.GetAsync(id);
        await Assert.That(stored!.InitContainers.Count).IsEqualTo(1);
        await Assert.That(stored.InitContainers[0].Image).IsEqualTo("ghcr.io/acme/migrator:2.0");

        // Handed to the executor carrying the init containers (it merges them into the pod).
        await Assert.That(exec.Started.Any(e => e.Id == id && e.InitContainers.Count == 1)).IsTrue();
    }

    [Test]
    public async Task AddOrUpdateRecurring_persists_initContainers()
    {
        var store = new InMemoryJobStore();
        var sched = NewScheduler(store, new RecordingExecutor());

        sched.AddOrUpdateRecurring("nightly-migrate", "Acme.MigrateThenRun", "0 3 * * *",
            initContainers: new[] { new InitContainerSpec { Name = "migrate", Image = "ghcr.io/acme/migrator:2.0" } });

        var entry = (await store.ListRecurringAsync()).Single(r => r.Id == "nightly-migrate");
        await Assert.That(entry.Kind).IsEqualTo(RecurringKind.Job);
        await Assert.That(entry.InitContainers.Count).IsEqualTo(1);
        await Assert.That(entry.InitContainers[0].Image).IsEqualTo("ghcr.io/acme/migrator:2.0");
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
