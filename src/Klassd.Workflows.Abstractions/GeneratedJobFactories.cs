namespace Klassd.Workflows.Abstractions;

/// <summary>
/// The reflection-free factory table that backs <c>JobRegistrationBuilder.Add&lt;TJob&gt;()</c>. The
/// Klassd.Workflows source generator (shipped as an analyzer with this package) inspects each job's
/// constructor at compile time and emits a <c>[ModuleInitializer]</c> that <see cref="Register"/>s a
/// <c>sp =&gt; new TJob(sp.GetRequiredService&lt;…&gt;())</c> factory here — so a job is constructed by
/// generated code, never by <c>ActivatorUtilities</c> reflection. The module initializer runs when the
/// job's assembly is loaded (before its registration code executes), so the entry is present by the
/// time <c>Add&lt;TJob&gt;()</c> looks it up.
/// </summary>
public static class GeneratedJobFactories
{
    private static readonly object _gate = new();
    private static readonly Dictionary<Type, Func<IServiceProvider, IJob>> _factories = new();

    /// <summary>Called by generated module initializers — one entry per concrete <see cref="IJob"/>.</summary>
    public static void Register(Type jobType, Func<IServiceProvider, IJob> factory)
    {
        lock (_gate) _factories[jobType] = factory;
    }

    /// <summary>The generated factory for <paramref name="jobType"/>, or null if none was emitted.</summary>
    public static Func<IServiceProvider, IJob>? Find(Type jobType)
    {
        lock (_gate) return _factories.TryGetValue(jobType, out var factory) ? factory : null;
    }
}
