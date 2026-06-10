namespace Klassd.Workflows.Abstractions;

/// <summary>
/// A named factory for an <see cref="IArtifactStore"/>. The worker discovers all
/// providers in its loaded assemblies and selects one by <see cref="Name"/> at
/// runtime (driven by config), so adding a new storage backend is just shipping
/// a class that implements this interface in an assembly the worker references —
/// no changes to the worker itself.
/// </summary>
public interface IArtifactStoreProvider
{
    /// <summary>Provider key, e.g. "file", "gcs", "s3" (matched case-insensitively).</summary>
    string Name { get; }

    /// <summary>Build the store from provider-specific settings (e.g. bucket, prefix, dir).</summary>
    IArtifactStore Create(IReadOnlyDictionary<string, string> settings);
}
