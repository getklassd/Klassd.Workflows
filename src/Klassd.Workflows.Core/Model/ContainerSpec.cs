namespace Klassd.Workflows.Core.Model;

/// <summary>
/// Describes an arbitrary container image to run as a DAG node (instead of one of our
/// <c>IJob</c> classes). Used e.g. to run a <c>cloud-sql-proxy</c> sidecar as a long-running
/// service node whose address is forwarded to dependents. The node's resolved arguments
/// (static + bound from upstream outputs) are passed to the container as environment variables.
/// </summary>
public sealed class ContainerSpec
{
    public string Image { get; init; } = "";

    /// <summary>Entrypoint override (container <c>command</c>); empty = use the image's entrypoint.</summary>
    public IReadOnlyList<string> Command { get; init; } = Array.Empty<string>();

    /// <summary>Container <c>args</c>.</summary>
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

    /// <summary>Static environment variables for the container (merged with the node's resolved arguments).</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Port the container serves on. When set, the engine publishes <c>address</c> = <c>&lt;ip&gt;:&lt;port&gt;</c>
    /// (and <c>ip</c>) as node outputs so dependents can connect. Required for a useful service node.
    /// </summary>
    public int? ServicePort { get; init; }

    /// <summary>
    /// If set, the node is considered ready only once this TCP port accepts connections (implemented
    /// as a Kubernetes <c>tcpSocket</c> readiness probe / a TCP poll under the local docker shim).
    /// Defaults to <see cref="ServicePort"/> behaviour of "ready when Running" when null.
    /// </summary>
    public int? ReadyTcpPort { get; init; }

    /// <summary>Container imagePullPolicy ("Always" | "IfNotPresent" | "Never"); null leaves K8s' default.</summary>
    public string? ImagePullPolicy { get; init; }
}
