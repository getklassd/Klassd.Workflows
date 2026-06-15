using Klassd.Workflows.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Klassd.Workflows.Worker;

/// <summary>
/// Builds and runs a worker. Reference this package from a thin exe, register the jobs it can run
/// (plus any services they depend on), and call <see cref="RunAsync"/>:
/// <code>
/// return await WorkerHost.CreateBuilder(args)
///     .ConfigureServices((svc, cfg) => svc.AddHttpClient())
///     .RegisterJobs(j =>
///     {
///         j.Add&lt;GreetingJob&gt;();                 // key defaults to the full type name
///         j.Add&lt;EmailJob&gt;("send-email");         // explicit key
///         j.Add("report", sp => new ReportJob(sp.GetRequiredService&lt;IFoo&gt;())); // explicit factory
///     })
///     .RunAsync();
/// </code>
/// Publishing that exe yields your worker image. The scheduler launches it once per job, passing the
/// dispatch key in the environment; the worker constructs the matching registered job and runs it.
/// </summary>
public sealed class WorkerHostBuilder
{
    private readonly string[] _args;
    private readonly List<Action<IServiceCollection, IConfiguration>> _configure = [];
    private readonly List<IArtifactStoreProvider> _artifactProviders = [];
    private IJobRegistry _registry = JobRegistry.Empty;

    internal WorkerHostBuilder(string[] args) => _args = args;

    /// <summary>
    /// Register services jobs can take as constructor dependencies. Invoked once at startup with the
    /// worker's composed configuration (appsettings[.{ENV}].json → <c>/secrets/*.json</c> → env vars).
    /// May be called multiple times; the callbacks run in order.
    /// </summary>
    public WorkerHostBuilder ConfigureServices(Action<IServiceCollection, IConfiguration> configure)
    {
        _configure.Add(configure);
        return this;
    }

    /// <summary>Register the jobs this worker can run, keyed by dispatch key.</summary>
    public WorkerHostBuilder RegisterJobs(Action<JobRegistrationBuilder> register)
    {
        _registry = JobRegistry.Build(register);
        return this;
    }

    /// <summary>
    /// Register an artifact-store backend (e.g. GCS, S3) the worker can select by name at runtime
    /// (driven by config). The built-in "file" provider is always available; register additional
    /// providers for the backends this worker should support.
    /// </summary>
    public WorkerHostBuilder AddArtifactProvider(IArtifactStoreProvider provider)
    {
        _artifactProviders.Add(provider);
        return this;
    }

    /// <summary>
    /// Run the single job the scheduler dispatched (described by the environment) and return a process
    /// exit code (0 success, 1 failure, 2 cancelled).
    /// </summary>
    public Task<int> RunAsync() => WorkerHost.RunAsync(_args, _configure, _registry, _artifactProviders);
}
