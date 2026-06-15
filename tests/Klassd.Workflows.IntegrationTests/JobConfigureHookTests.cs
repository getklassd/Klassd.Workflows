using Klassd.Workflows.Abstractions;
using Klassd.Workflows.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Klassd.Workflows.IntegrationTests;

/// <summary>
/// Unit coverage for the convention-based per-job <c>Configure(IServiceCollection, IConfiguration)</c>
/// hook the worker invokes on every execution (see <see cref="WorkerHost"/>).
/// </summary>
public class JobConfigureHookTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static (IServiceProvider provider, string log) Invoke(Type jobType, IConfiguration config)
    {
        var services = new ServiceCollection();
        var stdout = new StringWriter();
        WorkerHost.InvokeJobConfigureHook(jobType, services, config, stdout);
        return (services.BuildServiceProvider(), stdout.ToString());
    }

    [Test]
    public async Task Static_hook_registers_services()
    {
        var (provider, log) = Invoke(typeof(StaticConfigureJob), Config(("Marker", "from-static")));

        await Assert.That(provider.GetService<Marker>()?.Value).IsEqualTo("from-static");
        await Assert.That(log).IsEmpty();
    }

    [Test]
    public async Task Instance_hook_registers_services()
    {
        var (provider, log) = Invoke(typeof(InstanceConfigureJob), Config(("Marker", "from-instance")));

        await Assert.That(provider.GetService<Marker>()?.Value).IsEqualTo("from-instance");
        await Assert.That(log).IsEmpty();
    }

    [Test]
    public async Task Instance_hook_without_parameterless_ctor_is_skipped_with_warning()
    {
        var (provider, log) = Invoke(typeof(InstanceConfigureNoCtorJob), Config(("Marker", "ignored")));

        await Assert.That(provider.GetService<Marker>()).IsNull();
        await Assert.That(log).Contains("no parameterless constructor");
    }

    [Test]
    public async Task No_hook_is_a_no_op()
    {
        var (provider, log) = Invoke(typeof(PlainJob), Config());

        await Assert.That(provider.GetService<Marker>()).IsNull();
        await Assert.That(log).IsEmpty();
    }

    // --- test job types -------------------------------------------------------------------------

    public sealed class Marker
    {
        public required string Value { get; init; }
    }

    public sealed class StaticConfigureJob : IJob
    {
        public static void Configure(IServiceCollection services, IConfiguration configuration) =>
            services.AddSingleton(new Marker { Value = configuration["Marker"] ?? "" });

        public Task RunAsync(IJobContext context) => Task.CompletedTask;
    }

    public sealed class InstanceConfigureJob : IJob
    {
        public void Configure(IServiceCollection services, IConfiguration configuration) =>
            services.AddSingleton(new Marker { Value = configuration["Marker"] ?? "" });

        public Task RunAsync(IJobContext context) => Task.CompletedTask;
    }

    public sealed class InstanceConfigureNoCtorJob : IJob
    {
        // No parameterless ctor → the worker cannot create an instance to call the hook.
        public InstanceConfigureNoCtorJob(Marker _) { }

        public void Configure(IServiceCollection services, IConfiguration configuration) =>
            services.AddSingleton(new Marker { Value = "should-not-register" });

        public Task RunAsync(IJobContext context) => Task.CompletedTask;
    }

    public sealed class PlainJob : IJob
    {
        public Task RunAsync(IJobContext context) => Task.CompletedTask;
    }
}
