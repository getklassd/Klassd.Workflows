using Klassd.Workflows.Core.Model;
using Klassd.Workflows.Core.Storage;
using Klassd.Workflows.Core.Workflows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klassd.Workflows.IntegrationTests;

/// <summary>
/// Service (daemon) node semantics, driven through the real <see cref="WorkflowOrchestrator"/> with an
/// in-memory store and a stub executor — no cluster. Covers readiness-gating, address forwarding, and
/// teardown at the end of the run.
/// </summary>
public class ServiceNodeOrchestrationTests
{
    [Test, Timeout(15_000)]
    public async Task Service_gates_dependents_forwards_address_and_is_torn_down(CancellationToken cancellationToken)
    {
        var store = new InMemoryJobStore();
        var registry = new WorkflowRegistry();
        registry.Register(new WorkflowBuilder("svc-test")
            .Add("proxy", "Proxy", n => n.AsService())
            .Add("consumer", "Consumer", n => n.DependsOn("proxy").BindInput("db_host", "proxy", "address"))
            .Build());

        var executor = new ServiceAwareExecutor(store);
        var orch = new WorkflowOrchestrator(store, executor, registry, NullLogger<WorkflowOrchestrator>.Instance);
        await ((IHostedService)orch).StartAsync(default);

        var runId = await orch.StartAsync("svc-test");
        var run = await TestWait.RunTerminalAsync(store, runId);

        await Assert.That(run.Status).IsEqualTo(WorkflowRunStatus.Succeeded);

        var proxy = run.Node("proxy")!;
        var consumer = run.Node("consumer")!;
        await Assert.That(proxy.IsService).IsTrue();
        await Assert.That(proxy.Status).IsEqualTo(NodeRunStatus.Succeeded);     // torn down
        await Assert.That(consumer.Status).IsEqualTo(NodeRunStatus.Succeeded);

        // The proxy's execution was actually stopped by the executor.
        await Assert.That(executor.Stopped.Contains(proxy.ExecutionIds.First())).IsTrue();

        // The service address was forwarded into the consumer's arguments.
        var consumerExec = await store.GetAsync(consumer.ExecutionIds.First());
        await Assert.That(consumerExec!.Arguments["db_host"]).IsEqualTo(ServiceAwareExecutor.Address);

        await ((IHostedService)orch).StopAsync(default);
    }

    [Test, Timeout(30_000)]
    public async Task Fanout_respects_max_parallelism()
    {
        var store = new InMemoryJobStore();
        var registry = new WorkflowRegistry();
        // "seed" publishes a 6-element array; "work" fans out over it, capped at 2 concurrent.
        registry.Register(new WorkflowBuilder("fanout-cap")
            .Add("seed", "Seed")
            .Add("work", "Work", n => n.FanOutOver("seed", "items", "item", maxParallelism: 2))
            .Build());

        var executor = new SeedingProbeExecutor(store, seedNode: "seed",
            seedOutput: ("items", "[\"a\",\"b\",\"c\",\"d\",\"e\",\"f\"]"));
        var orch = new WorkflowOrchestrator(store, executor, registry, NullLogger<WorkflowOrchestrator>.Instance);
        await ((IHostedService)orch).StartAsync(default);

        var runId = await orch.StartAsync("fanout-cap");
        var run = await TestWait.RunTerminalAsync(store, runId, 25_000);

        await Assert.That(run.Status).IsEqualTo(WorkflowRunStatus.Succeeded);
        // All six fan-out items ran...
        await Assert.That(run.Node("work")!.ExecutionIds.Count()).IsEqualTo(6);
        // ...but never more than two at once.
        await Assert.That(executor.MaxObserved).IsLessThanOrEqualTo(2);

        await ((IHostedService)orch).StopAsync(default);
    }

    [Test, Timeout(15_000)]
    public async Task Workflow_of_only_services_completes(CancellationToken cancellationToken)
    {
        var store = new InMemoryJobStore();
        var registry = new WorkflowRegistry();
        registry.Register(new WorkflowBuilder("only-svc")
            .Add("proxy", "Proxy", n => n.AsService())
            .Build());

        var executor = new ServiceAwareExecutor(store);
        var orch = new WorkflowOrchestrator(store, executor, registry, NullLogger<WorkflowOrchestrator>.Instance);
        await ((IHostedService)orch).StartAsync(default);

        var runId = await orch.StartAsync("only-svc");
        var run = await TestWait.RunTerminalAsync(store, runId);

        await Assert.That(run.Status).IsEqualTo(WorkflowRunStatus.Succeeded);
        await Assert.That(executor.Stopped.Contains(run.Node("proxy")!.ExecutionIds.First())).IsTrue();

        await ((IHostedService)orch).StopAsync(default);
    }
}
