using Klassd.Workflows.Core.Model;

namespace Klassd.Workflows.Core.Abstractions;

/// <summary>Registry of named container-backed standalone jobs (see <see cref="ContainerJobDefinition"/>).</summary>
public interface IContainerJobRegistry
{
    void Register(ContainerJobDefinition definition);
    ContainerJobDefinition? Get(string name);
    IReadOnlyList<ContainerJobDefinition> All { get; }
}
