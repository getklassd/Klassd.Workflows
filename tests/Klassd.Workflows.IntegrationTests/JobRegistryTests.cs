using Klassd.Workflows.Abstractions;
using Klassd.Workflows.Core.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Klassd.Workflows.IntegrationTests;

/// <summary>
/// Unit coverage for the job registry: the framework-style replacement for reflection-by-type-name
/// dispatch. Jobs are registered explicitly (<see cref="JobRegistrationBuilder"/>); the worker
/// constructs the requested job from the registry, and the catalog reads its keys.
/// </summary>
public class JobRegistryTests
{
    [Test]
    public async Task Add_defaults_key_to_full_type_name()
    {
        var registry = JobRegistry.Build(j => j.Add<PlainJob>());

        await Assert.That(registry.TryGet(typeof(PlainJob).FullName!, out _)).IsTrue();
    }

    [Test]
    public async Task Add_with_explicit_key_overrides_the_default()
    {
        var registry = JobRegistry.Build(j => j.Add<PlainJob>("plain"));

        await Assert.That(registry.TryGet("plain", out var reg)).IsTrue();
        await Assert.That(reg.JobType).IsEqualTo(typeof(PlainJob));
        await Assert.That(registry.TryGet(typeof(PlainJob).FullName!, out _)).IsFalse();
    }

    [Test]
    public async Task Add_constructs_with_dependency_injection()
    {
        var registry = JobRegistry.Build(j => j.Add<DependentJob>("dep"));
        var provider = new ServiceCollection()
            .AddSingleton(new Marker { Value = "injected" })
            .BuildServiceProvider();

        registry.TryGet("dep", out var reg);
        var job = (DependentJob)reg.Factory(provider);

        await Assert.That(job.Marker.Value).IsEqualTo("injected");
    }

    [Test]
    public async Task Typed_factory_constructs_the_job_and_keeps_type_metadata()
    {
        // A factory returning a concrete IJob binds to Add<TJob>(key, factory): the lambda builds the
        // instance, and the type is still captured for the catalog.
        var registry = JobRegistry.Build(j =>
            j.Add("report", _ => new DependentJob(new Marker { Value = "from-factory" })));
        var provider = new ServiceCollection().BuildServiceProvider();

        registry.TryGet("report", out var reg);
        var job = (DependentJob)reg.Factory(provider);

        await Assert.That(job.Marker.Value).IsEqualTo("from-factory");
        await Assert.That(reg.JobType).IsEqualTo(typeof(DependentJob));
    }

    [Test]
    public async Task Untyped_factory_overload_has_no_type_metadata()
    {
        // Statically typed as Func<IServiceProvider, IJob> → the factory-only overload (no metadata).
        Func<IServiceProvider, IJob> factory = _ => new DependentJob(new Marker { Value = "x" });
        var registry = JobRegistry.Build(j => j.Add("report", factory));

        registry.TryGet("report", out var reg);

        await Assert.That(reg.JobType).IsNull();
    }

    [Test]
    public async Task Static_Configure_registers_the_jobs_own_dependencies()
    {
        // SelfConfiguringJob declares its dependency via the static IJob.Configure — wired by Add<T>()
        // with no call-site callback, no reflection. Mirrors WorkerHost.CreateJob: run the matched
        // registration's ConfigureServices, build the provider, construct the job.
        var registry = JobRegistry.Build(j => j.Add<SelfConfiguringJob>("self"));

        var job = (SelfConfiguringJob)BuildJob(registry, "self");

        await Assert.That(job.Marker.Value).IsEqualTo("from-static-configure");
    }

    [Test]
    public async Task Per_job_configure_registers_only_that_jobs_dependencies()
    {
        // Two registrations of the same job type, each contributing its OWN marker. Only the matched
        // registration's ConfigureServices runs, so 'a' must NOT see 'b's dependency, and vice versa.
        var registry = JobRegistry.Build(j =>
        {
            j.Add<DependentJob>("a", configure: (svc, _) => svc.AddSingleton(new Marker { Value = "a-only" }));
            j.Add<DependentJob>("b", configure: (svc, _) => svc.AddSingleton(new Marker { Value = "b-only" }));
        });

        await Assert.That(((DependentJob)BuildJob(registry, "a")).Marker.Value).IsEqualTo("a-only");
        await Assert.That(((DependentJob)BuildJob(registry, "b")).Marker.Value).IsEqualTo("b-only");
    }

    private static IJob BuildJob(IJobRegistry registry, string key)
    {
        registry.TryGet(key, out var reg);
        var services = new ServiceCollection();
        reg.ConfigureServices?.Invoke(services, new ConfigurationBuilder().Build());
        return reg.Factory(services.BuildServiceProvider());
    }

    [Test]
    public async Task TryGet_returns_false_for_unknown_key()
    {
        var registry = JobRegistry.Build(j => j.Add<PlainJob>("known"));

        await Assert.That(registry.TryGet("missing", out _)).IsFalse();
    }

    [Test]
    public async Task Catalog_exposes_registered_keys_and_display_names()
    {
        var registry = JobRegistry.Build(j => j.Add<PlainJob>("plain").Add<DependentJob>());

        var catalog = new JobCatalog(registry);
        var keys = catalog.Jobs.Select(x => x.TypeName).ToList();

        await Assert.That(keys).Contains("plain");
        await Assert.That(keys).Contains(typeof(DependentJob).FullName!);
        await Assert.That(catalog.Jobs.First(x => x.TypeName == "plain").DisplayName)
            .IsEqualTo(nameof(PlainJob));
    }

    // --- test job types -------------------------------------------------------------------------

    public sealed class Marker
    {
        public required string Value { get; init; }
    }

    public sealed class PlainJob : IJob
    {
        public Task RunAsync(IJobContext context) => Task.CompletedTask;
    }

    public sealed class DependentJob(JobRegistryTests.Marker marker) : IJob
    {
        public Marker Marker { get; } = marker;

        public Task RunAsync(IJobContext context) => Task.CompletedTask;
    }

    public sealed class SelfConfiguringJob(JobRegistryTests.Marker marker) : IJob
    {
        public Marker Marker { get; } = marker;

        public static void Configure(IServiceCollection services, IConfiguration configuration) =>
            services.AddSingleton(new Marker { Value = "from-static-configure" });

        public Task RunAsync(IJobContext context) => Task.CompletedTask;
    }
}
