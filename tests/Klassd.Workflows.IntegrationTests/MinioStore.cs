using Amazon.Runtime;
using Amazon.S3;
using Testcontainers.Minio;

namespace Klassd.Workflows.IntegrationTests;

/// <summary>
/// A MinIO container giving the worker pods a real S3-compatible artifact store, so the DAG can
/// pass large payloads between pods (which the pod-local "file" provider can't do). Pods reach it
/// at the container's bridge IP (egress NATs through the K3s node); the bucket is created from the
/// host via the mapped port.
/// </summary>
internal sealed class MinioStore : IAsyncDisposable
{
    public const string Bucket = "artifacts";
    public const string AccessKey = "minioadmin";
    public const string SecretKey = "minioadmin";

    private MinioContainer _minio = null!;

    /// <summary>S3 endpoint reachable from inside the cluster (worker pods).</summary>
    public string InClusterServiceUrl { get; private set; } = "";

    public static async Task<MinioStore> StartAsync(CancellationToken ct = default)
    {
        var store = new MinioStore();
        await store.InitAsync(ct);
        return store;
    }

    private async Task InitAsync(CancellationToken ct)
    {
        _minio = new MinioBuilder("minio/minio:RELEASE.2023-01-31T02-24-19Z")
            .WithUsername(AccessKey)
            .WithPassword(SecretKey)
            .Build();
        await _minio.StartAsync(ct);

        InClusterServiceUrl = $"http://{_minio.IpAddress}:9000";

        // Create the bucket from the host, via MinIO's mapped port.
        var config = new AmazonS3Config
        {
            ServiceURL = _minio.GetConnectionString(),
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        using var s3 = new AmazonS3Client(new BasicAWSCredentials(AccessKey, SecretKey), config);
        await s3.PutBucketAsync(Bucket, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _minio.DisposeAsync();
    }
}
