using Klassd.Workflows.Abstractions;
using Klassd.Workflows.Core.Execution;
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
}
