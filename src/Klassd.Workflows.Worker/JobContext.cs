using Klassd.Workflows.Abstractions;

namespace Klassd.Workflows.Worker;

/// <summary>
/// IJobContext used inside the worker. Log/progress calls are written to stdout
/// using <see cref="WorkerProtocol"/> so the scheduler can tail them.
/// </summary>
internal sealed class JobContext : IJobContext
{
    private readonly TextWriter _out;

    public JobContext(string jobId, string jobName, IReadOnlyDictionary<string, string> args,
        CancellationToken cancellationToken, TextWriter @out, IArtifactStore artifacts)
    {
        JobId = jobId;
        JobName = jobName;
        Arguments = args;
        CancellationToken = cancellationToken;
        _out = @out;
        Artifacts = artifacts;
    }

    public string JobId { get; }
    public string JobName { get; }
    public IReadOnlyDictionary<string, string> Arguments { get; }
    public CancellationToken CancellationToken { get; }
    public IArtifactStore Artifacts { get; }

    public void Log(string message) =>
        _out.WriteLine($"{WorkerProtocol.LogPrefix} {message}");

    public void ReportProgress(int percent, string? message = null) =>
        _out.WriteLine($"{WorkerProtocol.ProgressPrefix} {Math.Clamp(percent, 0, 100)} {message}".TrimEnd());

    public void SetOutput(string key, string value) =>
        // value may contain spaces / JSON; only the first space delimits the key.
        _out.WriteLine($"{WorkerProtocol.OutputPrefix} {key} {value}");
}
