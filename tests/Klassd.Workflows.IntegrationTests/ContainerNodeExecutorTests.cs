using Klassd.Workflows.Abstractions;
using Klassd.Workflows.Core.Model;
using k8s;
using k8s.Models;

namespace Klassd.Workflows.IntegrationTests;

/// <summary>
/// The arbitrary-container path against a real cluster (K3s via Testcontainers): running a public
/// image as a standalone job, and as a long-running service node inside a workflow. The images are
/// side-loaded into the node (see <see cref="KubernetesCluster"/>) so they run with
/// <c>imagePullPolicy=Never</c>. Opt-in via <c>KLASSD_K8S_IT=1</c>; see <see cref="RequiresKubernetesAttribute"/>.
/// </summary>
[RequiresKubernetes]
public class ContainerNodeExecutorTests
{
    [Test, Timeout(180_000)]
    public async Task Container_job_runs_to_completion(CancellationToken ct)
    {
        var id = await TestHost.Scheduler.EnqueueContainerAsync("echo", new ContainerSpec
        {
            Image = KubernetesCluster.BusyboxImage,
            Command = new[] { "sh", "-c" },
            Args = new[] { "echo hello-from-container" },
            ImagePullPolicy = "IfNotPresent",   // pulled by the cluster (not side-loaded), override the host's global Never
        });

        var exec = await WaitForAsync(id, e => e.IsTerminal, ct);

        await Assert.That(exec.Status).IsEqualTo(JobStatus.Succeeded);
        await Assert.That(exec.Container).IsNotNull();
        await Assert.That(Snapshot(exec).Any(l => l.Contains("hello-from-container"))).IsTrue();
    }

    [Test, Timeout(180_000)]
    public async Task Failing_container_job_reports_failure(CancellationToken ct)
    {
        var id = await TestHost.Scheduler.EnqueueContainerAsync("boom", new ContainerSpec
        {
            Image = KubernetesCluster.BusyboxImage,
            Command = new[] { "sh", "-c" },
            Args = new[] { "exit 3" },
            ImagePullPolicy = "IfNotPresent",
        });

        var exec = await WaitForAsync(id, e => e.IsTerminal, ct);

        await Assert.That(exec.Status).IsEqualTo(JobStatus.Failed);
        await Assert.That(exec.Error).IsNotNull();
    }

    [Test, Timeout(180_000)]
    public async Task Failing_init_container_gates_the_main_container(CancellationToken ct)
    {
        // The init container exits non-zero, so Kubernetes never starts the main container: the job
        // fails and the main container's marker never reaches the log. Proves init containers are
        // applied and gate the pod (on top of the executor-wide "preflight" init container).
        var id = await TestHost.Scheduler.EnqueueContainerAsync("init-gated", new ContainerSpec
        {
            Image = KubernetesCluster.BusyboxImage,
            Command = new[] { "sh", "-c" },
            Args = new[] { "echo MAIN-RAN" },
            ImagePullPolicy = "IfNotPresent",
            InitContainers = new[]
            {
                new InitContainerSpec
                {
                    Name = "gate",
                    Image = KubernetesCluster.BusyboxImage,
                    Command = new[] { "sh", "-c" },
                    Args = new[] { "exit 9" },
                    ImagePullPolicy = "IfNotPresent",
                },
            },
        });

        var exec = await WaitForAsync(id, e => e.IsTerminal, ct);

        await Assert.That(exec.Status).IsEqualTo(JobStatus.Failed);
        await Assert.That(Snapshot(exec).Any(l => l.Contains("MAIN-RAN"))).IsFalse();
    }

    [Test, Timeout(180_000)]
    public async Task Init_container_shares_an_emptydir_volume_with_the_main_container(CancellationToken ct)
    {
        // An emptyDir volume mounted into both the init container (which writes a file) and the main
        // container (which reads it). Proves pod volume creation + init/main mounts + data handoff.
        var id = await TestHost.Scheduler.EnqueueContainerAsync("vol-share", new ContainerSpec
        {
            Image = KubernetesCluster.BusyboxImage,
            Command = new[] { "sh", "-c" },
            Args = new[] { "cat /shared/msg" },
            ImagePullPolicy = "IfNotPresent",
            Volumes = new[] { new VolumeSpec { Name = "shared", Kind = VolumeKind.EmptyDir } },
            VolumeMounts = new[] { new VolumeMountSpec { Name = "shared", MountPath = "/shared" } },
            InitContainers = new[]
            {
                new InitContainerSpec
                {
                    Name = "seed",
                    Image = KubernetesCluster.BusyboxImage,
                    Command = new[] { "sh", "-c" },
                    Args = new[] { "echo hello-from-init > /shared/msg" },
                    ImagePullPolicy = "IfNotPresent",
                    VolumeMounts = new[] { new VolumeMountSpec { Name = "shared", MountPath = "/shared" } },
                },
            },
        });

        var exec = await WaitForAsync(id, e => e.IsTerminal, ct);

        await Assert.That(exec.Status).IsEqualTo(JobStatus.Succeeded).Because(string.Join("\n", Snapshot(exec)));
        await Assert.That(Snapshot(exec).Any(l => l.Contains("hello-from-init"))).IsTrue();
    }

    [Test, Timeout(180_000)]
    public async Task Container_security_context_runs_as_the_requested_user(CancellationToken ct)
    {
        // Container securityContext: run as uid 1000 (non-root), no privilege escalation, read-only
        // rootfs, all capabilities dropped. The main container prints its uid — proving the context
        // was applied (on top of the executor-wide pod securityContext set in TestHost).
        var id = await TestHost.Scheduler.EnqueueContainerAsync("as-user", new ContainerSpec
        {
            Image = KubernetesCluster.BusyboxImage,
            Command = new[] { "sh", "-c" },
            Args = new[] { "id -u" },
            ImagePullPolicy = "IfNotPresent",
            SecurityContext = new SecurityContextSpec
            {
                RunAsUser = 1000,
                RunAsNonRoot = true,
                AllowPrivilegeEscalation = false,
                ReadOnlyRootFilesystem = true,
                DropCapabilities = new[] { "ALL" },
            },
        });

        var exec = await WaitForAsync(id, e => e.IsTerminal, ct);

        await Assert.That(exec.Status).IsEqualTo(JobStatus.Succeeded).Because(string.Join("\n", Snapshot(exec)));
        await Assert.That(Snapshot(exec).Any(l => l.Contains("1000"))).IsTrue();
    }

    [Test, Timeout(180_000)]
    public async Task Container_job_with_resources_nodeselector_and_toleration_schedules_and_runs(CancellationToken ct)
    {
        // Explicit resource requests/limits + a node selector matching the single K3s node + a
        // tolerate-everything toleration. The pod must still schedule and the job complete — proving
        // all three are accepted and applied (a non-matching selector would leave the pod Pending).
        var id = await TestHost.Scheduler.EnqueueContainerAsync("scheduled", new ContainerSpec
        {
            Image = KubernetesCluster.BusyboxImage,
            Command = new[] { "sh", "-c" },
            Args = new[] { "echo scheduled-ok" },
            ImagePullPolicy = "IfNotPresent",
            Resources = new JobResourceRequirements { CpuRequest = "50m", MemoryRequest = "32Mi", MemoryLimit = "64Mi" },
            NodeSelector = new Dictionary<string, string> { ["kubernetes.io/os"] = "linux" },
            Tolerations = new[] { new TolerationSpec { Operator = "Exists" } },
        });

        var exec = await WaitForAsync(id, e => e.IsTerminal, ct);

        await Assert.That(exec.Status).IsEqualTo(JobStatus.Succeeded).Because(string.Join("\n", Snapshot(exec)));
        await Assert.That(Snapshot(exec).Any(l => l.Contains("scheduled-ok"))).IsTrue();
    }

    [Test, Timeout(180_000)]
    public async Task EnvFrom_configmap_surfaces_as_environment(CancellationToken ct)
    {
        // Create a ConfigMap in the cluster, import it via envFrom, and have the container echo the key.
        var config = new k8s.Kubernetes(KubernetesClientConfiguration.BuildConfigFromConfigFile(TestHost.KubeConfigPath));
        var cmName = "envfrom-test";
        try { await config.CoreV1.DeleteNamespacedConfigMapAsync(cmName, TestHost.Namespace); } catch { /* not present */ }
        await config.CoreV1.CreateNamespacedConfigMapAsync(new V1ConfigMap
        {
            Metadata = new V1ObjectMeta { Name = cmName },
            Data = new Dictionary<string, string> { ["GREETING"] = "from-configmap" },
        }, TestHost.Namespace);

        var id = await TestHost.Scheduler.EnqueueContainerAsync("envfrom", new ContainerSpec
        {
            Image = KubernetesCluster.BusyboxImage,
            Command = new[] { "sh", "-c" },
            Args = new[] { "echo $GREETING" },
            ImagePullPolicy = "IfNotPresent",
            EnvFrom = new[] { new EnvFromSpec { Kind = EnvFromKind.ConfigMap, Name = cmName } },
        });

        var exec = await WaitForAsync(id, e => e.IsTerminal, ct);

        await Assert.That(exec.Status).IsEqualTo(JobStatus.Succeeded).Because(string.Join("\n", Snapshot(exec)));
        await Assert.That(Snapshot(exec).Any(l => l.Contains("from-configmap"))).IsTrue();
    }

    [Test, Timeout(180_000)]
    public async Task Container_file_output_uses_default_when_the_file_is_absent(CancellationToken ct)
    {
        // The container writes nothing; the capture sidecar falls back to the declared default.
        var id = await TestHost.Scheduler.EnqueueContainerAsync("no-file", new ContainerSpec
        {
            Image = KubernetesCluster.BusyboxImage,
            Command = new[] { "sh", "-c" },
            Args = new[] { "true" },
            ImagePullPolicy = "IfNotPresent",
            FileOutputs = new[] { new OutputSpec { Name = "result", Path = "/mnt/out/result.json", Default = "fallback" } },
        });

        var exec = await WaitForAsync(id, e => e.IsTerminal, ct);

        await Assert.That(exec.Status).IsEqualTo(JobStatus.Succeeded).Because(string.Join("\n", Snapshot(exec)));
        await Assert.That(exec.Outputs.GetValueOrDefault("result")).IsEqualTo("fallback");
    }

    [Test, Timeout(300_000)]
    public async Task Container_file_output_feeds_a_capped_fanout(CancellationToken ct)
    {
        // markets (busybox) writes a 3-element JSON array to a file -> captured as "market_ids" via the
        // capture sidecar -> integrate fans out over it (one execution per market), capped at 2.
        var runId = await TestHost.Scheduler.EnqueueWorkflowAsync(TestHost.FileOutputFanoutWorkflow);

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

        // The file output was captured from the container's pod...
        var markets = run.Node("markets")!;
        var marketsExec = await TestHost.Store.GetAsync(markets.ExecutionIds.Last());
        await Assert.That(marketsExec!.Outputs.GetValueOrDefault("market_ids")).Contains("en-dk_DKK");

        // ...and drove a 3-way fan-out, all succeeded.
        var integrate = run.Node("integrate")!;
        await Assert.That(integrate.ExecutionIds.Count()).IsEqualTo(3);
        await Assert.That(integrate.Status).IsEqualTo(NodeRunStatus.Succeeded);
    }

    [Test, Timeout(240_000)]
    public async Task Container_service_node_becomes_ready_forwards_address_and_is_torn_down(CancellationToken ct)
    {
        var runId = await TestHost.Scheduler.EnqueueWorkflowAsync(TestHost.ContainerServiceWorkflow);

        WorkflowRun? run = null;
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            run = await TestHost.Store.GetWorkflowRunAsync(runId);
            if (run is { IsTerminal: true }) break;
            await Task.Delay(1500, ct);
        }

        await Assert.That(run).IsNotNull();
        await Assert.That(run!.Status).IsEqualTo(WorkflowRunStatus.Succeeded).Because(await DiagnoseAsync(run));

        var web = run.Node("web")!;
        var consumer = run.Node("consumer")!;

        // The nginx node ran as a service: it became ready, then the orchestrator tore it down once
        // the work node finished — so its final NodeRun status is Succeeded.
        await Assert.That(web.IsService).IsTrue();
        await Assert.That(web.Status).IsEqualTo(NodeRunStatus.Succeeded);
        await Assert.That(consumer.Status).IsEqualTo(NodeRunStatus.Succeeded);

        // The executor published address = <podIP>:80 and the orchestrator forwarded it into the
        // dependent's resolved arguments.
        var webExec = await TestHost.Store.GetAsync(web.ExecutionIds.Last());
        var address = webExec!.Outputs["address"];
        await Assert.That(address).EndsWith(":80");
        await Assert.That(webExec.Ready).IsTrue();

        var consumerExec = await TestHost.Store.GetAsync(consumer.ExecutionIds.Last());
        await Assert.That(consumerExec!.Arguments["name"]).IsEqualTo(address);
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
                sb.AppendLine($"    exec {execId}: {e.Status} ready={e.Ready} error={e.Error}");
                foreach (var line in Snapshot(e).TakeLast(6))
                    sb.AppendLine($"      | {line}");
            }
        }
        return sb.ToString();
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
