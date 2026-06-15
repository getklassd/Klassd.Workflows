using Klassd.Workflows.Abstractions;

namespace Klassd.Workflows.Core.Storage;

/// <summary>
/// Selects an <see cref="IArtifactStoreProvider"/> by name from the set the worker registered
/// (plus the built-in "file" fallback), then builds the store. Artifact backends are pluggable by
/// registering a provider on the worker (<c>AddArtifactProvider</c>) and selecting it by name via
/// config — no worker changes.
/// </summary>
public static class ArtifactStoreResolver
{
    public static IArtifactStore Resolve(string providerName, IReadOnlyDictionary<string, string> settings,
        IEnumerable<IArtifactStoreProvider> providers)
    {
        var provider = providers
            .FirstOrDefault(p => string.Equals(p.Name, providerName, StringComparison.OrdinalIgnoreCase))
            ?? new FileSystemArtifactStoreProvider();
        return provider.Create(settings);
    }
}
