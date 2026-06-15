using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Klassd.Workflows.Abstractions;

/// <summary>
/// The single contract every job implements. Implementations live in their own
/// project, referenced by the worker so the executor pod can load and run them.
/// </summary>
public interface IJob
{
    Task RunAsync(IJobContext context);

    /// <summary>
    /// Registers the dependencies this job needs into its own service collection. Called by
    /// <see cref="JobRegistrationBuilder.Add{TJob}(string?,System.Action{IServiceCollection,IConfiguration})"/>
    /// only when this job is the one dispatched — a worker pod runs a single job, so a job that isn't
    /// invoked never registers anything. Override to co-locate a job's wiring with the job; the empty
    /// default means jobs that just take already-registered (or no) dependencies implement nothing.
    /// Cross-cutting services shared by every job belong on the worker-wide <c>ConfigureServices</c>.
    /// </summary>
    static virtual void Configure(IServiceCollection services, IConfiguration configuration) { }
}
