using Klassd.Workflows.Abstractions;
using Klassd.Workflows.Core.Abstractions;
using Klassd.Workflows.Core.Execution;
using Klassd.Workflows.Core.Storage;
using Klassd.Workflows.Core.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Klassd.Workflows.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the scheduler core: in-memory store (override via the returned
    /// <see cref="WorkflowsBuilder"/>), scheduler, job catalog and the recurring
    /// background loop. Register an <see cref="IJobExecutor"/> separately (e.g.
    /// AddLocalExecutor or AddKubernetesExecutor).
    /// </summary>
    public static WorkflowsBuilder AddKlassdWorkflowsCore(this IServiceCollection services)
    {
        // Default store; swap with builder.UseJobStore<T>() / UsePostgres() / UseMongo().
        services.TryAddSingleton<IJobStore, InMemoryJobStore>();
        // Default empty job registry; populate it with builder.AddJobs(...). The catalog reads from it.
        services.TryAddSingleton(JobRegistry.Empty);
        services.AddSingleton<IJobCatalog, JobCatalog>();
        services.AddSingleton<IContainerJobRegistry, ContainerJobRegistry>();
        services.AddSingleton<IJobScheduler, JobScheduler>();
        services.AddHostedService<RecurringScheduler>();

        // Workflow DAG support.
        services.AddSingleton<IWorkflowRegistry, WorkflowRegistry>();
        services.AddSingleton<WorkflowOrchestrator>();
        services.AddSingleton<IWorkflowOrchestrator>(sp => sp.GetRequiredService<WorkflowOrchestrator>());
        services.AddHostedService(sp => sp.GetRequiredService<WorkflowOrchestrator>());
        return new WorkflowsBuilder(services);
    }

    /// <summary>Run jobs as local processes (dev). Point it at the built Worker.dll.</summary>
    public static IServiceCollection AddLocalExecutor(this IServiceCollection services, string workerDllPath)
    {
        services.Configure<LocalExecutorOptions>(o => o.WorkerDllPath = workerDllPath);
        services.AddSingleton<IJobExecutor, LocalProcessJobExecutor>();
        return services;
    }
}
