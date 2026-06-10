using System.Text;

namespace Klassd.Workflows.Abstractions;

/// <summary>
/// Stores large payloads out-of-band so they don't travel through env vars /
/// stdout outputs. A job saves an artifact and publishes the returned reference
/// as a small output; a downstream node loads it by that reference. Mirrors Argo
/// artifacts. The store must be reachable by every worker (a shared volume for
/// the filesystem store, or object storage in production).
/// </summary>
public interface IArtifactStore
{
    /// <summary>Save bytes and return an opaque reference to retrieve them later.</summary>
    Task<string> SaveAsync(string key, ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>Load bytes previously saved, by the reference returned from <see cref="SaveAsync"/>.</summary>
    Task<byte[]> LoadAsync(string reference, CancellationToken ct = default);
}

public static class ArtifactStoreExtensions
{
    public static Task<string> SaveTextAsync(this IArtifactStore store, string key, string text, CancellationToken ct = default) =>
        store.SaveAsync(key, Encoding.UTF8.GetBytes(text), ct);

    public static async Task<string> LoadTextAsync(this IArtifactStore store, string reference, CancellationToken ct = default) =>
        Encoding.UTF8.GetString(await store.LoadAsync(reference, ct));
}
