using System.Collections.Concurrent;
using Klassd.Workflows.Core.Abstractions;
using Klassd.Workflows.Core.Model;

namespace Klassd.Workflows.Core.Workflows;

public sealed class WorkflowRegistry : IWorkflowRegistry
{
    private readonly ConcurrentDictionary<string, WorkflowDefinition> _defs = new();

    public void Register(WorkflowDefinition definition) => _defs[definition.Name] = definition;

    public WorkflowDefinition? Get(string name) => _defs.GetValueOrDefault(name);

    public IReadOnlyList<WorkflowDefinition> All => _defs.Values.OrderBy(d => d.Name).ToList();
}
