using System.Reflection;
using Klassd.Workflows.Abstractions;
using k8s.Models;

namespace Klassd.Workflows.Kubernetes;

/// <summary>
/// Resolves the final pod resources for a job by layering, lowest to highest
/// precedence: options.DefaultResources → [JobResources] attribute → per-job
/// appsettings override. Merge is per-field, so config can override just the
/// memory limit while leaving the attribute's CPU values intact.
/// </summary>
internal static class JobResourceResolver
{
    public static V1ResourceRequirements? Resolve(string jobTypeName, KubernetesExecutorOptions options)
    {
        var merged = new JobResourceRequirements();

        if (options.DefaultResources is not null)
            merged = merged.OverlayWith(options.DefaultResources);

        var attr = FindType(jobTypeName)?.GetCustomAttribute<JobResourcesAttribute>();
        if (attr is not null)
            merged = merged.OverlayWith(attr.ToRequirements());

        var configOverride = options.Resources.GetValueOrDefault(jobTypeName)
            ?? options.Resources.GetValueOrDefault(ShortName(jobTypeName));
        if (configOverride is not null)
            merged = merged.OverlayWith(configOverride);

        return ToK8s(merged);
    }

    private static V1ResourceRequirements? ToK8s(JobResourceRequirements r)
    {
        if (r.IsEmpty) return null;

        var requests = new Dictionary<string, ResourceQuantity>();
        var limits = new Dictionary<string, ResourceQuantity>();

        if (r.CpuRequest is not null) requests["cpu"] = new ResourceQuantity(r.CpuRequest);
        if (r.MemoryRequest is not null) requests["memory"] = new ResourceQuantity(r.MemoryRequest);
        if (r.CpuLimit is not null) limits["cpu"] = new ResourceQuantity(r.CpuLimit);
        if (r.MemoryLimit is not null) limits["memory"] = new ResourceQuantity(r.MemoryLimit);

        return new V1ResourceRequirements
        {
            Requests = requests.Count > 0 ? requests : null,
            Limits = limits.Count > 0 ? limits : null,
        };
    }

    private static string ShortName(string typeName) =>
        typeName.Contains('.') ? typeName[(typeName.LastIndexOf('.') + 1)..] : typeName;

    private static Type? FindType(string typeName) =>
        Type.GetType(typeName) ??
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(typeName))
            .FirstOrDefault(t => t is not null);
}
