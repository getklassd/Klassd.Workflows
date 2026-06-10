using Google.Cloud.Storage.V1;
using Klassd.Workflows.Abstractions;

namespace Klassd.Workflows.Artifacts.Gcs;

/// <summary>
/// <see cref="IArtifactStore"/> backed by a Google Cloud Storage bucket. The
/// reference is the object name (prefix included), resolved within the bucket —
/// so any worker pod configured with the same bucket can read it.
/// </summary>
public sealed class GcsArtifactStore : IArtifactStore
{
    private readonly StorageClient _client;
    private readonly string _bucket;
    private readonly string _prefix;

    public GcsArtifactStore(StorageClient client, string bucket, string prefix = "")
    {
        _client = client;
        _bucket = bucket;
        _prefix = prefix;
    }

    public async Task<string> SaveAsync(string key, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var safeKey = string.Concat(key.Split(Path.GetInvalidFileNameChars()));
        var objectName = $"{_prefix}{Guid.NewGuid():n}-{safeKey}";
        using var ms = new MemoryStream(data.ToArray());
        await _client.UploadObjectAsync(_bucket, objectName, contentType: null, ms, cancellationToken: ct);
        return objectName;
    }

    public async Task<byte[]> LoadAsync(string reference, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await _client.DownloadObjectAsync(_bucket, reference, ms, cancellationToken: ct);
        return ms.ToArray();
    }
}

/// <summary>Discoverable "gcs" provider. Settings: <c>bucket</c> (required), <c>prefix</c> (optional).</summary>
public sealed class GcsArtifactStoreProvider : IArtifactStoreProvider
{
    public string Name => "gcs";

    public IArtifactStore Create(IReadOnlyDictionary<string, string> settings)
    {
        var bucket = settings.GetValueOrDefault("bucket")
            ?? throw new InvalidOperationException("gcs artifact provider requires a 'bucket' setting.");
        var prefix = settings.GetValueOrDefault("prefix", "");
        return new GcsArtifactStore(StorageClient.Create(), bucket, prefix);
    }
}
