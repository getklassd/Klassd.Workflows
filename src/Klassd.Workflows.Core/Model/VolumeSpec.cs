namespace Klassd.Workflows.Core.Model;

/// <summary>The backing source for a <see cref="VolumeSpec"/>.</summary>
public enum VolumeKind
{
    /// <summary>An ephemeral scratch volume shared by the pod's containers (Kubernetes <c>emptyDir</c>).</summary>
    EmptyDir,
    /// <summary>A Kubernetes Secret mounted as files (<see cref="VolumeSpec.Source"/> = secret name).</summary>
    Secret,
    /// <summary>A Kubernetes ConfigMap mounted as files (<see cref="VolumeSpec.Source"/> = config-map name).</summary>
    ConfigMap,
    /// <summary>An existing PersistentVolumeClaim (<see cref="VolumeSpec.Source"/> = claim name).</summary>
    PersistentVolumeClaim,
    /// <summary>A path on the host node (<see cref="VolumeSpec.Source"/> = host path).</summary>
    HostPath,
}

/// <summary>
/// A pod-level volume. Declare it at any level (executor-wide, node, or container) and mount it into
/// the containers that need it with a <see cref="VolumeMountSpec"/>. The canonical use is an
/// <see cref="VolumeKind.EmptyDir"/> that an init container writes and the main container reads.
///
/// Volumes are a Kubernetes-executor concept; the local docker shim ignores them (logged).
/// </summary>
public sealed class VolumeSpec
{
    public string Name { get; init; } = "";

    public VolumeKind Kind { get; init; } = VolumeKind.EmptyDir;

    /// <summary>
    /// The backing name/path, by <see cref="Kind"/>: secret name, config-map name, PVC claim name,
    /// or host path. Unused for <see cref="VolumeKind.EmptyDir"/>.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>Optional size limit for an <see cref="VolumeKind.EmptyDir"/>, e.g. <c>"256Mi"</c>.</summary>
    public string? SizeLimit { get; init; }

    /// <summary>Mount the underlying source read-only (PersistentVolumeClaim); ignored for the others.</summary>
    public bool ReadOnly { get; init; }
}

/// <summary>Mounts a <see cref="VolumeSpec"/> (matched by <see cref="Name"/>) into a container.</summary>
public sealed class VolumeMountSpec
{
    /// <summary>Must match a declared <see cref="VolumeSpec.Name"/>.</summary>
    public string Name { get; init; } = "";

    /// <summary>Absolute path inside the container the volume is mounted at.</summary>
    public string MountPath { get; init; } = "";

    /// <summary>Mount only this sub-path of the volume, instead of its root.</summary>
    public string? SubPath { get; init; }

    public bool ReadOnly { get; init; }
}
