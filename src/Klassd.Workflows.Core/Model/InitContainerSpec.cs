using Klassd.Workflows.Abstractions;

namespace Klassd.Workflows.Core.Model;

/// <summary>
/// An init container to run, to completion and in order, before a pod's main container starts
/// (Kubernetes <c>initContainers</c>). Used for pre-flight work: a DB migration, waiting on a
/// dependency, or seeding a shared path. If an init container fails the main container never runs
/// and the job fails. The pod's resolved arguments (static + bound from upstream outputs) are passed
/// as environment variables, alongside this spec's <see cref="Env"/>, so an init container can read
/// e.g. a <c>db_host</c> bound from a service node.
///
/// Init containers are a Kubernetes-executor concept; the local docker shim ignores them (logged).
/// </summary>
public sealed class InitContainerSpec
{
    public string Name { get; init; } = "init";

    public string Image { get; init; } = "";

    /// <summary>Entrypoint override (container <c>command</c>); empty = use the image's entrypoint.</summary>
    public IReadOnlyList<string> Command { get; init; } = Array.Empty<string>();

    /// <summary>Container <c>args</c>.</summary>
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

    /// <summary>Static environment variables (merged with the pod's resolved arguments).</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    /// <summary>Container imagePullPolicy ("Always" | "IfNotPresent" | "Never"); null falls back to the executor default.</summary>
    public string? ImagePullPolicy { get; init; }

    /// <summary>Volumes (declared at any level) to mount into this init container — e.g. the shared
    /// <see cref="VolumeKind.EmptyDir"/> it writes for the main container to read.</summary>
    public IReadOnlyList<VolumeMountSpec> VolumeMounts { get; init; } = Array.Empty<VolumeMountSpec>();

    /// <summary>Container security context for this init container; null falls back to the executor-wide default.</summary>
    public SecurityContextSpec? SecurityContext { get; init; }

    /// <summary>CPU/memory requests+limits for this init container; overlaid on the executor's <c>DefaultResources</c>.</summary>
    public JobResourceRequirements? Resources { get; init; }

    /// <summary>ConfigMaps/Secrets imported as environment variables into this init container.</summary>
    public IReadOnlyList<EnvFromSpec> EnvFrom { get; init; } = Array.Empty<EnvFromSpec>();
}
