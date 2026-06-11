using Klassd.Workflows.Abstractions;

namespace Klassd.Workflows.Worker;

/// <summary>
/// IJobContext used inside the worker. Log/progress calls are written to stdout
/// using <see cref="WorkerProtocol"/> so the scheduler can tail them.
/// </summary>
internal sealed class JobContext(
    string jobId,
    string jobName,
    IReadOnlyDictionary<string, string> args,
    CancellationToken cancellationToken,
    TextWriter @out,
    IArtifactStore artifacts)
    : IJobContext
{
    public string JobId { get; } = jobId;
    public string JobName { get; } = jobName;
    public IReadOnlyDictionary<string, string> Arguments { get; } = args;
    public CancellationToken CancellationToken { get; } = cancellationToken;
    public IArtifactStore Artifacts { get; } = artifacts;

    public void Log(string message) =>
        @out.WriteLine($"{WorkerProtocol.LogPrefix} {message}");

    public void ReportProgress(int percent, string? message = null) =>
        @out.WriteLine($"{WorkerProtocol.ProgressPrefix} {Math.Clamp(percent, 0, 100)} {message}".TrimEnd());

    // One bar per loop. The bound is re-evaluated once per iteration, so the calls seen before
    // the current one == iterations already completed; current reaches total exactly as the loop
    // exits. Once a bar fills it's "done", and the next WithProgress call opens a fresh bar (so
    // multiple loops in one job each get their own inline bar).
    private int _barId = -1;
    private int _barTotal;
    private int _barCalls;
    private bool _barDone = true;

    public int WithProgress(int total)
    {
        if (total <= 0) return total;

        if (_barDone)
        {
            _barId++;
            _barTotal = total;
            _barCalls = 0;
            _barDone = false;
        }

        var current = Math.Min(_barCalls, _barTotal);
        @out.WriteLine($"{WorkerProtocol.ProgressBarPrefix} {_barId} {current} {_barTotal}");
        _barCalls++;
        if (current >= _barTotal) _barDone = true;
        return total;
    }

    public void SetOutput(string key, string value) =>
        // value may contain spaces / JSON; only the first space delimits the key.
        @out.WriteLine($"{WorkerProtocol.OutputPrefix} {key} {value}");
}
