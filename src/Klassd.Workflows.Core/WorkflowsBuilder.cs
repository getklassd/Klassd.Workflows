using Klassd.Workflows.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Klassd.Workflows.Core;

/// <summary>
/// Returned by <c>AddKlassdWorkflowsCore</c>. The extension point for swapping
/// the durable backing store: call <see cref="UseJobStore{T}"/> directly, or use
/// an adapter package's convenience method (e.g. <c>UsePostgres</c>,
/// <c>UseMongo</c>) which is just an extension method on this type.
/// </summary>
public sealed class WorkflowsBuilder
{
    public IServiceCollection Services { get; }

    public WorkflowsBuilder(IServiceCollection services) => Services = services;

    /// <summary>Replace the job/workflow store with a custom <see cref="IJobStore"/>.</summary>
    public WorkflowsBuilder UseJobStore<T>() where T : class, IJobStore
    {
        Services.Replace(ServiceDescriptor.Singleton<IJobStore, T>());
        return this;
    }

    /// <summary>Replace the store using a factory (for stores needing connection strings, etc.).</summary>
    public WorkflowsBuilder UseJobStore(Func<IServiceProvider, IJobStore> factory)
    {
        Services.Replace(ServiceDescriptor.Singleton(factory));
        return this;
    }
}
