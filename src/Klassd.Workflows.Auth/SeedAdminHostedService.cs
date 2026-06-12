using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Klassd.Workflows.Auth;

/// <summary>
/// Seeds the configured admin user on startup when the store has no users yet, so a fresh
/// deployment isn't locked out. No-op when <see cref="KlassdWorkflowsAuthOptions.SeedAdminEmail"/>
/// / <see cref="KlassdWorkflowsAuthOptions.SeedAdminPassword"/> are unset or any user already exists.
/// </summary>
internal sealed class SeedAdminHostedService(
    IServiceProvider services, KlassdWorkflowsAuthOptions options, ILogger<SeedAdminHostedService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.SeedAdminEmail) || string.IsNullOrWhiteSpace(options.SeedAdminPassword))
            return;

        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<WorkflowsUserService>();
        try
        {
            if ((await users.GetAllAsync(cancellationToken)).Count > 0) return;
            await users.SeedAsync(options.SeedAdminEmail, options.SeedAdminPassword, cancellationToken);
            logger.LogInformation("Seeded dashboard admin user '{Email}'.", options.SeedAdminEmail);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to seed dashboard admin user.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
