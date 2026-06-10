using System.Reflection;
using Klassd.Workflows.Abstractions;

namespace Klassd.Workflows.Core.Storage;

/// <summary>
/// Selects an <see cref="IArtifactStoreProvider"/> by name from all providers in
/// the loaded assemblies (same discovery approach as jobs), then builds the
/// store. Used by the worker so artifact backends are pluggable: reference an
/// assembly containing a provider and select it by name — no worker changes.
/// </summary>
public static class ArtifactStoreResolver
{
    public static IReadOnlyList<IArtifactStoreProvider> DiscoverProviders()
    {
        var iface = typeof(IArtifactStoreProvider);
        return AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(SafeGetTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false } && iface.IsAssignableFrom(t))
            .Select(t => (IArtifactStoreProvider)Activator.CreateInstance(t)!)
            .ToList();
    }

    public static IArtifactStore Resolve(string providerName, IReadOnlyDictionary<string, string> settings)
    {
        var provider = DiscoverProviders()
            .FirstOrDefault(p => string.Equals(p.Name, providerName, StringComparison.OrdinalIgnoreCase))
            ?? new FileSystemArtifactStoreProvider();
        return provider.Create(settings);
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }
}
