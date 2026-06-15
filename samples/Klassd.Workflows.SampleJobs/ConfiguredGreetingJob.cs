using Klassd.Workflows.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Klassd.Workflows.SampleJobs;

/// <summary>
/// Demonstrates the per-execution <c>Configure(IServiceCollection, IConfiguration)</c> hook: the
/// worker calls it before constructing the job (on every run, workflow nodes included), so a job can
/// register its own dependencies from configuration without a global <c>IWorkerStartup</c>.
/// </summary>
/// <remarks>
/// Set the greeting via configuration (appsettings, <c>/secrets/*.json</c>, or an env var), e.g.
/// <c>Greeting__Salutation=Hej</c>. The hook registers <see cref="GreetingOptions"/> from config and
/// the job receives it through its constructor — resolved with <see cref="ActivatorUtilities"/>.
/// </remarks>
public sealed class ConfiguredGreetingJob : IJob
{
    private readonly GreetingOptions _options;

    // Parameterless ctor is required so the worker can instantiate the type to invoke the instance
    // Configure hook; ActivatorUtilities then re-creates it with the registered dependency.
    public ConfiguredGreetingJob() => _options = new GreetingOptions();

    public ConfiguredGreetingJob(GreetingOptions options) => _options = options;

    // Convention hook — discovered by name/signature, no interface to implement.
    public void Configure(IServiceCollection services, IConfiguration configuration)
    {
        var salutation = configuration["Greeting:Salutation"] ?? "Hello";
        services.AddSingleton(new GreetingOptions { Salutation = salutation });
    }

    public Task RunAsync(IJobContext context)
    {
        var name = context.Arguments.GetValueOrDefault("name", "world");
        context.Log($"{_options.Salutation}, {name}!");
        return Task.CompletedTask;
    }
}

/// <summary>Greeting configuration registered by <see cref="ConfiguredGreetingJob.Configure"/>.</summary>
public sealed class GreetingOptions
{
    public string Salutation { get; init; } = "Hello";
}
