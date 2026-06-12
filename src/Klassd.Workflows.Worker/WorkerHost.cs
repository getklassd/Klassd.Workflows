using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using Klassd.Workflows.Abstractions;
using Klassd.Workflows.Core.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Klassd.Workflows.Worker;

/// <summary>
/// The executor-pod entry point, packaged so you can build your own worker image: reference this
/// package plus your job assemblies from a one-line exe (<c>return await WorkerHost.RunAsync(args);</c>)
/// and publish it, or layer your job DLLs onto the published base image. It reads the job descriptor
/// from environment variables (<see cref="WorkerProtocol"/>), loads the requested <see cref="IJob"/>
/// from the assemblies on its load path, runs it, and reports outcome via the stdout protocol the
/// scheduler tails.
/// </summary>
public static class WorkerHost
{
    /// <summary>
    /// Runs the single job described by the environment and returns a process exit code
    /// (0 success, 1 failure, 2 cancelled). <paramref name="args"/> is currently unused — the job
    /// descriptor travels in env vars — but is accepted so callers can forward <c>Main</c>'s args.
    /// </summary>
    public static async Task<int> RunAsync(string[]? args = null)
    {
        var stdout = Console.Out;

        string jobId = Environment.GetEnvironmentVariable(WorkerProtocol.EnvJobId) ?? "unknown";
        string jobName = Environment.GetEnvironmentVariable(WorkerProtocol.EnvJobName) ?? "unknown";
        string? jobType = Environment.GetEnvironmentVariable(WorkerProtocol.EnvJobType);
        string argsJson = Environment.GetEnvironmentVariable(WorkerProtocol.EnvJobArgs) ?? "{}";

        void Fail(string message)
        {
            stdout.WriteLine($"{WorkerProtocol.StatePrefix} Failed {message}");
            stdout.Flush();
        }

        if (string.IsNullOrWhiteSpace(jobType))
        {
            Fail($"No {WorkerProtocol.EnvJobType} provided.");
            return 1;
        }

        var jobArgs = JsonSerializer.Deserialize<Dictionary<string, string>>(argsJson) ?? new();

        // Make sure job assemblies sitting next to the worker are loaded so the type (and any
        // IWorkerStartup / IArtifactStoreProvider) resolves.
        LoadAssembliesInBaseDirectory();

        var type = ResolveType(jobType);
        if (type is null)
        {
            Fail($"Job type '{jobType}' not found in loaded assemblies.");
            return 1;
        }

        IJob job;
        try
        {
            job = CreateJob(type, stdout);
        }
        catch (Exception ex)
        {
            Fail($"Could not create job '{jobType}': {ex.Message}");
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

        // Resolve the artifact store from the configured provider (file | gcs | s3 | any
        // IArtifactStoreProvider on the load path). Providers are discovered from the assemblies
        // next to the worker — adding a backend needs no worker changes.
        var artifactProvider = Environment.GetEnvironmentVariable(WorkerProtocol.EnvArtifactProvider) ?? "file";
        var artifactSettings = JsonSerializer.Deserialize<Dictionary<string, string>>(
            Environment.GetEnvironmentVariable(WorkerProtocol.EnvArtifactSettings) ?? "{}") ?? new();
        // Back-compat: a bare KLASSD_ARTIFACT_DIR feeds the file provider's "dir".
        if (!artifactSettings.ContainsKey("dir") &&
            Environment.GetEnvironmentVariable(WorkerProtocol.EnvArtifactDir) is { } legacyDir)
            artifactSettings["dir"] = legacyDir;

        var artifacts = ArtifactStoreResolver.Resolve(artifactProvider, artifactSettings);

        // The pod's own IP (downward API in K8s); empty locally, where 127.0.0.1 is the right address.
        var podIp = Environment.GetEnvironmentVariable(WorkerProtocol.EnvPodIp);
        if (string.IsNullOrWhiteSpace(podIp)) podIp = "127.0.0.1";

        var context = new JobContext(jobId, jobName, jobArgs, cts.Token, stdout, artifacts, podIp);

        try
        {
            context.Log($"Starting {jobName} ({jobType})");
            await job.RunAsync(context);
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
    /// Creates the job. If an <see cref="IWorkerStartup"/> is present on the load path, it composes a
    /// configuration + DI container so the job can take constructor dependencies; otherwise an empty
    /// container is used. Either way the job is created with <see cref="ActivatorUtilities"/>, which
    /// also handles a parameterless constructor — so jobs needing nothing keep working unchanged.
    /// </summary>
    private static IJob CreateJob(Type type, TextWriter stdout)
    {
        var configuration = BuildConfiguration();
        var services = new ServiceCollection();
        services.AddSingleton(configuration);

        var startup = DiscoverStartup(stdout);
        startup?.Configure(services, configuration);

        var provider = services.BuildServiceProvider();
        return (IJob)ActivatorUtilities.CreateInstance(provider, type);
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

    private static IWorkerStartup? DiscoverStartup(TextWriter stdout)
    {
        var candidates = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(SafeGetTypes)
            .Where(t => typeof(IWorkerStartup).IsAssignableFrom(t)
                        && t is { IsAbstract: false, IsInterface: false }
                        && t.GetConstructor(Type.EmptyTypes) is not null)
            .ToList();

        if (candidates.Count == 0) return null;
        if (candidates.Count > 1)
            stdout.WriteLine($"{WorkerProtocol.LogPrefix} Multiple IWorkerStartup implementations found " +
                             $"({string.Join(", ", candidates.Select(c => c.FullName))}); using {candidates[0].FullName}.");

        return (IWorkerStartup)Activator.CreateInstance(candidates[0])!;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }

    private static void SafeCancel(CancellationTokenSource cts)
    {
        try { cts.Cancel(); } catch (ObjectDisposedException) { /* already shutting down */ }
    }

    private static void LoadAssembliesInBaseDirectory()
    {
        var dir = AppContext.BaseDirectory;
        foreach (var dll in Directory.GetFiles(dir, "*.dll"))
        {
            try
            {
                var name = AssemblyName.GetAssemblyName(dll);
                if (!AssemblyLoadContext.Default.Assemblies.Any(a => a.GetName().Name == name.Name))
                    AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
            }
            catch { /* skip native/unmanaged dlls */ }
        }
    }

    private static Type? ResolveType(string typeName) =>
        Type.GetType(typeName) ??
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(typeName))
            .FirstOrDefault(t => t is not null);
}
