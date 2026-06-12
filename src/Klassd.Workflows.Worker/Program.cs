using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using Klassd.Workflows.Abstractions;
using Klassd.Workflows.Core.Storage;
using Klassd.Workflows.Worker;

// Entry point of the executor pod. Reads the job descriptor from the
// environment, loads the IJob implementation by type name, runs it, and
// reports outcome via the stdout protocol. The scheduler tails this output.

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

// Make sure job assemblies sitting next to the worker are loaded so the type resolves.
LoadAssembliesInBaseDirectory();

var type = ResolveType(jobType);
if (type is null)
{
    Fail($"Job type '{jobType}' not found in loaded assemblies.");
    return 1;
}

if (Activator.CreateInstance(type) is not IJob job)
{
    Fail($"Type '{jobType}' does not implement IJob or has no parameterless constructor.");
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

// Resolve the artifact store from the configured provider (file | gcs | s3 |
// any IArtifactStoreProvider on the load path). Providers are discovered from
// the assemblies next to the worker — adding a backend needs no worker changes.
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

static void SafeCancel(CancellationTokenSource cts)
{
    try { cts.Cancel(); } catch (ObjectDisposedException) { /* already shutting down */ }
}

static void LoadAssembliesInBaseDirectory()
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

static Type? ResolveType(string typeName) =>
    Type.GetType(typeName) ??
    AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetType(typeName))
        .FirstOrDefault(t => t is not null);
