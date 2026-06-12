namespace Klassd.Workflows.Core.Model;

/// <summary>
/// A pod toleration (Kubernetes <c>tolerations</c>) — lets the pod schedule onto nodes carrying a
/// matching taint. With <c>Operator = "Exists"</c> leave <see cref="Value"/> null.
/// </summary>
public sealed class TolerationSpec
{
    public string? Key { get; init; }

    /// <summary><c>"Equal"</c> (match key+value) or <c>"Exists"</c> (match key only).</summary>
    public string? Operator { get; init; }

    public string? Value { get; init; }

    /// <summary><c>"NoSchedule"</c> | <c>"PreferNoSchedule"</c> | <c>"NoExecute"</c>; null matches all effects.</summary>
    public string? Effect { get; init; }

    /// <summary>For a <c>NoExecute</c> taint, how long the pod stays bound after the taint is added.</summary>
    public long? TolerationSeconds { get; init; }
}

/// <summary>
/// Pod affinity (Kubernetes <c>affinity</c>): which nodes the pod prefers/requires, and co-location
/// rules relative to other pods. All parts are optional. Node affinity uses match expressions; pod
/// (anti-)affinity uses label matching plus a topology key.
/// </summary>
public sealed class AffinitySpec
{
    public NodeAffinitySpec? NodeAffinity { get; init; }
    public PodAffinitySpec? PodAffinity { get; init; }
    public PodAffinitySpec? PodAntiAffinity { get; init; }
}

/// <summary>Constrains which nodes a pod can/should run on, by node label expressions.</summary>
public sealed class NodeAffinitySpec
{
    /// <summary>Hard requirement (<c>requiredDuringSchedulingIgnoredDuringExecution</c>): the terms are OR-ed.</summary>
    public IReadOnlyList<NodeSelectorTermSpec> Required { get; init; } = Array.Empty<NodeSelectorTermSpec>();

    /// <summary>Soft preference (<c>preferredDuringSchedulingIgnoredDuringExecution</c>).</summary>
    public IReadOnlyList<PreferredNodeTermSpec> Preferred { get; init; } = Array.Empty<PreferredNodeTermSpec>();
}

/// <summary>A set of node label requirements that must all match (AND).</summary>
public sealed class NodeSelectorTermSpec
{
    public IReadOnlyList<NodeSelectorRequirementSpec> MatchExpressions { get; init; } = Array.Empty<NodeSelectorRequirementSpec>();
}

/// <summary>One node label requirement: <c>Key Operator Values</c> (e.g. <c>"pool" In ["batch"]</c>).</summary>
public sealed class NodeSelectorRequirementSpec
{
    public string Key { get; init; } = "";

    /// <summary><c>"In"</c> | <c>"NotIn"</c> | <c>"Exists"</c> | <c>"DoesNotExist"</c> | <c>"Gt"</c> | <c>"Lt"</c>.</summary>
    public string Operator { get; init; } = "In";

    public IReadOnlyList<string> Values { get; init; } = Array.Empty<string>();
}

/// <summary>A weighted preferred node term (1–100 weight).</summary>
public sealed class PreferredNodeTermSpec
{
    public int Weight { get; init; } = 1;
    public NodeSelectorTermSpec Preference { get; init; } = new();
}

/// <summary>Pod (anti-)affinity rules, matched against other pods' labels within a topology domain.</summary>
public sealed class PodAffinitySpec
{
    public IReadOnlyList<PodAffinityTermSpec> Required { get; init; } = Array.Empty<PodAffinityTermSpec>();
    public IReadOnlyList<WeightedPodAffinityTermSpec> Preferred { get; init; } = Array.Empty<WeightedPodAffinityTermSpec>();
}

/// <summary>Selects peer pods by label within a topology (e.g. spread across <c>kubernetes.io/hostname</c>).</summary>
public sealed class PodAffinityTermSpec
{
    public IReadOnlyDictionary<string, string> MatchLabels { get; init; } = new Dictionary<string, string>();

    /// <summary>The node label defining the topology domain (default <c>kubernetes.io/hostname</c>).</summary>
    public string TopologyKey { get; init; } = "kubernetes.io/hostname";

    /// <summary>Namespaces to match peer pods in; null = the pod's own namespace.</summary>
    public IReadOnlyList<string>? Namespaces { get; init; }
}

/// <summary>A weighted preferred pod-affinity term (1–100 weight).</summary>
public sealed class WeightedPodAffinityTermSpec
{
    public int Weight { get; init; } = 1;
    public PodAffinityTermSpec Term { get; init; } = new();
}
