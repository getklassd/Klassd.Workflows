using Klassd.Workflows.Abstractions;

namespace Klassd.Workflows.Core.Storage;

/// <summary>
/// Artifact store backed by a directory. Works for the local executor and for
/// Kubernetes when the directory is a shared ReadWriteMany volume mounted at the
/// same path in every worker pod. For production object storage (GCS/S3),
/// implement <see cref="IArtifactStore"/> separately. The reference is the file
/// name, resolved against the configured root — so it is portable across pods
/// that mount the same volume.
/// </summary>
public sealed class FileSystemArtifactStore : IArtifactStore
{
    private readonly string _root;

    public FileSystemArtifactStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    public async Task<string> SaveAsync(string key, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var safeKey = string.Concat(key.Split(Path.GetInvalidFileNameChars()));
        var name = $"{Guid.NewGuid():n}-{safeKey}";
        await File.WriteAllBytesAsync(Path.Combine(_root, name), data.ToArray(), ct);
        return name;
    }

    public async Task<byte[]> LoadAsync(string reference, CancellationToken ct = default)
    {
        var path = Path.Combine(_root, Path.GetFileName(reference)); // guard against traversal
        return await File.ReadAllBytesAsync(path, ct);
    }
}
