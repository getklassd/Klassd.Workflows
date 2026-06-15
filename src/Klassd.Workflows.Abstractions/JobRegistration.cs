using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Klassd.Workflows.Abstractions;

/// <summary>
/// A single job a worker can run: the dispatch <see cref="Key"/> the scheduler sends (in
/// <c>KLASSD_JOB_TYPE</c>), the optional CLR <see cref="JobType"/> — used for catalog metadata
/// (display name and <see cref="JobInputAttribute"/> inputs) when the job is registered by type —
/// the <see cref="Factory"/> that constructs it from the worker's service provider, and an optional
/// per-job <see cref="ConfigureServices"/> callback that registers only this job's dependencies.
/// Because a worker pod runs a single dispatched job, only the matching job's
/// <see cref="ConfigureServices"/> ever runs — so each job pays only for the services it uses,
/// rather than every job in the image sharing one worker-wide registration.
/// </summary>
public sealed record JobRegistration(
    string Key,
    Type? JobType,
    Func<IServiceProvider, IJob> Factory,
    Action<IServiceCollection, IConfiguration>? ConfigureServices = null);

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
/// scheduler sends). <c>Add&lt;TJob&gt;()</c> defaults the key to the job's full type name —
/// matching what the scheduler emits for <c>EnqueueAsync&lt;T&gt;</c>, recurring jobs and workflow
/// nodes — or you can give an explicit key. Jobs are constructed without reflection: the
/// Klassd.Workflows source generator emits a <c>new TJob(sp.GetRequiredService&lt;…&gt;())</c> factory
/// for every job at compile time (see <see cref="GeneratedJobFactories"/>), and <c>Add&lt;TJob&gt;()</c>
/// uses it — or you can pass an explicit factory. A job declares its own dependencies by overriding the
/// static <see cref="IJob.Configure"/> — picked up automatically here, with no reflection (the generic
/// constraint resolves <c>TJob.Configure</c> at compile time). It runs only when that job is the one
/// dispatched, so registering a job's services never costs the jobs that don't use them. The optional
/// <c>configure</c> argument adds further registrations at the call site (it runs after
/// <see cref="IJob.Configure"/>). Cross-cutting services shared by every job still go on the worker-wide
/// <c>ConfigureServices</c>.
/// </summary>
public sealed class JobRegistrationBuilder
{
    private readonly Dictionary<string, JobRegistration> _registrations = new(StringComparer.Ordinal);

    /// <summary>
    /// Register <typeparamref name="TJob"/> under <paramref name="key"/> (default: its full type name),
    /// using the source-generated constructor factory. The job's static <see cref="IJob.Configure"/> is
    /// wired automatically; the optional <paramref name="configure"/> adds further per-job registrations
    /// after it.
    /// </summary>
    public JobRegistrationBuilder Add<TJob>(
        string? key = null,
        Action<IServiceCollection, IConfiguration>? configure = null) where TJob : IJob
    {
        var k = key ?? typeof(TJob).FullName!;
        _registrations[k] = new JobRegistration(k, typeof(TJob), GeneratedFactory<TJob>(), Combine<TJob>(configure));
        return this;
    }

    /// <summary>
    /// The compile-time-generated <c>new TJob(…)</c> factory, or a throwing stub directing the caller to
    /// reference the generator (or pass an explicit factory) when none was emitted — e.g. the job's
    /// assembly hasn't pulled in the analyzer.
    /// </summary>
    private static Func<IServiceProvider, IJob> GeneratedFactory<TJob>() where TJob : IJob
    {
        var factory = GeneratedJobFactories.Find(typeof(TJob));
        if (factory is not null) return factory;
        return _ => throw new InvalidOperationException(
            $"No generated factory for '{typeof(TJob).FullName}'. The Klassd.Workflows source generator " +
            "(shipped with Klassd.Workflows.Abstractions) should emit one for every IJob; ensure the job's " +
            $"assembly references the analyzer, or register it with an explicit factory: " +
            $"j.Add<{typeof(TJob).Name}>(key, sp => new {typeof(TJob).Name}(...)).");
    }

    /// <summary>
    /// Register <typeparamref name="TJob"/> under <paramref name="key"/> with an explicit factory.
    /// The job's static <see cref="IJob.Configure"/> is wired automatically; the optional
    /// <paramref name="configure"/> adds further per-job registrations after it.
    /// </summary>
    public JobRegistrationBuilder Add<TJob>(
        string key,
        Func<IServiceProvider, TJob> factory,
        Action<IServiceCollection, IConfiguration>? configure = null) where TJob : IJob
    {
        _registrations[key] = new JobRegistration(key, typeof(TJob), sp => factory(sp), Combine<TJob>(configure));
        return this;
    }

    /// <summary>
    /// The job's static <see cref="IJob.Configure"/> (resolved via the generic constraint, no
    /// reflection) followed by the optional call-site <paramref name="configure"/>.
    /// </summary>
    private static Action<IServiceCollection, IConfiguration> Combine<TJob>(
        Action<IServiceCollection, IConfiguration>? configure) where TJob : IJob =>
        (services, configuration) =>
        {
            TJob.Configure(services, configuration);
            configure?.Invoke(services, configuration);
        };

    /// <summary>
    /// Register a job under <paramref name="key"/> with a factory (no type metadata for the catalog),
    /// optionally with a <paramref name="configure"/> callback for this job's own dependencies.
    /// </summary>
    public JobRegistrationBuilder Add(
        string key,
        Func<IServiceProvider, IJob> factory,
        Action<IServiceCollection, IConfiguration>? configure = null)
    {
        _registrations[key] = new JobRegistration(key, null, factory, configure);
        return this;
    }

    internal IReadOnlyCollection<JobRegistration> Build() => _registrations.Values.ToList();
}
