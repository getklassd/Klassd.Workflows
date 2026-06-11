using Klassd.Workflows.Abstractions;
using Klassd.Workflows.Core.Model;
using Klassd.Workflows.SampleJobs;

namespace Klassd.Workflows.IntegrationTests;

/// <summary>
/// End-to-end against a real cluster (K3s via Testcontainers): the worker image runs as a K8s Job,
/// <see cref="Klassd.Workflows.Kubernetes.KubernetesJobExecutor"/> tails the pod log and feeds it
/// through the shared output processor. Opt-in via <c>KLASSD_K8S_IT=1</c>; see <see cref="RequiresKubernetesAttribute"/>.
/// </summary>
[RequiresKubernetes]
public class KubernetesExecutorTests
{
    [Test, Timeout(180_000)]
    public async Task Job_runs_to_completion(CancellationToken ct)
    {
        var exec = await RunJobAsync<HelloWorldJob>(new() { ["name"] = "k3s" }, ct);

        await Assert.That(exec.Status).IsEqualTo(JobStatus.Succeeded);
        await Assert.That(exec.Progress).IsEqualTo(100);

        var logs = Snapshot(exec);
        await Assert.That(logs.Any(l => l.Contains("Hello, k3s"))).IsTrue();

        // The inline progress bar (##BAR## <id> <current> <total>) reached its total.
        var bar = logs.LastOrDefault(l => l.StartsWith(WorkerProtocol.ConsoleBarMarker, StringComparison.Ordinal));
        await Assert.That(bar).IsNotNull();
        var parts = bar!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(parts[^2]).IsEqualTo(parts[^1]);
    }

    [Test, Timeout(180_000)]
    public async Task Failing_job_reports_failure(CancellationToken ct)
    {
        var exec = await RunJobAsync<FailingJob>(new(), ct);

        await Assert.That(exec.Status).IsEqualTo(JobStatus.Failed);
        await Assert.That(exec.Error).IsNotNull();
    }

    [Test, Timeout(180_000)]
    public async Task Stopping_a_running_job_marks_it_stopped(CancellationToken ct)
    {
        // HelloWorldJob runs ~10s; start it, wait until it's actually running, then stop it.
        var id = await TestHost.Scheduler.EnqueueAsync<HelloWorldJob>(new() { ["name"] = "stopme" });
        await WaitForAsync(id, e => e.Status is JobStatus.Running, ct);

        await TestHost.Scheduler.StopAsync(id);

        var exec = await WaitForAsync(id, e => e.IsTerminal, ct);
        await Assert.That(exec.Status).IsEqualTo(JobStatus.Stopped);
    }

    [Test, Timeout(300_000)]
    public async Task Workflow_dag_runs_all_nodes(CancellationToken ct)
    {
        var runId = await TestHost.Scheduler.EnqueueWorkflowAsync(TestHost.SampleWorkflow);

        WorkflowRun? run = null;
        var deadline = DateTime.UtcNow.AddMinutes(4);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            run = await TestHost.Store.GetWorkflowRunAsync(runId);
            if (run is { IsTerminal: true }) break;
            await Task.Delay(1500, ct);
        }

        await Assert.That(run).IsNotNull();
        await Assert.That(run!.Status).IsEqualTo(WorkflowRunStatus.Succeeded).Because(await DiagnoseAsync(run));
        // notify runs (status == ok), rollback is omitted; every node ends terminal.
        await Assert.That(run.Nodes.All(n => n.IsTerminal)).IsTrue();
        await Assert.That(run.Node("rollback")!.Status).IsEqualTo(NodeRunStatus.Omitted);
        await Assert.That(run.Node("publish")!.Status).IsEqualTo(NodeRunStatus.Succeeded); // succeeded via retry

        // The finalizer read the dataset artifact data-proxy wrote — proving cross-pod artifact
        // passing through the MinIO-backed S3 store.
        var finalizerExecId = run.Node("finalizer")!.ExecutionIds.Last();
        var finalizer = await TestHost.Store.GetAsync(finalizerExecId);
        await Assert.That(Snapshot(finalizer!).Any(l => l.Contains("Loaded dataset artifact"))).IsTrue();
    }

    private static async Task<string> DiagnoseAsync(WorkflowRun run)
    {
        var sb = new System.Text.StringBuilder($"Run {run.Id} = {run.Status}\n");
        foreach (var node in run.Nodes)
        {
            sb.AppendLine($"  node {node.Name}: {node.Status}");
            foreach (var execId in node.ExecutionIds)
            {
                var e = await TestHost.Store.GetAsync(execId);
                if (e is null) continue;
                sb.AppendLine($"    exec {execId}: {e.Status} error={e.Error}");
                foreach (var line in Snapshot(e).TakeLast(6))
                    sb.AppendLine($"      | {line}");
            }
        }
        return sb.ToString();
    }

    private static async Task<JobExecution> RunJobAsync<TJob>(Dictionary<string, string> args, CancellationToken ct)
        where TJob : IJob
    {
        var id = await TestHost.Scheduler.EnqueueAsync<TJob>(args);
        return await WaitForAsync(id, e => e.IsTerminal, ct);
    }

    private static async Task<JobExecution> WaitForAsync(string id, Func<JobExecution, bool> predicate, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var exec = await TestHost.Store.GetAsync(id);
            if (exec is not null && predicate(exec)) return exec;
            await Task.Delay(1000, ct);
        }
        throw new TimeoutException($"Job {id} did not reach the expected state in time.");
    }

    private static IReadOnlyList<string> Snapshot(JobExecution e)
    {
        lock (e.Logs) return e.Logs.ToList();
    }
}
