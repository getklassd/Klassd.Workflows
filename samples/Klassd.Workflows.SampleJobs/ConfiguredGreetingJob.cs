using Klassd.Workflows.Abstractions;

namespace Klassd.Workflows.SampleJobs;

/// <summary>
/// Demonstrates a constructor-injected job. Its dependency (<see cref="GreetingOptions"/>) is
/// registered by the worker's <c>ConfigureServices</c> hook (see the SampleWorker), and the worker
/// constructs the job through that container — so the salutation can come from configuration
/// (appsettings, <c>/secrets/*.json</c>, or an env var like <c>Greeting__Salutation=Hej</c>).
/// </summary>
public sealed class ConfiguredGreetingJob : IJob
{
    private readonly GreetingOptions _options;

    public ConfiguredGreetingJob(GreetingOptions options) => _options = options;

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
