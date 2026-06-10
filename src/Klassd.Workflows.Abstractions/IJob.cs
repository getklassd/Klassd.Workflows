namespace Klassd.Workflows.Abstractions;

/// <summary>
/// The single contract every job implements. Implementations live in their own
/// project, referenced by the worker so the executor pod can load and run them.
/// </summary>
public interface IJob
{
    Task RunAsync(IJobContext context);
}
