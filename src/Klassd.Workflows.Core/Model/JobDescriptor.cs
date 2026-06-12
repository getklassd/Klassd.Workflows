namespace Klassd.Workflows.Core.Model;

/// <summary>Everything needed to start one run of a job.</summary>
public sealed record JobDescriptor(
    string JobName,
    string JobTypeName,
    Dictionary<string, string> Arguments)
{
    /// <summary>When set, run this container image instead of an <c>IJob</c> (worker) type.</summary>
    public ContainerSpec? Container { get; init; }

    /// <summary>Long-running service (daemon) node — readiness, not exit, satisfies dependents.</summary>
    public bool IsService { get; init; }
}
