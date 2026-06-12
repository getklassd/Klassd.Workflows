using Klassd.Auth.Abstractions;
using Klassd.Auth.AspNetCore.Cookies;
using Klassd.Auth.Core.DependencyInjection;
using Klassd.Auth.Core.Sessions;
using Klassd.Auth.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Klassd.Workflows.Auth;

/// <summary>
/// Adds cookie-based authentication + a Users admin to the Klassd.Workflows dashboard. This is a thin
/// Workflows profile over the Klassd.Auth packages: email + password local accounts, SSO layered on via
/// the external-login seam (see Klassd.Workflows.Auth.OpenIdConnect, which links by email), and a
/// loopback bypass for local dev / <c>kubectl port-forward</c>.
/// </summary>
/// <example>
/// <code>
/// builder.Services.AddKlassdWorkflowsAuth(o =>
/// {
///     o.SigningKey = config["Auth:SigningKey"];
///     o.SeedAdminEmail = config["Auth:SeedAdmin:Email"];
///     o.SeedAdminPassword = config["Auth:SeedAdmin:Password"];
/// });
/// builder.Services.AddKlassdWorkflowsOpenIdConnect("Company SSO", config.GetSection("Oidc")); // optional
///
/// var app = builder.Build();
/// app.UseKlassdWorkflowsAuth();   // before MapKlassdWorkflowsDashboard()
/// app.MapKlassdWorkflowsDashboard();
/// </code>
/// </example>
public static class KlassdWorkflowsAuthExtensions
{
    /// <summary>Development fallback signing key (HS256, 32+ chars). Override via <see cref="KlassdWorkflowsAuthOptions.SigningKey"/>.</summary>
    private const string DevSigningKey = "klassd-workflows-dev-signing-key-change-me";

    /// <summary>
    /// Registers Klassd.Auth with cookie sign-in tuned for the Workflows dashboard: the auth cookie,
    /// the external-SSO callback cookie, cascading auth state, the optional seed admin, and a default
    /// SQLite user store (a Workflows storage adapter — UseSqlite / UsePostgres / UseMongo — overrides
    /// it with the matching durable Klassd.Auth store).
    /// </summary>
    public static IServiceCollection AddKlassdWorkflowsAuth(
        this IServiceCollection services, Action<KlassdWorkflowsAuthOptions>? configure = null)
    {
        var options = new KlassdWorkflowsAuthOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        var auth = services.AddKlassdAuth(new SessionConfig
        {
            SigningKey = string.IsNullOrWhiteSpace(options.SigningKey) ? DevSigningKey : options.SigningKey,
        });

        auth.AddKlassdAuthCookies(o =>
        {
            o.CookieName = options.CookieName;
            o.BasePath = "/auth";
            o.LoginPath = "/login";
            o.AccessDeniedPath = "/login";
            o.ExpireTimeSpan = options.ExpireTimeSpan;
            o.BypassOnLoopback = options.BypassOnLoopback;
            o.AllowLocalLogin = options.AllowLocalLogin;
            o.AutoProvisionExternalUsers = options.AutoProvisionExternalUsers;
            if (!string.IsNullOrEmpty(options.SeedAdminPassword))
            {
                o.SeedAdminEmail = options.SeedAdminEmail;
                o.SeedAdminPassword = options.SeedAdminPassword;
            }
        });

        // Default durable store so auth works out of the box; a Workflows storage adapter
        // (UseSqlite/UsePostgres/UseMongo) re-registers the matching Klassd.Auth store and wins.
        auth.UseSqlite("Data Source=klassd-wf-auth.db");

        // Stash the builder so the storage adapters can attach their matching Klassd.Auth store.
        services.AddSingleton(auth);
        return services;
    }

    /// <summary>
    /// Wires authentication + the loopback bypass + authorization into the pipeline and maps the
    /// login/logout/SSO endpoints under <c>/auth</c>. Call before mapping the dashboard.
    /// </summary>
    public static WebApplication UseKlassdWorkflowsAuth(this WebApplication app) => app.UseKlassdAuthCookies();
}
