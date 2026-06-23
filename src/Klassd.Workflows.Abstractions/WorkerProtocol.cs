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

    /// <summary>
    /// Emitted by a long-running "service" job once it's up and its outputs (e.g. address) are
    /// published. Marks the execution ready so the DAG can unblock dependents while the job keeps
    /// running (the engine tears it down when the rest of the run finishes). Format: <c>##READY##</c>.
    /// </summary>
    public const string ReadyPrefix = "##READY##";

    // Environment variables the executor sets on the worker container/process.
    public const string EnvJobId = "KLASSD_JOB_ID";
    public const string EnvJobName = "KLASSD_JOB_NAME";
    public const string EnvJobType = "KLASSD_JOB_TYPE";   // assembly-qualified-ish full type name
    public const string EnvJobArgs = "KLASSD_JOB_ARGS";   // JSON object of string->string
    public const string EnvTenant = "KLASSD_TENANT";      // tenant id this run belongs to (empty = none)

    /// <summary>
    /// Configuration key under which the worker surfaces the current tenant, so a job's static
    /// <c>Configure(services, configuration)</c> can register tenant-specific dependencies with
    /// <c>configuration[WorkerProtocol.ConfigTenantKey]</c>. Absent (null) for a non-tenant run.
    /// </summary>
    public const string ConfigTenantKey = "Klassd:Tenant";
    public const string EnvArtifactDir = "KLASSD_ARTIFACT_DIR";           // file provider directory (back-compat)
    public const string EnvArtifactProvider = "KLASSD_ARTIFACT_PROVIDER"; // provider name: file | gcs | s3 | custom
    public const string EnvArtifactSettings = "KLASSD_ARTIFACT_SETTINGS"; // JSON object of provider settings
    public const string EnvPodIp = "KLASSD_POD_IP";       // the pod's own IP (downward API); empty when run locally
    public const string EnvOutputSpecs = "KLASSD_OUTPUT_SPECS"; // JSON array of declared file outputs {Name,Path,Default}

    /// <summary>
    /// Switches the worker into capture-sidecar mode (no job): it shares the outputs volume with an
    /// arbitrary-container node and, on SIGTERM (after the main container exits), reads the declared
    /// files / defaults and emits <see cref="OutputPrefix"/> lines. Value is the same JSON as
    /// <see cref="EnvOutputSpecs"/>.
    /// </summary>
    public const string EnvCaptureOutputs = "KLASSD_CAPTURE_OUTPUTS";
}
