namespace Klassd.Workflows.Core.Model;

/// <summary>
/// A declarative file output (Argo's <c>outputs.parameters[].valueFrom.path</c> + <c>default</c>):
/// after the node runs, the engine reads the file at <see cref="Path"/> and publishes its (trimmed)
/// contents as the node output <see cref="Name"/>; if the file is missing or empty, <see cref="Default"/>
/// is used instead. Dependents bind it like any other output (<c>BindInput</c> / <c>FanOutOver</c>).
///
/// For an <c>IJob</c> node the worker reads the file in-pod; for an arbitrary container node a capture
/// sidecar reads it from a shared volume. The value is a single line (compact JSON, an id, an address…);
/// large payloads should go through the <c>IArtifactStore</c>.
/// </summary>
public sealed class OutputSpec
{
    public string Name { get; init; } = "";

    /// <summary>Absolute path of the file the step writes the value to.</summary>
    public string Path { get; init; } = "";

    /// <summary>Value to publish when the file is missing or empty; null means "publish nothing".</summary>
    public string? Default { get; init; }
}
