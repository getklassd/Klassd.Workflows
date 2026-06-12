using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
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
    private readonly ConcurrentDictionary<string, string> _containers = new(); // execId → docker container id

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
        // Init containers and volumes are pod concepts; the local shim has no equivalent, so they're skipped.
        var initCount = exec.InitContainers.Count + (exec.Container?.InitContainers.Count ?? 0);
        var volumeCount = exec.Volumes.Count + (exec.Container?.Volumes.Count ?? 0);
        if (initCount > 0 || volumeCount > 0)
            _logger.LogDebug("Local executor ignores {Init} init container(s) and {Volumes} volume(s) on job {Id} (Kubernetes-only).",
                initCount, volumeCount, exec.Id);

        if (exec.Container is not null)
            return StartContainerAsync(exec);

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
        if (exec.FileOutputs.Count > 0)
            psi.Environment[WorkerProtocol.EnvOutputSpecs] = JsonSerializer.Serialize(exec.FileOutputs);

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
        if (_containers.TryRemove(exec.Id, out var containerId))
        {
            try { RunDocker($"rm -f {containerId}", waitForExit: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to remove container {Id}", containerId); }
        }
        if (_running.TryRemove(exec.Id, out var process))
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to kill worker process"); }
        }
        exec.Status = JobStatus.Stopped;
        exec.FinishedAt = DateTimeOffset.UtcNow;
        return _store.UpdateAsync(exec);
    }

    // ── Container nodes: run an arbitrary image via `docker` (dev parity with the K8s path) ──────
    private async Task StartContainerAsync(JobExecution exec)
    {
        var spec = exec.Container!;
        exec.Status = JobStatus.Starting;
        await _store.UpdateAsync(exec);

        // docker run [-d|--rm] [-p 127.0.0.1::<servicePort>] -e K=V ... [--entrypoint cmd0] image [args]
        // Services run detached and are NOT auto-removed: if one crashes we want the dead container to
        // linger so we can read its logs and exit code. We rm it explicitly at teardown (StopAsync).
        // Run-to-completion containers self-clean with --rm.
        var args = new List<string> { "run" };
        if (exec.IsService) args.Add("-d");
        else args.Add("--rm");
        if (spec.ServicePort is { } sp) { args.Add("-p"); args.Add($"127.0.0.1::{sp}"); }

        foreach (var (k, v) in spec.Env) { args.Add("-e"); args.Add($"{k}={v}"); }
        foreach (var (k, v) in exec.Arguments)
            if (!k.StartsWith("__", StringComparison.Ordinal)) { args.Add("-e"); args.Add($"{k}={v}"); }
        args.Add("-e"); args.Add($"{WorkerProtocol.EnvPodIp}=127.0.0.1");

        // Declarative file outputs: bind-mount a host temp dir per output directory so we can read the
        // files back after the (run-to-completion) container exits. Services don't produce outputs.
        var outputReads = new List<(OutputSpec spec, string hostPath)>();
        if (!exec.IsService)
        {
            var dirMap = new Dictionary<string, string>();
            foreach (var o in spec.FileOutputs.Concat(exec.FileOutputs))
            {
                var dir = PosixDir(o.Path);
                if (dir.Length == 0) continue;
                if (!dirMap.TryGetValue(dir, out var hostDir))
                {
                    hostDir = Path.Combine(Path.GetTempPath(), $"klassd-out-{exec.Id}", $"d{dirMap.Count}");
                    Directory.CreateDirectory(hostDir);
                    dirMap[dir] = hostDir;
                    args.Add("-v"); args.Add($"{hostDir}:{dir}");
                }
                outputReads.Add((o, Path.Combine(hostDir, o.Path[(o.Path.LastIndexOf('/') + 1)..])));
            }
        }

        var command = spec.Command.ToList();
        if (command.Count > 0) { args.Add("--entrypoint"); args.Add(command[0]); }
        args.Add(spec.Image);
        foreach (var c in command.Skip(1)) args.Add(c);
        foreach (var a in spec.Args) args.Add(a);

        if (exec.IsService)
            await StartServiceContainerAsync(exec, spec, args);
        else
            _ = Task.Run(() => RunContainerToCompletionAsync(exec, args, outputReads));
    }

    private static string PosixDir(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx <= 0 ? "" : path[..idx];
    }

    private async Task StartServiceContainerAsync(JobExecution exec, ContainerSpec spec, List<string> runArgs)
    {
        var (code, stdout, stderr) = await RunDockerCaptureAsync(runArgs);
        if (code != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            await FailAsync(exec.Id, $"docker run failed: {stderr.Trim()}");
            return;
        }

        var containerId = stdout.Trim().Split('\n')[^1].Trim();
        _containers[exec.Id] = containerId;
        exec.ExecutorHandle = $"docker:{containerId[..Math.Min(12, containerId.Length)]}";

        // Guard against an image that crashes on startup (e.g. cloud-sql-proxy pointed at a bad
        // instance / missing credentials). Without this we'd stream logs and probe a container that's
        // already gone, surfacing a cryptic "No such container" instead of the real reason.
        var (exists, running, exitCode) = await InspectContainerAsync(containerId);
        if (!exists || !running)
        {
            var (_, outLog, errLog) = await RunDockerCaptureAsync(["logs", containerId]);
            var detail = $"{outLog}{errLog}".Trim();
            if (detail.Length > 800) detail = "…" + detail[^800..];
            _containers.TryRemove(exec.Id, out _);
            try { RunDocker($"rm -f {containerId}", waitForExit: true); } catch { /* best effort */ }
            await FailAsync(exec.Id,
                $"Service container '{exec.JobName}' exited during startup (exit code {exitCode})"
                + (detail.Length > 0 ? $": {detail}" : "."));
            return;
        }

        exec.Status = JobStatus.Running;
        exec.StartedAt ??= DateTimeOffset.UtcNow;

        // Resolve the published host port → address other local processes can reach.
        int? hostPort = null;
        if (spec.ServicePort is { } sp)
        {
            var (pc, pout, _) = await RunDockerCaptureAsync(["port", containerId, $"{sp}/tcp"]);
            if (pc == 0 && pout.Trim() is { Length: > 0 } mapping)
            {
                var lastColon = mapping.Split('\n')[0].Trim().LastIndexOf(':');
                if (lastColon >= 0 && int.TryParse(mapping.Split('\n')[0].Trim()[(lastColon + 1)..], out var hp))
                    hostPort = hp;
            }
            exec.Outputs["ip"] = "127.0.0.1";
            exec.Outputs["address"] = $"127.0.0.1:{hostPort ?? sp}";
        }
        await _store.UpdateAsync(exec);

        StreamContainerLogs(exec.Id, containerId);

        // Readiness: wait until the published port accepts a TCP connection.
        var probePort = hostPort ?? spec.ReadyTcpPort;
        if (probePort is { } pp && !await WaitTcpAsync("127.0.0.1", pp, TimeSpan.FromSeconds(30)))
        {
            // Port never opened. The container may have crashed late or just be wedged — surface why.
            var (_, outLog, errLog) = await RunDockerCaptureAsync(["logs", "--tail", "40", containerId]);
            var detail = $"{outLog}{errLog}".Trim();
            if (detail.Length > 800) detail = "…" + detail[^800..];
            _containers.TryRemove(exec.Id, out _);
            try { RunDocker($"rm -f {containerId}", waitForExit: true); } catch { /* best effort */ }
            await FailAsync(exec.Id,
                $"Service container '{exec.JobName}' did not become ready on port {pp} within 30s"
                + (detail.Length > 0 ? $": {detail}" : "."));
            return;
        }

        var live = await _store.GetAsync(exec.Id);
        if (live is { IsTerminal: false } && !live.Ready)
        {
            live.Ready = true;
            live.ReadyAt = DateTimeOffset.UtcNow;
            await _store.UpdateAsync(live);
        }
    }

    private async Task RunContainerToCompletionAsync(JobExecution exec, List<string> runArgs,
        IReadOnlyList<(OutputSpec spec, string hostPath)>? outputReads = null)
    {
        try
        {
            var proc = StartDocker(runArgs, out var started);
            if (!started) { await FailAsync(exec.Id, "Docker is not available (could not start `docker`)."); return; }

            _running[exec.Id] = proc;
            exec.Status = JobStatus.Running;
            exec.StartedAt ??= DateTimeOffset.UtcNow;
            await _store.UpdateAsync(exec);

            proc.OutputDataReceived += async (_, e) => { if (e.Data is not null) await _store.AppendLogAsync(exec.Id, $"[{DateTimeOffset.Now:HH:mm:ss}] {e.Data}"); };
            proc.ErrorDataReceived += async (_, e) => { if (e.Data is not null) await _store.AppendLogAsync(exec.Id, $"[stderr] {e.Data}"); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync();
            _running.TryRemove(exec.Id, out _);

            var current = await _store.GetAsync(exec.Id);
            if (current is { IsTerminal: false })
            {
                current.Status = proc.ExitCode == 0 ? JobStatus.Succeeded : JobStatus.Failed;
                if (current.Status == JobStatus.Failed) current.Error = $"Container exited with code {proc.ExitCode}.";
                current.Progress = current.Status == JobStatus.Succeeded ? 100 : current.Progress;
                CollectFileOutputs(current, outputReads);
                current.FinishedAt = DateTimeOffset.UtcNow;
                await _store.UpdateAsync(current);
            }
        }
        catch (Exception ex) { await FailAsync(exec.Id, ex.Message); }
    }

    // Read each declared output file the container wrote (or fall back to its default) and publish it.
    private static void CollectFileOutputs(JobExecution exec, IReadOnlyList<(OutputSpec spec, string hostPath)>? outputReads)
    {
        if (outputReads is null) return;
        foreach (var (spec, hostPath) in outputReads)
        {
            string? value = null;
            try
            {
                if (File.Exists(hostPath) && File.ReadAllText(hostPath).Trim() is { Length: > 0 } content)
                    value = content;
            }
            catch { /* unreadable -> default */ }
            value ??= spec.Default;
            if (value is not null) exec.Outputs[spec.Name] = value;
        }
    }

    private void StreamContainerLogs(string execId, string containerId)
    {
        var proc = StartDocker(["logs", "-f", containerId], out var started);
        if (!started) return;
        proc.OutputDataReceived += async (_, e) => { if (e.Data is not null) await _store.AppendLogAsync(execId, $"[{DateTimeOffset.Now:HH:mm:ss}] {e.Data}"); };
        proc.ErrorDataReceived += async (_, e) => { if (e.Data is not null) await _store.AppendLogAsync(execId, $"[{DateTimeOffset.Now:HH:mm:ss}] {e.Data}"); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
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

    // Returns true once the port accepts a connection, false if the timeout elapses first.
    private static async Task<bool> WaitTcpAsync(string host, int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await client.ConnectAsync(host, port, cts.Token);
                return true; // connected → ready
            }
            catch { await Task.Delay(500); }
        }
        return false;
    }

    private Process StartDocker(IEnumerable<string> args, out bool started)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var proc = new Process { StartInfo = psi };
        try { started = proc.Start(); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to start docker"); started = false; }
        return proc;
    }

    // Best-effort container state probe: (exists, running, exitCode). exists=false if docker can't find it.
    private async Task<(bool exists, bool running, int exitCode)> InspectContainerAsync(string containerId)
    {
        var (code, stdout, _) = await RunDockerCaptureAsync(
            ["inspect", "-f", "{{.State.Running}} {{.State.ExitCode}}", containerId]);
        if (code != 0) return (false, false, 0);
        var parts = stdout.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var running = parts.Length > 0 && parts[0].Equals("true", StringComparison.OrdinalIgnoreCase);
        var exit = parts.Length > 1 && int.TryParse(parts[1], out var c) ? c : 0;
        return (true, running, exit);
    }

    private async Task<(int code, string stdout, string stderr)> RunDockerCaptureAsync(IEnumerable<string> args)
    {
        var proc = StartDocker(args, out var started);
        if (!started) return (-1, "", "Docker is not available (could not start `docker`).");
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout, stderr);
    }

    private void RunDocker(string args, bool waitForExit)
    {
        var proc = StartDocker(args.Split(' ', StringSplitOptions.RemoveEmptyEntries), out var started);
        if (started && waitForExit) proc.WaitForExit(5000);
    }
}
