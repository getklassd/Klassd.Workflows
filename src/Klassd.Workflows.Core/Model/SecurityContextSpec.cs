namespace Klassd.Workflows.Core.Model;

/// <summary>
/// Pod-level security context (Kubernetes <c>pod.spec.securityContext</c>) — settings that apply to
/// the whole pod, e.g. the fsGroup that owns mounted volumes. Set executor-wide for a default on every
/// pod, or per node/container to override it. A null field is left unset (Kubernetes default).
/// </summary>
public sealed class PodSecurityContextSpec
{
    public long? RunAsUser { get; init; }
    public long? RunAsGroup { get; init; }
    public bool? RunAsNonRoot { get; init; }

    /// <summary>Supplemental group that owns mounted volumes and new files (Kubernetes <c>fsGroup</c>).</summary>
    public long? FsGroup { get; init; }

    /// <summary>Additional groups added to the first process in each container.</summary>
    public IReadOnlyList<long> SupplementalGroups { get; init; } = Array.Empty<long>();

    /// <summary>Seccomp profile type, e.g. <c>"RuntimeDefault"</c> or <c>"Unconfined"</c>; null leaves it unset.</summary>
    public string? SeccompProfileType { get; init; }
}

/// <summary>
/// Container-level security context (Kubernetes <c>container.securityContext</c>). Applied per
/// container — set executor-wide as the default for every container (main + init), or per
/// container/init/node to override it. A null field is left unset (Kubernetes default).
/// </summary>
public sealed class SecurityContextSpec
{
    public long? RunAsUser { get; init; }
    public long? RunAsGroup { get; init; }
    public bool? RunAsNonRoot { get; init; }
    public bool? ReadOnlyRootFilesystem { get; init; }
    public bool? AllowPrivilegeEscalation { get; init; }
    public bool? Privileged { get; init; }

    /// <summary>Linux capabilities to add (e.g. <c>"NET_BIND_SERVICE"</c>).</summary>
    public IReadOnlyList<string> AddCapabilities { get; init; } = Array.Empty<string>();

    /// <summary>Linux capabilities to drop (e.g. <c>"ALL"</c>).</summary>
    public IReadOnlyList<string> DropCapabilities { get; init; } = Array.Empty<string>();

    /// <summary>Seccomp profile type, e.g. <c>"RuntimeDefault"</c>; null leaves it unset.</summary>
    public string? SeccompProfileType { get; init; }
}
