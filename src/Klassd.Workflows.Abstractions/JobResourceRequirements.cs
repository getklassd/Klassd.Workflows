namespace Klassd.Workflows.Abstractions;

/// <summary>
/// Kubernetes pod resource requests/limits for a job, expressed as Kubernetes
/// quantity strings (e.g. CPU "250m"/"1", memory "128Mi"/"1Gi"). Every field is
/// optional so layers can be merged field-by-field: a global default, then the
/// job's <see cref="JobResourcesAttribute"/>, then appsettings overrides.
///
/// Clearing vs inheriting during a merge:
/// <list type="bullet">
/// <item><c>null</c> (field absent) means "inherit the lower layer's value".</item>
/// <item>the sentinel <c>"none"</c> or <c>""</c> means "explicitly clear this
/// field" — used to drop a limit/request that a lower layer set.</item>
/// <item><see cref="Unconstrained"/> = true clears every field, so the pod runs
/// with no requests or limits at all.</item>
/// </list>
/// </summary>
public sealed class JobResourceRequirements
{
    public string? CpuRequest { get; set; }
    public string? CpuLimit { get; set; }
    public string? MemoryRequest { get; set; }
    public string? MemoryLimit { get; set; }

    /// <summary>When true this layer wipes all requests/limits (no resources block emitted).</summary>
    public bool Unconstrained { get; set; }

    public bool IsEmpty =>
        CpuRequest is null && CpuLimit is null && MemoryRequest is null && MemoryLimit is null;

    /// <summary>
    /// Returns a copy where <paramref name="over"/> takes precedence: a non-null
    /// field replaces, the "none"/"" sentinel clears, and a null field inherits.
    /// If <paramref name="over"/> is <see cref="Unconstrained"/>, the result is empty.
    /// </summary>
    public JobResourceRequirements OverlayWith(JobResourceRequirements? over)
    {
        if (over is null) return this;
        if (over.Unconstrained) return new();
        return new()
        {
            CpuRequest = Pick(CpuRequest, over.CpuRequest),
            CpuLimit = Pick(CpuLimit, over.CpuLimit),
            MemoryRequest = Pick(MemoryRequest, over.MemoryRequest),
            MemoryLimit = Pick(MemoryLimit, over.MemoryLimit),
        };
    }

    private static string? Pick(string? baseline, string? over) =>
        over is null ? baseline : IsClear(over) ? null : over;

    private static bool IsClear(string value) =>
        value.Length == 0 || value.Equals("none", StringComparison.OrdinalIgnoreCase);
}
