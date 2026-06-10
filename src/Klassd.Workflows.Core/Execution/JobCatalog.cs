using Klassd.Workflows.Abstractions;
using Klassd.Workflows.Core.Abstractions;

namespace Klassd.Workflows.Core.Execution;

/// <summary>Scans loaded assemblies for concrete IJob types.</summary>
public sealed class JobCatalog : IJobCatalog
{
    public IReadOnlyList<JobTypeInfo> Jobs { get; }

    public JobCatalog()
    {
        var jobType = typeof(IJob);
        Jobs = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(SafeGetTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false } && jobType.IsAssignableFrom(t))
            .Select(t => new JobTypeInfo(t.FullName!, t.Name))
            .OrderBy(j => j.DisplayName)
            .ToList();
    }

    private static IEnumerable<Type> SafeGetTypes(System.Reflection.Assembly a)
    {
        try { return a.GetTypes(); }
        catch (System.Reflection.ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }
}
