using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Klassd.Workflows.Auth;

/// <summary>
/// Host/adapter-facing seam for adding SSO providers to the dashboard login. SSO packages
/// (e.g. Klassd.Workflows.Auth.OpenIdConnect) build their <c>AddKlassdWorkflows…</c> on top of this.
/// </summary>
public static class ExternalLoginExtensions
{
    /// <summary>
    /// Registers an external login provider: records it (so a "Sign in with {displayName}" button
    /// appears on the login page) and lets <paramref name="configure"/> attach the actual handler
    /// to the shared authentication builder. The handler MUST set
    /// <c>SignInScheme = <see cref="KlassdWorkflowsAuthSchemes.External"/></c> so the external-login
    /// callback can provision/link the dashboard user.
    /// </summary>
    public static IServiceCollection AddExternalLogin(
        this IServiceCollection services, string scheme, string displayName, Action<AuthenticationBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(configure);

        // The registry is registered by AddKlassdWorkflowsAuth; capture it from the pending descriptor.
        var registry = services.LastOrDefault(d => d.ServiceType == typeof(ExternalLoginRegistry))?.ImplementationInstance as ExternalLoginRegistry
            ?? throw new InvalidOperationException(
                "Klassd.Workflows auth is not registered. Call AddKlassdWorkflowsAuth() before adding external logins.");
        registry.Add(new ExternalLoginDescriptor(scheme, displayName));

        // The cookie schemes were already configured in AddKlassdWorkflowsAuth, so this only adds the handler.
        configure(services.AddAuthentication());
        return services;
    }
}
