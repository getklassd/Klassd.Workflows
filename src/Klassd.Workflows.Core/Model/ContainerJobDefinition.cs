namespace Klassd.Workflows.Core.Model;

/// <summary>
/// A named standalone job backed by an arbitrary container image (not an <c>IJob</c>). Lets existing
/// containers — e.g. legacy Go tools — run as first-class jobs (enqueue, schedule, run from the UI)
/// without porting them to the IJob/worker model. Registered in code via <c>IContainerJobRegistry</c>.
/// </summary>
public sealed class ContainerJobDefinition
{
    public string Name { get; init; } = "";
    public ContainerSpec Container { get; init; } = new();

    /// <summary>Optional one-line description shown in the dashboard catalog.</summary>
    public string? Description { get; init; }
}
