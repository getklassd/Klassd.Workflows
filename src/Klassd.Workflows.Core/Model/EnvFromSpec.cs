namespace Klassd.Workflows.Core.Model;

/// <summary>Whether an <see cref="EnvFromSpec"/> pulls from a ConfigMap or a Secret.</summary>
public enum EnvFromKind
{
    ConfigMap,
    Secret,
}

/// <summary>
/// Imports every key of a ConfigMap or Secret as environment variables of a container (Kubernetes
/// <c>envFrom</c>). Declare executor-wide (applied to every main container) or per
/// container/init/node. Combined with the container's explicit env (explicit env wins on a clash).
/// </summary>
public sealed class EnvFromSpec
{
    public EnvFromKind Kind { get; init; } = EnvFromKind.ConfigMap;

    /// <summary>The ConfigMap or Secret name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Tolerate the ConfigMap/Secret not existing (Kubernetes <c>optional</c>).</summary>
    public bool Optional { get; init; }

    /// <summary>Optional prefix prepended to every imported variable name.</summary>
    public string? Prefix { get; init; }
}
