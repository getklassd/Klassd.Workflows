using System.Net;
using System.Security.Claims;
using Klassd.Auth.Abstractions;
using Klassd.Auth.AspNetCore.Cookies;
using Klassd.Auth.Core.DependencyInjection;
using Klassd.Auth.Core.Sessions;
using Klassd.Auth.Data.Sqlite;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

        if (!options.OwnsHost && string.IsNullOrWhiteSpace(options.BasePath))
            throw new InvalidOperationException(
                "KlassdWorkflowsAuthOptions.OwnsHost is false but BasePath is empty. In shared-host mode the " +
                "dashboard must be mounted under a non-empty BasePath (matching AddKlassdWorkflowsDashboard) so " +
                "its authentication can be scoped to those routes.");

        services.AddSingleton(options);

        // Shared-host mode: capture whatever default authentication scheme the host already configured
        // BEFORE AddKlassdAuthCookies (below) promotes its cookie to the default — so we can restore it
        // afterwards. This Configure runs in registration order (after the host's AddAuthentication, before
        // Klassd's), so it observes the host's value, not the cookie's. The dashboard NEVER authenticates
        // against this scheme; it's preserved only so the host's own auth keeps working untouched.
        string? capturedHostDefaultScheme = null;
        if (!options.OwnsHost)
            services.Configure<AuthenticationOptions>(o => capturedHostDefaultScheme = o.DefaultScheme);

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
            // In shared-host mode the loopback bypass is re-applied, scoped to the dashboard routes, in
            // UseKlassdWorkflowsAuth — never globally (it would forge a principal for the host's routes too).
            o.BypassOnLoopback = options.OwnsHost && options.BypassOnLoopback;
            o.AllowLocalLogin = options.AllowLocalLogin;
            o.AutoProvisionExternalUsers = options.AutoProvisionExternalUsers;
            o.AutoLinkByVerifiedEmail = options.AutoLinkByVerifiedEmail;
            if (!string.IsNullOrEmpty(options.SeedAdminPassword))
            {
                o.SeedAdminEmail = options.SeedAdminEmail;
                o.SeedAdminPassword = options.SeedAdminPassword;
            }
        });

        if (!options.OwnsHost)
        {
            // AddKlassdAuthCookies called AddAuthentication(KlassdAuthSchemes.Cookie), which promotes the
            // cookie to the app's DEFAULT scheme. In a shared host that would silently re-point the host's
            // own RequireAuthorization()/[Authorize] (anything relying on the default scheme) at the cookie.
            // Restore the host's default — the cookie stays a named scheme and is authenticated explicitly,
            // only on dashboard routes, in UseKlassdWorkflowsAuth. PostConfigure runs after all Configure,
            // so it has the last word. HostAuthenticationScheme overrides the captured value if set.
            services.PostConfigure<AuthenticationOptions>(o =>
                o.DefaultScheme = string.IsNullOrWhiteSpace(options.HostAuthenticationScheme)
                    ? capturedHostDefaultScheme
                    : options.HostAuthenticationScheme);
        }

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
    /// <para>When <see cref="KlassdWorkflowsAuthOptions.OwnsHost"/> is false, authentication and the
    /// loopback bypass are scoped to the dashboard's <see cref="KlassdWorkflowsAuthOptions.BasePath"/>
    /// (and the <c>/auth</c> endpoints) so the host's own authentication is never touched elsewhere.</para>
    /// </summary>
    public static WebApplication UseKlassdWorkflowsAuth(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<KlassdWorkflowsAuthOptions>();
        if (options.OwnsHost)
            return app.UseKlassdAuthCookies();

        // Shared-host mode. Authenticate the dashboard cookie (and forge a loopback principal, if enabled)
        // ONLY on the dashboard's own routes and the /auth endpoints. Every other request falls through
        // untouched, so the host's default scheme keeps handling it.
        var dashboardPath = new PathString(options.BasePath.TrimEnd('/'));
        var authPath = new PathString("/auth");

        app.UseWhen(
            ctx => ctx.Request.Path.StartsWithSegments(dashboardPath)
                   || ctx.Request.Path.StartsWithSegments(authPath),
            branch =>
            {
                branch.Use(async (ctx, next) =>
                {
                    // Authenticate the dashboard cookie EXCLUSIVELY and REPLACE the principal — never inherit
                    // whatever the host's own scheme may have set on this request. The jobs dashboard is
                    // staff-only: an ecom customer presenting the host's customer token must NOT satisfy the
                    // dashboard's [Authorize], so we reset to anonymous when there is no valid dashboard cookie.
                    var result = await ctx.AuthenticateAsync(KlassdAuthSchemes.Cookie);
                    ctx.User = result.Succeeded && result.Principal is not null
                        ? result.Principal
                        : new ClaimsPrincipal(new ClaimsIdentity());
                    await next(ctx);
                });

                if (options.BypassOnLoopback)
                    branch.Use(async (ctx, next) =>
                    {
                        if (ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip)
                            && ctx.User.Identity?.IsAuthenticated != true)
                        {
                            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                                [new Claim(ClaimTypes.Name, "localhost"), new Claim(ClaimTypes.NameIdentifier, "loopback")],
                                "Loopback"));
                        }
                        await next(ctx);
                    });
            });

        // The login/logout/SSO endpoints under /auth. They sign in/out the cookie with an explicit
        // scheme, so they work regardless of the host's default scheme.
        app.MapKlassdAuthCookieEndpoints();
        return app;
    }
}
