using Klassd.Workflows.Abstractions;

namespace Klassd.Workflows.Core.Storage;

/// <summary>Built-in "file" artifact provider, backed by <see cref="FileSystemArtifactStore"/>.</summary>
public sealed class FileSystemArtifactStoreProvider : IArtifactStoreProvider
{
    public string Name => "file";

    public IArtifactStore Create(IReadOnlyDictionary<string, string> settings)
    {
        var dir = settings.GetValueOrDefault("dir")
            ?? Path.Combine(Path.GetTempPath(), "klassd-workflows-artifacts");
        return new FileSystemArtifactStore(dir);
    }
}
