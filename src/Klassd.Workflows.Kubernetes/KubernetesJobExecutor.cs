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
        var job = BuildJob(jobName, exec);

        await _client.BatchV1.CreateNamespacedJobAsync(job, _options.Namespace, cancellationToken: ct);

        exec.ExecutorHandle = jobName;
        exec.Status = JobStatus.Starting;
        await _store.UpdateAsync(exec);

        // Tail the pod in the background; StartAsync only dispatches.
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
