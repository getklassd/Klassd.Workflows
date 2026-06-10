using Klassd.Workflows.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Klassd.Workflows.Kubernetes;

public static class ServiceCollectionExtensions
{
    /// <summary>Run jobs as Kubernetes Jobs (one pod per execution), configured in code.</summary>
    public static IServiceCollection AddKubernetesExecutor(
        this IServiceCollection services, Action<KubernetesExecutorOptions> configure)
    {
        services.Configure(configure);
        services.AddSingleton<IJobExecutor, KubernetesJobExecutor>();
        return services;
    }

    /// <summary>
    /// Run jobs as Kubernetes Jobs, binding options (image, namespace,
    /// DefaultResources, per-job Resources, ...) from a configuration section.
    /// This is the Vault-friendly path: change appsettings / injected config and
    /// pod resources update without a recompile.
    /// </summary>
    public static IServiceCollection AddKubernetesExecutor(
        this IServiceCollection services, IConfiguration section)
    {
        services.Configure<KubernetesExecutorOptions>(section);
        services.AddSingleton<IJobExecutor, KubernetesJobExecutor>();
        return services;
    }
}
