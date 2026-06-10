using Amazon.S3;
using Amazon.S3.Model;
using Klassd.Workflows.Abstractions;

namespace Klassd.Workflows.Artifacts.S3;

/// <summary>
/// <see cref="IArtifactStore"/> backed by an S3 bucket (or any S3-compatible
/// endpoint, e.g. MinIO). The reference is the object key.
/// </summary>
public sealed class S3ArtifactStore : IArtifactStore
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;
    private readonly string _prefix;

    public S3ArtifactStore(IAmazonS3 s3, string bucket, string prefix = "")
    {
        _s3 = s3;
        _bucket = bucket;
        _prefix = prefix;
    }

    public async Task<string> SaveAsync(string key, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var safeKey = string.Concat(key.Split(Path.GetInvalidFileNameChars()));
        var objectKey = $"{_prefix}{Guid.NewGuid():n}-{safeKey}";
        using var ms = new MemoryStream(data.ToArray());
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = objectKey,
            InputStream = ms
        }, ct);
        return objectKey;
    }

    public async Task<byte[]> LoadAsync(string reference, CancellationToken ct = default)
    {
        using var response = await _s3.GetObjectAsync(_bucket, reference, ct);
        using var ms = new MemoryStream();
        await response.ResponseStream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}

/// <summary>
/// Discoverable "s3" provider. Settings: <c>bucket</c> (required), <c>prefix</c>,
/// <c>region</c>, and <c>serviceUrl</c> (for S3-compatible endpoints like MinIO).
/// Credentials come from the standard AWS chain.
/// </summary>
public sealed class S3ArtifactStoreProvider : IArtifactStoreProvider
{
    public string Name => "s3";

    public IArtifactStore Create(IReadOnlyDictionary<string, string> settings)
    {
        var bucket = settings.GetValueOrDefault("bucket")
            ?? throw new InvalidOperationException("s3 artifact provider requires a 'bucket' setting.");
        var prefix = settings.GetValueOrDefault("prefix", "");

        var config = new AmazonS3Config();
        if (settings.TryGetValue("region", out var region) && !string.IsNullOrWhiteSpace(region))
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        if (settings.TryGetValue("serviceUrl", out var url) && !string.IsNullOrWhiteSpace(url))
        {
            config.ServiceURL = url;
            config.ForcePathStyle = true; // required for MinIO and most S3-compatible stores
        }

        return new S3ArtifactStore(new AmazonS3Client(config), bucket, prefix);
    }
}
