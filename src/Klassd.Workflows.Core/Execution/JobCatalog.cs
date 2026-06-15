using System.Reflection;
using Klassd.Workflows.Abstractions;
using Klassd.Workflows.Core.Abstractions;

namespace Klassd.Workflows.Core.Execution;

/// <summary>
/// Exposes the registered jobs (see <see cref="IJobRegistry"/>) to the dashboard UI's "Run" buttons.
/// Each registration's key is the dispatch identity; the display name and inputs come from the
/// registered CLR type's <see cref="JobInputAttribute"/>s when the job was registered by type.
/// </summary>
public sealed class JobCatalog : IJobCatalog
{
    public IReadOnlyList<JobTypeInfo> Jobs { get; }

    public JobCatalog(IJobRegistry registry)
    {
        Jobs = registry.Registrations
            .Select(r => new JobTypeInfo(
                r.Key,
                r.JobType?.Name ?? r.Key,
                r.JobType is null ? [] : ReadInputs(r.JobType)))
            .OrderBy(j => j.DisplayName)
            .ToList();
    }

    private static IReadOnlyList<JobInputInfo> ReadInputs(Type t) =>
        t.GetCustomAttributes<JobInputAttribute>(false)
            .Select(a => new JobInputInfo(
                a.Name, a.Label ?? a.Name, a.Default, a.Required, a.Type, a.Description))
            .ToList();
}
