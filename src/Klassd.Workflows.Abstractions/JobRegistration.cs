using Microsoft.Extensions.DependencyInjection;

namespace Klassd.Workflows.Abstractions;

/// <summary>
/// A single job a worker can run: the dispatch <see cref="Key"/> the scheduler sends (in
/// <c>KLASSD_JOB_TYPE</c>), the optional CLR <see cref="JobType"/> — used for catalog metadata
/// (display name and <see cref="JobInputAttribute"/> inputs) when the job is registered by type —
/// and the <see cref="Factory"/> that constructs it from the worker's service provider.
/// </summary>
public sealed record JobRegistration(string Key, Type? JobType, Func<IServiceProvider, IJob> Factory);

/// <summary>
/// The set of jobs a worker can run, keyed by the dispatch key the scheduler sends. Built from a
/// <see cref="JobRegistrationBuilder"/> and shared between the worker (to construct the requested
/// job) and the scheduler host (to populate the job catalog), so both sides agree on the key set.
/// </summary>
public interface IJobRegistry
{
    IReadOnlyCollection<JobRegistration> Registrations { get; }

    bool TryGet(string key, out JobRegistration registration);
}

/// <summary>Dictionary-backed <see cref="IJobRegistry"/>; build one with <see cref="Build"/>.</summary>
public sealed class JobRegistry : IJobRegistry
{
    private readonly Dictionary<string, JobRegistration> _byKey;

    public JobRegistry(IEnumerable<JobRegistration> registrations)
    {
        _byKey = registrations.ToDictionary(r => r.Key, StringComparer.Ordinal);
        Registrations = _byKey.Values.ToList();
    }

    /// <summary>An empty registry — the worker/scheduler default until jobs are registered.</summary>
    public static IJobRegistry Empty { get; } = new JobRegistry([]);

    public IReadOnlyCollection<JobRegistration> Registrations { get; }

    public bool TryGet(string key, out JobRegistration registration) =>
        _byKey.TryGetValue(key, out registration!);

    /// <summary>Build a registry from a registration callback.</summary>
    public static IJobRegistry Build(Action<JobRegistrationBuilder> configure)
    {
        var builder = new JobRegistrationBuilder();
        configure(builder);
        return new JobRegistry(builder.Build());
    }
}

/// <summary>
/// Fluent registration of the jobs a worker can run, each mapped to a dispatch key (the value the
/// scheduler sends). <see cref="Add{TJob}(string?)"/> defaults the key to the job's full type name —
/// matching what the scheduler emits for <c>EnqueueAsync&lt;T&gt;</c>, recurring jobs and workflow
/// nodes — or you can give an explicit key. Jobs are constructed with <see cref="ActivatorUtilities"/>
/// (so they can take constructor dependencies registered via the worker's <c>ConfigureServices</c>)
/// or with an explicit factory.
/// </summary>
public sealed class JobRegistrationBuilder
{
    private readonly Dictionary<string, JobRegistration> _registrations = new(StringComparer.Ordinal);

    /// <summary>Register <typeparamref name="TJob"/> under <paramref name="key"/> (default: its full type name).</summary>
    public JobRegistrationBuilder Add<TJob>(string? key = null) where TJob : IJob
    {
        var k = key ?? typeof(TJob).FullName!;
        _registrations[k] = new JobRegistration(k, typeof(TJob),
            sp => ActivatorUtilities.CreateInstance<TJob>(sp));
        return this;
    }

    /// <summary>Register <typeparamref name="TJob"/> under <paramref name="key"/> with an explicit factory.</summary>
    public JobRegistrationBuilder Add<TJob>(string key, Func<IServiceProvider, TJob> factory) where TJob : IJob
    {
        _registrations[key] = new JobRegistration(key, typeof(TJob), sp => factory(sp));
        return this;
    }

    /// <summary>Register a job under <paramref name="key"/> with a factory (no type metadata for the catalog).</summary>
    public JobRegistrationBuilder Add(string key, Func<IServiceProvider, IJob> factory)
    {
        _registrations[key] = new JobRegistration(key, null, factory);
        return this;
    }

    internal IReadOnlyCollection<JobRegistration> Build() => _registrations.Values.ToList();
}
