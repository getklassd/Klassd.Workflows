namespace Klassd.Workflows.Abstractions;

/// <summary>
/// Wire protocol between the worker process/pod and the scheduler. The worker
/// writes prefixed lines to stdout; the executor tails that stream (pod logs or
/// captured process output) and translates the lines into store updates. Using
/// stdout means the exact same worker works locally and inside Kubernetes.
/// </summary>
public static class WorkerProtocol
{
    public const string LogPrefix = "##LOG##";
    public const string ProgressPrefix = "##PROGRESS##"; // ##PROGRESS## <percent> <message>
    public const string ProgressBarPrefix = "##PROGRESSBAR##"; // ##PROGRESSBAR## <barId> <current> <total>

    /// <summary>
    /// Sentinel stored in a job's log buffer to mark an inline progress bar's position.
    /// Format: <c>##BAR## &lt;barId&gt; &lt;current&gt; &lt;total&gt;</c>. The executor updates the
    /// entry with this id in place (rather than appending) so the bar advances where it sits in
    /// the console; the dashboard renders these lines as a progress bar.
    /// </summary>
    public const string ConsoleBarMarker = "##BAR##";
    public const string StatePrefix = "##STATE##";       // ##STATE## <Succeeded|Failed> <message>
    public const string OutputPrefix = "##OUTPUT##";     // ##OUTPUT## <key> <value>

    // Environment variables the executor sets on the worker container/process.
    public const string EnvJobId = "KLASSD_JOB_ID";
    public const string EnvJobName = "KLASSD_JOB_NAME";
    public const string EnvJobType = "KLASSD_JOB_TYPE";   // assembly-qualified-ish full type name
    public const string EnvJobArgs = "KLASSD_JOB_ARGS";   // JSON object of string->string
    public const string EnvArtifactDir = "KLASSD_ARTIFACT_DIR";           // file provider directory (back-compat)
    public const string EnvArtifactProvider = "KLASSD_ARTIFACT_PROVIDER"; // provider name: file | gcs | s3 | custom
    public const string EnvArtifactSettings = "KLASSD_ARTIFACT_SETTINGS"; // JSON object of provider settings
}
