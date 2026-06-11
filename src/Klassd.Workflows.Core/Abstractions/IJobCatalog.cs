using Klassd.Workflows.Abstractions;

namespace Klassd.Workflows.Core.Abstractions;

/// <summary>An input a job declares via <see cref="JobInputAttribute"/>, resolved for the UI.</summary>
public sealed record JobInputInfo(
    string Name, string Label, string? Default, bool Required, JobInputType Type, string? Description);

public sealed record JobTypeInfo(string TypeName, string DisplayName, IReadOnlyList<JobInputInfo> Inputs);

/// <summary>Discovers IJob implementations loaded in the host, for the UI's "Run" buttons.</summary>
public interface IJobCatalog
{
    IReadOnlyList<JobTypeInfo> Jobs { get; }
}
