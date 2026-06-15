using System.Runtime.InteropServices;
using System.Text.Json;
using Klassd.Workflows.Abstractions;
using Klassd.Workflows.Core.Model;
using Klassd.Workflows.Core.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Klassd.Workflows.Worker;

/// <summary>
/// The executor-pod entry point. Build a worker with <see cref="CreateBuilder"/>: reference this
/// package from a thin exe, register the jobs it can run, and publish it as your worker image. It
/// reads the job descriptor from environment variables (<see cref="WorkerProtocol"/>), constructs the
/// requested <see cref="IJob"/> from the registry, runs it, and reports outcome via the stdout
/// protocol the scheduler tails.
/// </summary>
public static class WorkerHost
{
    /// <summary>Start building a worker. See <see cref="WorkerHostBuilder"/> for the fluent API.</summary>
    public static WorkerHostBuilder CreateBuilder(string[]? args = null) => new(args ?? []);

    /// <summary>
    /// Runs the single job described by the environment and returns a process exit code
    /// (0 success, 1 failure, 2 cancelled). <paramref name="args"/> is currently unused — the job
    /// descriptor travels in env vars — but is accepted so callers can forward <c>Main</c>'s args.
    /// </summary>
    internal static async Task<int> RunAsync(
        string[]? args,
        IReadOnlyList<Action<IServiceCollection, IConfiguration>> configureServices,
        IJobRegistry registry,
        IReadOnlyList<IArtifactStoreProvider> artifactProviders)
    {
        var stdout = Console.Out;

        // Capture-sidecar mode: no job — just wait for the main container to exit, then publish its
        // declared file outputs. Used for arbitrary-container nodes (see RunCaptureAsync).
        if (Environment.GetEnvironmentVariable(WorkerProtocol.EnvCaptureOutputs) is { Length: > 0 } captureJson)
            return await RunCaptureAsync(captureJson, stdout);

        string jobId = Environment.GetEnvironmentVariable(WorkerProtocol.EnvJobId) ?? "unknown";
        string jobName = Environment.GetEnvironmentVariable(WorkerProtocol.EnvJobName) ?? "unknown";
        string? jobKey = Environment.GetEnvironmentVariable(WorkerProtocol.EnvJobType);
        string argsJson = Environment.GetEnvironmentVariable(WorkerProtocol.EnvJobArgs) ?? "{}";

        void Fail(string message)
        {
            stdout.WriteLine($"{WorkerProtocol.StatePrefix} Failed {message}");
            stdout.Flush();
        }

        if (string.IsNullOrWhiteSpace(jobKey))
        {
            Fail($"No {WorkerProtocol.EnvJobType} provided.");
            return 1;
        }

        var jobArgs = JsonSerializer.Deserialize<Dictionary<string, string>>(argsJson) ?? new();

        IJob job;
        try
        {
            job = CreateJob(registry, jobKey, configureServices);
        }
        catch (Exception ex)
        {
            Fail($"Could not create job '{jobKey}': {ex.Message}");
            return 1;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; SafeCancel(cts); };
        // Kubernetes sends SIGTERM when a job is stopped / the pod is deleted.
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            SafeCancel(cts);
        });

        // Resolve the artifact store from the configured provider name (file | gcs | s3 | …),
        // selecting from the providers this worker registered via AddArtifactProvider. The built-in
        // "file" provider is always available as the fallback.
        var artifactProvider = Environment.GetEnvironmentVariable(WorkerProtocol.EnvArtifactProvider) ?? "file";
        var artifactSettings = JsonSerializer.Deserialize<Dictionary<string, string>>(
            Environment.GetEnvironmentVariable(WorkerProtocol.EnvArtifactSettings) ?? "{}") ?? new();
        // Back-compat: a bare KLASSD_ARTIFACT_DIR feeds the file provider's "dir".
        if (!artifactSettings.ContainsKey("dir") &&
            Environment.GetEnvironmentVariable(WorkerProtocol.EnvArtifactDir) is { } legacyDir)
            artifactSettings["dir"] = legacyDir;

        var artifacts = ArtifactStoreResolver.Resolve(artifactProvider, artifactSettings, artifactProviders);

        // The pod's own IP (downward API in K8s); empty locally, where 127.0.0.1 is the right address.
        var podIp = Environment.GetEnvironmentVariable(WorkerProtocol.EnvPodIp);
        if (string.IsNullOrWhiteSpace(podIp)) podIp = "127.0.0.1";

        var context = new JobContext(jobId, jobName, jobArgs, cts.Token, stdout, artifacts, podIp);

        try
        {
            context.Log($"Starting {jobName} ({jobKey})");
            await job.RunAsync(context);
            // Declarative file outputs: read the files the job wrote (or fall back to defaults) and
            // publish them as node outputs, before reporting success.
            EmitFileOutputs(Environment.GetEnvironmentVariable(WorkerProtocol.EnvOutputSpecs), context.SetOutput);
            stdout.WriteLine($"{WorkerProtocol.StatePrefix} Succeeded");
            stdout.Flush();
            return 0;
        }
        catch (OperationCanceledException)
        {
            Fail("Job was cancelled.");
            return 2;
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            stdout.WriteLine($"{WorkerProtocol.LogPrefix} {ex}");
            stdout.Flush();
            return 1;
        }
    }

    /// <summary>
    /// Capture-sidecar mode for arbitrary-container nodes: this runs alongside the node's container
    /// (as a Kubernetes native sidecar) sharing the outputs volume. It idles until the main container
    /// exits — at which point Kubernetes sends it SIGTERM — then reads the declared output files (or
    /// their defaults) from the shared volume and emits them as <c>##OUTPUT##</c> lines the executor
    /// collects from this container's log.
    /// </summary>
    private static async Task<int> RunCaptureAsync(string specsJson, TextWriter stdout)
    {
        var done = new TaskCompletionSource();
        void Emit()
        {
            EmitFileOutputs(specsJson, (k, v) => stdout.WriteLine($"{WorkerProtocol.OutputPrefix} {k} {v}"));
            stdout.Flush();
            done.TrySetResult();
        }
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; Emit(); });
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; Emit(); });
        await done.Task;
        return 0;
    }

    /// <summary>
    /// Reads each declared output file (trimmed) — or its default when the file is missing/empty —
    /// and publishes it via <paramref name="setOutput"/>. Used both in-pod after an IJob runs and in
    /// the capture sidecar for container nodes.
    /// </summary>
    private static void EmitFileOutputs(string? specsJson, Action<string, string> setOutput)
    {
        if (string.IsNullOrWhiteSpace(specsJson)) return;
        List<OutputSpec>? specs;
        try { specs = JsonSerializer.Deserialize<List<OutputSpec>>(specsJson); }
        catch { return; }

        foreach (var s in specs ?? [])
        {
            string? value = null;
            try
            {
                if (File.Exists(s.Path) && File.ReadAllText(s.Path).Trim() is { Length: > 0 } content)
                    value = content;
            }
            catch { /* unreadable -> fall back to default */ }
            value ??= s.Default;
            if (value is not null) setOutput(s.Name, value);
        }
    }

    /// <summary>
    /// Looks the dispatch key up in the registry and constructs the job from a fresh service provider.
    /// Worker-wide <paramref name="configureServices"/> callbacks register cross-cutting dependencies;
    /// the matched registration's own <see cref="JobRegistration.ConfigureServices"/> then registers
    /// just that job's dependencies — and because a pod runs only the dispatched job, no other job's
    /// services are ever built. The registration's factory (<see cref="ActivatorUtilities"/> for
    /// <c>Add&lt;T&gt;()</c>, or a user-supplied lambda) builds the instance. Throws if no job is
    /// registered under the key.
    /// </summary>
    private static IJob CreateJob(IJobRegistry registry, string key,
        IReadOnlyList<Action<IServiceCollection, IConfiguration>> configureServices)
    {
        if (!registry.TryGet(key, out var registration))
            throw new InvalidOperationException(
                $"No job registered for key '{key}'. Registered keys: " +
                $"{string.Join(", ", registry.Registrations.Select(r => r.Key))}.");

        var configuration = BuildConfiguration();
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        foreach (var configure in configureServices) configure(services, configuration);
        registration.ConfigureServices?.Invoke(services, configuration);

        var provider = services.BuildServiceProvider();
        return registration.Factory(provider);
    }

    /// <summary>
    /// appsettings[.{ENV}].json (optional) → every <c>/secrets/*.json</c> (the Vault-agent drop dir,
    /// if present) → environment variables. Last source wins, so env overrides files.
    /// </summary>
    private static IConfiguration BuildConfiguration()
    {
        var env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                  ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
        if (!string.IsNullOrWhiteSpace(env))
            builder.AddJsonFile($"appsettings.{env}.json", optional: true, reloadOnChange: false);

        const string secretsDir = "/secrets";
        if (Directory.Exists(secretsDir))
            foreach (var file in Directory.EnumerateFiles(secretsDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                builder.AddJsonFile(file, optional: true, reloadOnChange: false);

        return builder.AddEnvironmentVariables().Build();
    }

    private static void SafeCancel(CancellationTokenSource cts)
    {
        try { cts.Cancel(); } catch (ObjectDisposedException) { /* already shutting down */ }
    }
}
