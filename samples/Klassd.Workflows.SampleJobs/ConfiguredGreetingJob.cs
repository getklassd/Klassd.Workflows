using Klassd.Workflows.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Klassd.Workflows.SampleJobs;

/// <summary>
/// Demonstrates a constructor-injected job that declares its own dependency. The static
/// <see cref="Configure"/> registers <see cref="GreetingOptions"/> — wired automatically by
/// <c>j.Add&lt;ConfiguredGreetingJob&gt;()</c> and run only when this job is dispatched — and the
/// worker constructs the job through that container, so the salutation can come from configuration
/// (appsettings, <c>/secrets/*.json</c>, or an env var like <c>Greeting__Salutation=Hej</c>).
/// </summary>
public sealed class ConfiguredGreetingJob : IJob
{
    private readonly GreetingOptions _options;

    public ConfiguredGreetingJob(GreetingOptions options) => _options = options;

    public static void Configure(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton(new GreetingOptions
        {
            Salutation = configuration["Greeting:Salutation"] ?? "Hello"
        });

    public Task RunAsync(IJobContext context)
    {
        var name = context.Arguments.GetValueOrDefault("name", "world");
        context.Log($"{_options.Salutation}, {name}!");
        return Task.CompletedTask;
    }
}

/// <summary>Greeting configuration the worker registers for <see cref="ConfiguredGreetingJob"/>.</summary>
public sealed class GreetingOptions
{
    public string Salutation { get; init; } = "Hello";
}
