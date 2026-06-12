using System.Text.Json;
using Klassd.Workflows.Abstractions;
using Klassd.Workflows.Core.Abstractions;
using Klassd.Workflows.Core.Execution;
using Klassd.Workflows.Core.Model;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Klassd.Workflows.Kubernetes;

/// <summary>
/// Runs each job as a Kubernetes Job (one pod). The pod runs the worker image
/// with the job descriptor in env vars; this executor tails the pod logs and
/// feeds them through the shared <see cref="WorkerOutputProcessor"/>.
/// </summary>
public sealed class KubernetesJobExecutor : IJobExecutor
{
    private readonly IJobStore _store;
    private readonly KubernetesExecutorOptions _options;
    private readonly ILogger<KubernetesJobExecutor> _logger;
    private readonly k8s.Kubernetes _client;

    public string Name => "kubernetes";

    public KubernetesJobExecutor(IJobStore store, IOptions<KubernetesExecutorOptions> options,
        ILogger<KubernetesJobExecutor> logger)
    {
        _store = store;
        _options = options.Value;
        _logger = logger;

        var config = _options.InCluster
            ? KubernetesClientConfiguration.InClusterConfig()
            : string.IsNullOrWhiteSpace(_options.KubeConfigPath)
                ? KubernetesClientConfiguration.BuildDefaultConfig()
                : KubernetesClientConfiguration.BuildConfigFromConfigFile(_options.KubeConfigPath);

        _client = new k8s.Kubernetes(config);
    }

    public async Task StartAsync(JobExecution exec, CancellationToken ct = default)
    {
        var jobName = $"klassd-workflows-{exec.Id}".ToLowerInvariant();
        var job = exec.Container is not null ? BuildContainerJob(jobName, exec) : BuildJob(jobName, exec);

        await _client.BatchV1.CreateNamespacedJobAsync(job, _options.Namespace, cancellationToken: ct);

        exec.ExecutorHandle = jobName;
        exec.Status = JobStatus.Starting;
        await _store.UpdateAsync(exec);

        // Tail the pod in the background; StartAsync only dispatches.
        if (exec.Container is not null)
            _ = Task.Run(() => FollowContainerAsync(jobName, exec), CancellationToken.None);
        else
            _ = Task.Run(() => FollowAsync(jobName, exec), CancellationToken.None);
    }

    public async Task StopAsync(JobExecution exec, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(exec.ExecutorHandle))
        {
            try
            {
                await _client.BatchV1.DeleteNamespacedJobAsync(
                    exec.ExecutorHandle, _options.Namespace,
                    new V1DeleteOptions { PropagationPolicy = "Background" }, cancellationToken: ct);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed deleting job {Job}", exec.ExecutorHandle); }
        }

        exec.Status = JobStatus.Stopped;
        exec.FinishedAt = DateTimeOffset.UtcNow;
        await _store.UpdateAsync(exec);
    }

    private V1Job BuildJob(string jobName, JobExecution exec)
    {
        var env = new List<V1EnvVar>
        {
            new() { Name = WorkerProtocol.EnvJobId, Value = exec.Id },
            new() { Name = WorkerProtocol.EnvJobName, Value = exec.JobName },
            new() { Name = WorkerProtocol.EnvJobType, Value = exec.JobTypeName },
            new() { Name = WorkerProtocol.EnvJobArgs, Value = JsonSerializer.Serialize(exec.Arguments) },
            PodIpEnvVar(),
        };
        var artifactSettings = new Dictionary<string, string>(_options.ArtifactSettings);
        if (_options.ArtifactProvider == "file" && !artifactSettings.ContainsKey("dir")
            && !string.IsNullOrWhiteSpace(_options.ArtifactDir))
            artifactSettings["dir"] = _options.ArtifactDir;
        env.Add(new V1EnvVar { Name = WorkerProtocol.EnvArtifactProvider, Value = _options.ArtifactProvider });
        env.Add(new V1EnvVar { Name = WorkerProtocol.EnvArtifactSettings, Value = JsonSerializer.Serialize(artifactSettings) });

        return new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = jobName,
                Labels = new Dictionary<string, string> { ["app"] = "klassd-workflows", ["klassd-workflows/execution"] = exec.Id }
            },
            Spec = new V1JobSpec
            {
                BackoffLimit = 0,
                TtlSecondsAfterFinished = _options.TtlSecondsAfterFinished,
                // Backstop: cap a daemon's lifetime so an orphaned one (scheduler died before teardown)
                // is still reaped by Kubernetes. Normal jobs complete on their own, so no deadline.
                ActiveDeadlineSeconds = exec.IsService ? _options.ServiceActiveDeadlineSeconds : null,
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta
                    {
                        Labels = new Dictionary<string, string> { ["app"] = "klassd-workflows", ["klassd-workflows/execution"] = exec.Id }
                    },
                    Spec = new V1PodSpec
                    {
                        RestartPolicy = "Never",
                        ServiceAccountName = _options.ServiceAccountName,
                        Containers = new List<V1Container>
                        {
                            new()
                            {
                                Name = "worker",
                                Image = _options.WorkerImage,
                                ImagePullPolicy = _options.ImagePullPolicy,
                                Env = env,
                                Resources = JobResourceResolver.Resolve(exec.JobTypeName, _options)
                            }
                        }
                    }
                }
            }
        };
    }

    /// <summary>The pod's own IP via the downward API — lets a service job advertise its address.</summary>
    private static V1EnvVar PodIpEnvVar() => new()
    {
        Name = WorkerProtocol.EnvPodIp,
        ValueFrom = new V1EnvVarSource { FieldRef = new V1ObjectFieldSelector { FieldPath = "status.podIP" } }
    };

    /// <summary>Builds a Job that runs an arbitrary container image (not the worker) for a container node.</summary>
    private V1Job BuildContainerJob(string jobName, JobExecution exec)
    {
        var spec = exec.Container!;

        // Static container env + the node's resolved arguments (bound inputs) as env vars + POD_IP.
        var env = spec.Env.Select(kv => new V1EnvVar { Name = kv.Key, Value = kv.Value }).ToList();
        foreach (var (k, v) in exec.Arguments)
            if (!k.StartsWith("__", StringComparison.Ordinal))
                env.Add(new V1EnvVar { Name = k, Value = v });
        env.Add(PodIpEnvVar());

        var ports = new List<V1ContainerPort>();
        if (spec.ServicePort is { } sp) ports.Add(new V1ContainerPort { ContainerPort = sp });
        if (spec.ReadyTcpPort is { } rp && rp != spec.ServicePort) ports.Add(new V1ContainerPort { ContainerPort = rp });

        var container = new V1Container
        {
            Name = "container",
            Image = spec.Image,
            ImagePullPolicy = spec.ImagePullPolicy ?? _options.ImagePullPolicy,
            Command = spec.Command.Count > 0 ? spec.Command.ToList() : null,
            Args = spec.Args.Count > 0 ? spec.Args.ToList() : null,
            Env = env,
            Ports = ports.Count > 0 ? ports : null,
        };
        if (spec.ReadyTcpPort is { } readyPort)
            container.ReadinessProbe = new V1Probe
            {
                TcpSocket = new V1TCPSocketAction { Port = readyPort },
                PeriodSeconds = 3,
                FailureThreshold = 60
            };

        return new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = jobName,
                Labels = new Dictionary<string, string> { ["app"] = "klassd-workflows", ["klassd-workflows/execution"] = exec.Id }
            },
            Spec = new V1JobSpec
            {
                BackoffLimit = 0,
                TtlSecondsAfterFinished = _options.TtlSecondsAfterFinished,
                // Backstop: cap a daemon's lifetime so an orphaned one (scheduler died before teardown)
                // is still reaped by Kubernetes. Normal jobs complete on their own, so no deadline.
                ActiveDeadlineSeconds = exec.IsService ? _options.ServiceActiveDeadlineSeconds : null,
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta
                    {
                        Labels = new Dictionary<string, string> { ["app"] = "klassd-workflows", ["klassd-workflows/execution"] = exec.Id }
                    },
                    Spec = new V1PodSpec
                    {
                        RestartPolicy = "Never",
                        ServiceAccountName = _options.ServiceAccountName,
                        Containers = new List<V1Container> { container }
                    }
                }
            }
        };
    }

    /// <summary>
    /// Follows an arbitrary-container node. Publishes the pod IP / address as outputs, marks the
    /// execution ready once the pod's Ready condition holds (service nodes), streams raw logs, and —
    /// for non-service containers — settles terminal status from the pod phase.
    /// </summary>
    private async Task FollowContainerAsync(string jobName, JobExecution exec)
    {
        try
        {
            var podName = await WaitForPodAsync(jobName);
            if (podName is null)
            {
                await FailAsync(exec.Id, "Container pod never started.");
                return;
            }

            await PublishAddressAndReadinessAsync(jobName, podName, exec);

            // Stream raw container logs into the console (no worker protocol here).
            try
            {
                using var response = await _client.CoreV1.ReadNamespacedPodLogWithHttpMessagesAsync(
                    podName, _options.Namespace, follow: true);
                using var reader = new StreamReader(response.Body);
                while (await reader.ReadLineAsync() is { } line)
                    await _store.AppendLogAsync(exec.Id, $"[{DateTimeOffset.Now:HH:mm:ss}] {line}");
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Container log stream ended for {Job}", jobName); }

            // Settle. Services are torn down by the orchestrator (StopAsync → already terminal here);
            // a non-service container's outcome is its pod phase.
            var current = await _store.GetAsync(exec.Id);
            if (current is not { IsTerminal: false }) return;

            if (current.IsService)
            {
                await FailAsync(exec.Id, "Service container exited before it was torn down.");
                return;
            }

            var phase = await ReadPodPhaseAsync(jobName);
            current.Status = phase == "Succeeded" ? JobStatus.Succeeded : JobStatus.Failed;
            if (current.Status == JobStatus.Failed) current.Error = $"Container ended in phase '{phase}'.";
            current.FinishedAt = DateTimeOffset.UtcNow;
            await _store.UpdateAsync(current);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error following container pod for {Job}", jobName);
            await FailAsync(exec.Id, ex.Message);
        }
    }

    /// <summary>Polls until the pod has an IP (and, for services, is Ready); publishes ip/address outputs.</summary>
    private async Task PublishAddressAndReadinessAsync(string jobName, string podName, JobExecution exec)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            V1Pod pod;
            try { pod = await _client.CoreV1.ReadNamespacedPodAsync(podName, _options.Namespace); }
            catch { break; }

            var ip = pod.Status?.PodIP;
            var live = await _store.GetAsync(exec.Id);
            if (live is null || live.IsTerminal) return;

            if (live.Status is JobStatus.Starting or JobStatus.Enqueued)
            {
                live.Status = JobStatus.Running;
                live.StartedAt ??= DateTimeOffset.UtcNow;
            }

            if (!string.IsNullOrEmpty(ip))
            {
                live.Outputs["ip"] = ip;
                if (exec.Container!.ServicePort is { } port)
                    live.Outputs["address"] = $"{ip}:{port}";
            }

            var ready = pod.Status?.Conditions?.Any(c => c.Type == "Ready" && c.Status == "True") == true;
            if (exec.IsService && ready && !live.Ready)
            {
                live.Ready = true;
                live.ReadyAt = DateTimeOffset.UtcNow;
            }
            await _store.UpdateAsync(live);

            // Done once we have what the node needs: a ready service, or an addressed/running container.
            if (exec.IsService ? live.Ready : !string.IsNullOrEmpty(ip))
                return;

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private async Task<string?> ReadPodPhaseAsync(string jobName)
    {
        var pods = await _client.CoreV1.ListNamespacedPodAsync(_options.Namespace, labelSelector: $"job-name={jobName}");
        return pods.Items.FirstOrDefault()?.Status?.Phase;
    }

    private async Task FailAsync(string execId, string error)
    {
        var current = await _store.GetAsync(execId);
        if (current is { IsTerminal: false })
        {
            current.Status = JobStatus.Failed;
            current.Error = error;
            current.FinishedAt = DateTimeOffset.UtcNow;
            await _store.UpdateAsync(current);
        }
    }

    private async Task FollowAsync(string jobName, JobExecution exec)
    {
        try
        {
            var podName = await WaitForPodAsync(jobName);
            if (podName is null)
            {
                exec.Status = JobStatus.Failed;
                exec.Error = "Worker pod never started.";
                exec.FinishedAt = DateTimeOffset.UtcNow;
                await _store.UpdateAsync(exec);
                return;
            }

            using var response = await _client.CoreV1.ReadNamespacedPodLogWithHttpMessagesAsync(
                podName, _options.Namespace, follow: true);
            using var reader = new StreamReader(response.Body);

            while (await reader.ReadLineAsync() is { } line)
                await WorkerOutputProcessor.ProcessLineAsync(_store, exec, line);

            // If the worker never reported a terminal state, infer it from the pod.
            var current = await _store.GetAsync(exec.Id);
            if (current is { IsTerminal: false })
            {
                current.Status = JobStatus.Failed;
                current.Error = "Pod log stream ended before the job reported completion.";
                current.FinishedAt = DateTimeOffset.UtcNow;
                await _store.UpdateAsync(current);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error following pod logs for {Job}", jobName);
            var current = await _store.GetAsync(exec.Id);
            if (current is { IsTerminal: false })
            {
                current.Status = JobStatus.Failed;
                current.Error = ex.Message;
                current.FinishedAt = DateTimeOffset.UtcNow;
                await _store.UpdateAsync(current);
            }
        }
    }

    private async Task<string?> WaitForPodAsync(string jobName)
    {
        // Poll for a pod owned by the Job that has progressed past "pending scheduling".
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var pods = await _client.CoreV1.ListNamespacedPodAsync(
                _options.Namespace, labelSelector: $"job-name={jobName}");

            var pod = pods.Items.FirstOrDefault();
            var phase = pod?.Status?.Phase;
            if (pod is not null && phase is "Running" or "Succeeded" or "Failed")
                return pod.Metadata.Name;

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        return null;
    }
}
