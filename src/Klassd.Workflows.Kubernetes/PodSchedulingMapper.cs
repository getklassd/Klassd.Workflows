using Klassd.Workflows.Core.Model;
using k8s.Models;

namespace Klassd.Workflows.Kubernetes;

/// <summary>Maps the engine's scheduling specs (toleration / affinity) to their Kubernetes equivalents.</summary>
internal static class PodSchedulingMapper
{
    public static List<V1Toleration>? Tolerations(IEnumerable<TolerationSpec> specs)
    {
        var list = specs.Select(t => new V1Toleration
        {
            Key = t.Key,
            OperatorProperty = t.Operator,
            Value = t.Value,
            Effect = t.Effect,
            TolerationSeconds = t.TolerationSeconds,
        }).ToList();
        return list.Count > 0 ? list : null;
    }

    public static V1Affinity? Affinity(AffinitySpec? a) => a is null ? null : new V1Affinity
    {
        NodeAffinity = NodeAffinity(a.NodeAffinity),
        PodAffinity = PodAffinity(a.PodAffinity),
        PodAntiAffinity = PodAntiAffinity(a.PodAntiAffinity),
    };

    private static V1NodeAffinity? NodeAffinity(NodeAffinitySpec? n)
    {
        if (n is null || (n.Required.Count == 0 && n.Preferred.Count == 0)) return null;
        return new V1NodeAffinity
        {
            RequiredDuringSchedulingIgnoredDuringExecution = n.Required.Count > 0
                ? new V1NodeSelector { NodeSelectorTerms = n.Required.Select(NodeTerm).ToList() }
                : null,
            PreferredDuringSchedulingIgnoredDuringExecution = n.Preferred.Count > 0
                ? n.Preferred.Select(p => new V1PreferredSchedulingTerm { Weight = p.Weight, Preference = NodeTerm(p.Preference) }).ToList()
                : null,
        };
    }

    private static V1NodeSelectorTerm NodeTerm(NodeSelectorTermSpec t) => new()
    {
        MatchExpressions = t.MatchExpressions.Select(e => new V1NodeSelectorRequirement
        {
            Key = e.Key,
            OperatorProperty = e.Operator,
            Values = e.Values.Count > 0 ? e.Values.ToList() : null,
        }).ToList(),
    };

    private static V1PodAffinity? PodAffinity(PodAffinitySpec? p)
    {
        if (p is null || (p.Required.Count == 0 && p.Preferred.Count == 0)) return null;
        return new V1PodAffinity
        {
            RequiredDuringSchedulingIgnoredDuringExecution = p.Required.Count > 0 ? p.Required.Select(PodTerm).ToList() : null,
            PreferredDuringSchedulingIgnoredDuringExecution = p.Preferred.Count > 0 ? p.Preferred.Select(WeightedPodTerm).ToList() : null,
        };
    }

    private static V1PodAntiAffinity? PodAntiAffinity(PodAffinitySpec? p)
    {
        if (p is null || (p.Required.Count == 0 && p.Preferred.Count == 0)) return null;
        return new V1PodAntiAffinity
        {
            RequiredDuringSchedulingIgnoredDuringExecution = p.Required.Count > 0 ? p.Required.Select(PodTerm).ToList() : null,
            PreferredDuringSchedulingIgnoredDuringExecution = p.Preferred.Count > 0 ? p.Preferred.Select(WeightedPodTerm).ToList() : null,
        };
    }

    private static V1WeightedPodAffinityTerm WeightedPodTerm(WeightedPodAffinityTermSpec w) =>
        new() { Weight = w.Weight, PodAffinityTerm = PodTerm(w.Term) };

    private static V1PodAffinityTerm PodTerm(PodAffinityTermSpec t) => new()
    {
        LabelSelector = new V1LabelSelector
        {
            MatchLabels = t.MatchLabels.Count > 0 ? new Dictionary<string, string>(t.MatchLabels) : null,
        },
        TopologyKey = t.TopologyKey,
        Namespaces = t.Namespaces?.ToList(),
    };
}
