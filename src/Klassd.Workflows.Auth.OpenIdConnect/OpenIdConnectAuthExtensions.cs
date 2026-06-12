using Klassd.Workflows.Auth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Klassd.Workflows.Auth.OpenIdConnect;

/// <summary>
/// Adds OpenID Connect / OAuth 2.0 single sign-on to the dashboard — the same OIDC flow Klassd CMS
/// uses (Entra ID, Okta, Auth0, Google, Ping, …). Built on the external-login seam: the handler signs
/// into the temporary external cookie and the callback links/provisions a dashboard user by email.
/// Requires <see cref="KlassdWorkflowsAuthExtensions.AddKlassdWorkflowsAuth"/> first.
/// </summary>
public static class OpenIdConnectAuthExtensions
{
    /// <summary>Registers an OIDC provider, configured by <paramref name="configure"/>.</summary>
    public static IServiceCollection AddKlassdWorkflowsOpenIdConnect(
        this IServiceCollection services, string displayName, Action<OpenIdConnectOptions> configure,
        string scheme = "oidc")
    {
        ArgumentNullException.ThrowIfNull(configure);
        return services.AddExternalLogin(scheme, displayName, auth =>
            auth.AddOpenIdConnect(scheme, options =>
            {
                options.SignInScheme = KlassdWorkflowsAuthSchemes.External; // exchanged for the dashboard cookie
                options.CallbackPath = $"/signin-{scheme}";
                options.GetClaimsFromUserInfoEndpoint = true;
                configure(options);
            }));
    }

    /// <summary>
    /// Registers an OIDC provider from a configuration section (<c>Authority</c>, <c>ClientId</c>,
    /// <c>ClientSecret</c>, optional <c>ResponseType</c>, <c>Scope</c> array, <c>SaveTokens</c>).
    /// </summary>
    public static IServiceCollection AddKlassdWorkflowsOpenIdConnect(
        this IServiceCollection services, string displayName, IConfiguration section, string scheme = "oidc")
    {
        ArgumentNullException.ThrowIfNull(section);
        return services.AddKlassdWorkflowsOpenIdConnect(displayName, options =>
        {
            options.Authority = section["Authority"];
            options.ClientId = section["ClientId"];
            options.ClientSecret = section["ClientSecret"];
            options.ResponseType = section["ResponseType"] ?? "code";
            if (bool.TryParse(section["SaveTokens"], out var saveTokens))
                options.SaveTokens = saveTokens;

            var scopes = section.GetSection("Scope").Get<string[]>();
            if (scopes is { Length: > 0 })
            {
                options.Scope.Clear();
                foreach (var s in scopes)
                    options.Scope.Add(s);
            }
        }, scheme);
    }
}
