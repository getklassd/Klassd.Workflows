namespace Klassd.Workflows.Abstractions;

/// <summary>
/// Runtime context handed to a job while it executes inside the executor pod.
/// Lets the job emit log lines and report progress back to the dashboard.
/// </summary>
public interface IJobContext
{
    /// <summary>Unique id of this execution.</summary>
    string JobId { get; }

    /// <summary>Human-friendly job name.</summary>
    string JobName { get; }

    /// <summary>Arguments passed to the job at enqueue time.</summary>
    IReadOnlyDictionary<string, string> Arguments { get; }

    /// <summary>Cancellation token, signalled when the job is stopped from the UI.</summary>
    CancellationToken CancellationToken { get; }

    /// <summary>Out-of-band storage for large payloads passed between nodes.</summary>
    IArtifactStore Artifacts { get; }

    /// <summary>Write a line to the job's live console.</summary>
    void Log(string message);

    /// <summary>Report progress 0-100 with an optional status message.</summary>
    void ReportProgress(int percent, string? message = null);

    /// <summary>
    /// Declares a fixed-size loop and returns <paramref name="total"/> so it can be used as
    /// the loop bound, e.g. <c>for (var i = 0; i &lt; context.WithProgress(items.Count); i++)</c>.
    /// Each evaluation advances a single progress bar pinned in the job's console (updated in
    /// place rather than appending a line per step), reaching <paramref name="total"/> when the
    /// loop exits. Independent of <see cref="ReportProgress"/>, which drives the overall percent.
    /// </summary>
    int WithProgress(int total);

    /// <summary>
    /// Publish a named output. Downstream DAG nodes can read it as an argument,
    /// and a node can fan out over an output whose value is a JSON array
    /// (mirrors Argo's <c>outputs.parameters</c> + <c>withParam</c>).
    /// </summary>
    void SetOutput(string key, string value);
}
