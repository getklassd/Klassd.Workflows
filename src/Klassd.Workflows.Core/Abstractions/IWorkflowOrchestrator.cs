namespace Klassd.Workflows.Core.Abstractions;

public interface IWorkflowOrchestrator
{
    /// <summary>Start a run of a registered workflow definition. Returns the run id.</summary>
    Task<string> StartAsync(string definitionName, string? tenant = null);
}
