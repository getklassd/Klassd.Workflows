namespace Klassd.Workflows.Core.Abstractions;

public sealed record JobTypeInfo(string TypeName, string DisplayName);

/// <summary>Discovers IJob implementations loaded in the host, for the UI's "Trigger" buttons.</summary>
public interface IJobCatalog
{
    IReadOnlyList<JobTypeInfo> Jobs { get; }
}
