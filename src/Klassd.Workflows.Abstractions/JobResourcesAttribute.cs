namespace Klassd.Workflows.Abstractions;

/// <summary>
/// Declares default pod resource requests/limits for a job, right next to the
/// job class. Values are Kubernetes quantity strings (CPU "250m"/"1", memory
/// "128Mi"/"1Gi"). appsettings overrides take precedence per field, so this is
/// the baseline a job author ships; ops can tune it without recompiling.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class JobResourcesAttribute : Attribute
{
    public string? CpuRequest { get; set; }
    public string? CpuLimit { get; set; }
    public string? MemoryRequest { get; set; }
    public string? MemoryLimit { get; set; }

    /// <summary>
    /// Run this job with no requests or limits at all. Overrides the global
    /// default. Use the per-field "none" sentinel instead to drop only some.
    /// </summary>
    public bool Unconstrained { get; set; }

    public JobResourceRequirements ToRequirements() => new()
    {
        CpuRequest = CpuRequest,
        CpuLimit = CpuLimit,
        MemoryRequest = MemoryRequest,
        MemoryLimit = MemoryLimit,
        Unconstrained = Unconstrained,
    };
}
