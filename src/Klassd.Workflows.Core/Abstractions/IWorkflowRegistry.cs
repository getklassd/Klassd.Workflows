using Klassd.Workflows.Core.Model;

namespace Klassd.Workflows.Core.Abstractions;

/// <summary>Holds the workflow DAGs registered in code at startup.</summary>
public interface IWorkflowRegistry
{
    void Register(WorkflowDefinition definition);
    WorkflowDefinition? Get(string name);
    IReadOnlyList<WorkflowDefinition> All { get; }
}
