using System.Collections.Concurrent;
using Klassd.Workflows.Core.Abstractions;
using Klassd.Workflows.Core.Model;

namespace Klassd.Workflows.Core.Execution;

public sealed class ContainerJobRegistry : IContainerJobRegistry
{
    private readonly ConcurrentDictionary<string, ContainerJobDefinition> _defs = new();

    public void Register(ContainerJobDefinition definition) => _defs[definition.Name] = definition;

    public ContainerJobDefinition? Get(string name) => _defs.GetValueOrDefault(name);

    public IReadOnlyList<ContainerJobDefinition> All => _defs.Values.OrderBy(d => d.Name).ToList();
}
