using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Klassd.Workflows.Worker;

/// <summary>
/// Optional startup hook for the worker's dependency-injection container. Implement this in (or
/// alongside) your job assembly and the worker discovers it automatically from the assemblies on
/// its load path — no registration call needed. Jobs are then created via the container, so they
/// can take constructor dependencies (e.g. <c>IConfiguration</c>, typed clients, a DB context).
///
/// When no implementation is present the worker falls back to creating jobs with their
/// parameterless constructor, so existing jobs keep working unchanged.
/// </summary>
/// <remarks>
/// Exactly one implementation is expected. The worker instantiates it with its parameterless
/// constructor (before the container exists), then calls <see cref="Configure"/>. The job type
/// itself is resolved with <see cref="ActivatorUtilities"/>, so it does not need to be registered
/// explicitly — only its dependencies do.
/// </remarks>
public interface IWorkerStartup
{
    /// <summary>
    /// Register services for the job to consume. <paramref name="configuration"/> is the worker's
    /// composed configuration (appsettings[.{ENV}].json, then any <c>/secrets/*.json</c>, then
    /// environment variables — last wins).
    /// </summary>
    void Configure(IServiceCollection services, IConfiguration configuration);
}
