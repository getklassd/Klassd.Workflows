using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Klassd.Workflows.Abstractions;
using Klassd.Workflows.Core.Abstractions;
using Klassd.Workflows.Core.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Klassd.Workflows.Core.Execution;

public sealed class LocalExecutorOptions
{
    /// <summary>Path to the built Klassd.Workflows.Worker.dll.</summary>
    public string WorkerDllPath { get; set; } = "";

    /// <summary>The dotnet host used to launch the worker.</summary>
    public string DotnetPath { get; set; } = "dotnet";

    /// <summary>Shared directory workers use for the "file" artifact provider.</summary>
    public string ArtifactDir { get; set; } = Path.Combine(Path.GetTempPath(), "klassd-workflows-artifacts");

    /// <summary>Artifact provider name the worker should use (file | gcs | s3 | custom).</summary>
    public string ArtifactProvider { get; set; } = "file";

    /// <summary>Provider-specific artifact settings (e.g. bucket, prefix).</summary>
    public Dictionary<string, string> ArtifactSettings { get; set; } = new();
}

/// <summary>
/// Dev/test executor: runs each job as a local <c>dotnet Worker.dll</c> process
/// instead of a pod. Same worker, same stdout protocol — so behaviour matches
/// the Kubernetes path without needing a cluster.
/// </summary>
public sealed class LocalProcessJobExecutor : IJobExecutor
{
    private readonly IJobStore _store;
    private readonly LocalExecutorOptions _options;
    private readonly ILogger<LocalProcessJobExecutor> _logger;
    private readonly ConcurrentDictionary<string, Process> _running = new();

    public string Name => "local";

    public LocalProcessJobExecutor(IJobStore store, IOptions<LocalExecutorOptions> options,
        ILogger<LocalProcessJobExecutor> logger)
    {
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    public Task StartAsync(JobExecution exec, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.DotnetPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(_options.WorkerDllPath);
        psi.Environment[WorkerProtocol.EnvJobId] = exec.Id;
        psi.Environment[WorkerProtocol.EnvJobName] = exec.JobName;
        psi.Environment[WorkerProtocol.EnvJobType] = exec.JobTypeName;
        psi.Environment[WorkerProtocol.EnvJobArgs] = JsonSerializer.Serialize(exec.Arguments);

        var artifactSettings = new Dictionary<string, string>(_options.ArtifactSettings);
        if (_options.ArtifactProvider == "file" && !artifactSettings.ContainsKey("dir"))
            artifactSettings["dir"] = _options.ArtifactDir;
        psi.Environment[WorkerProtocol.EnvArtifactProvider] = _options.ArtifactProvider;
        psi.Environment[WorkerProtocol.EnvArtifactSettings] = JsonSerializer.Serialize(artifactSettings);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.OutputDataReceived += async (_, e) =>
        {
            if (e.Data is null) return;
            try { await WorkerOutputProcessor.ProcessLineAsync(_store, exec, e.Data); }
            catch (Exception ex) { _logger.LogError(ex, "Failed processing worker output"); }
        };
        process.ErrorDataReceived += async (_, e) =>
        {
            if (e.Data is null) return;
            await _store.AppendLogAsync(exec.Id, $"[stderr] {e.Data}");
        };
        process.Exited += async (_, _) =>
        {
            _running.TryRemove(exec.Id, out _);
            // If the worker died without emitting a terminal state, mark it failed.
            var current = await _store.GetAsync(exec.Id);
            if (current is { IsTerminal: false })
            {
                current.Status = JobStatus.Failed;
                current.Error = $"Worker exited with code {process.ExitCode} before reporting completion.";
                current.FinishedAt = DateTimeOffset.UtcNow;
                await _store.UpdateAsync(current);
            }
            process.Dispose();
        };

        exec.Status = JobStatus.Starting;
        process.Start();
        exec.ExecutorHandle = $"pid:{process.Id}";
        _running[exec.Id] = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return _store.UpdateAsync(exec);
    }

    public Task StopAsync(JobExecution exec, CancellationToken ct = default)
    {
        if (_running.TryRemove(exec.Id, out var process))
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to kill worker process"); }
        }
        exec.Status = JobStatus.Stopped;
        exec.FinishedAt = DateTimeOffset.UtcNow;
        return _store.UpdateAsync(exec);
    }
}
