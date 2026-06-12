using System.Net;
using System.Security.Claims;
using Klassd.Workflows.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Klassd.Workflows.Auth;

/// <summary>
/// Adds cookie-based authentication + a Users admin to the Klassd.Workflows dashboard, mirroring the
/// Klassd CMS auth model. Email + password local accounts; SSO is layered on via the external-login
/// seam (see Klassd.Workflows.Auth.OpenIdConnect). Loopback requests bypass auth (local dev /
/// port-forward).
/// </summary>
/// <example>
/// <code>
/// builder.Services.AddKlassdWorkflowsAuth(o =>
/// {
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
    /// <summary>
    /// Registers the auth cookie + temporary external cookie, the user service and default in-memory
    /// user store (durable adapters override it), cascading auth state, and the seed-admin startup task.
    /// </summary>
    public static IServiceCollection AddKlassdWorkflowsAuth(
        this IServiceCollection services, Action<KlassdWorkflowsAuthOptions>? configure = null)
    {
        var options = new KlassdWorkflowsAuthOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton(new ExternalLoginRegistry());

        // Default user store; a storage adapter (UsePostgres/UseMongo/UseSqlite) replaces it.
        services.TryAddSingleton<IWorkflowsUserStore, InMemoryWorkflowsUserStore>();
        services.AddScoped<WorkflowsUserService>();

        services.AddAuthentication(KlassdWorkflowsAuthSchemes.Cookie)
            .AddCookie(KlassdWorkflowsAuthSchemes.Cookie, o =>
            {
                o.Cookie.Name = options.CookieName;
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.LoginPath = "/login";
                o.AccessDeniedPath = "/login";
                o.ExpireTimeSpan = options.ExpireTimeSpan;
                o.SlidingExpiration = true;
            })
            .AddCookie(KlassdWorkflowsAuthSchemes.External, o =>
            {
                o.Cookie.Name = "klassd_wf_external";
                o.Cookie.HttpOnly = true;
                o.ExpireTimeSpan = TimeSpan.FromMinutes(10);
            });

        services.AddAuthorization();
        services.AddCascadingAuthenticationState();
        services.AddHostedService<SeedAdminHostedService>();
        return services;
    }

    /// <summary>
    /// Wires authentication + authorization into the pipeline (with the loopback bypass between them)
    /// and maps the login/logout/SSO endpoints. Call before mapping the dashboard.
    /// </summary>
    public static WebApplication UseKlassdWorkflowsAuth(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<KlassdWorkflowsAuthOptions>();

        app.UseAuthentication();

        if (options.BypassOnLoopback)
            app.Use(async (context, next) =>
            {
                // Real transport peer — unaffected by X-Forwarded-* unless the host explicitly
                // rewrites it (which the options doc warns against).
                var ip = context.Connection.RemoteIpAddress;
                if (ip is not null && IPAddress.IsLoopback(ip) && context.User.Identity?.IsAuthenticated != true)
                {
                    var identity = new ClaimsIdentity(
                        [
                            new Claim(ClaimTypes.Name, "localhost"),
                            new Claim(ClaimTypes.NameIdentifier, "loopback"),
                        ],
                        authenticationType: "Loopback");
                    context.User = new ClaimsPrincipal(identity);
                }

                await next(context);
            });

        app.UseAuthorization();
        app.MapKlassdWorkflowsAuthEndpoints();
        return app;
    }

    /// <summary>Effective local-login state: forced on if no external providers are configured (anti-lockout).</summary>
    public static bool LocalLoginEnabled(this KlassdWorkflowsAuthOptions options, ExternalLoginRegistry registry) =>
        options.AllowLocalLogin || registry.Providers.Count == 0;

    private static void MapKlassdWorkflowsAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        // ── Local email/password ──────────────────────────────────────
        routes.MapPost("/auth/login", async (HttpContext ctx, WorkflowsUserService users, KlassdWorkflowsAuthOptions opts, ExternalLoginRegistry registry) =>
        {
            if (!opts.LocalLoginEnabled(registry))
                return Results.Redirect("/login?error=1");

            var form = await ctx.Request.ReadFormAsync();
            var email = form["email"].ToString();
            var password = form["password"].ToString();

            var user = await users.FindByEmailAsync(email);
            if (user is null || !users.VerifyPassword(user, password)) // VerifyPassword rejects disabled users
                return Results.Redirect("/login?error=1");

            await SignInAsync(ctx, user);
            return Results.Redirect("/");
        }).AllowAnonymous().DisableAntiforgery();

        routes.MapPost("/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(KlassdWorkflowsAuthSchemes.Cookie);
            return Results.Redirect("/login");
        }).DisableAntiforgery();

        // ── External (SSO) ────────────────────────────────────────────
        routes.MapGet("/auth/external/{scheme}", (string scheme, ExternalLoginRegistry registry) =>
        {
            if (registry.Providers.All(p => p.Scheme != scheme))
                return Results.NotFound();

            var props = new AuthenticationProperties { RedirectUri = "/auth/external-callback" };
            props.Items["scheme"] = scheme; // preserved so the callback knows the provider
            return Results.Challenge(props, [scheme]);
        }).AllowAnonymous();

        routes.MapGet("/auth/external-callback", async (HttpContext ctx, WorkflowsUserService users, KlassdWorkflowsAuthOptions opts) =>
        {
            var result = await ctx.AuthenticateAsync(KlassdWorkflowsAuthSchemes.External);
            if (!result.Succeeded || result.Principal is null)
                return Results.Redirect("/login?error=sso");

            var scheme = result.Properties?.Items.TryGetValue("scheme", out var s) == true && s is not null
                ? s
                : result.Ticket?.AuthenticationScheme ?? KlassdWorkflowsAuthSchemes.External;

            var info = (opts.MapExternalUser ?? WorkflowsUserService.DefaultExternalMapping)(result.Principal);
            if (string.IsNullOrWhiteSpace(info.ExternalId))
                return Results.Redirect("/login?error=sso");

            var user = await users.ProvisionExternalAsync(scheme, info, opts.AutoProvisionExternalUsers);

            await ctx.SignOutAsync(KlassdWorkflowsAuthSchemes.External); // clear the temp cookie regardless

            if (user is null) // unknown identity + auto-provision off, or the matched account is disabled
                return Results.Redirect("/login?error=sso");

            await SignInAsync(ctx, user);
            return Results.Redirect("/");
        }).AllowAnonymous();
    }

    /// <summary>Signs the user into the primary dashboard cookie.</summary>
    private static Task SignInAsync(HttpContext ctx, WorkflowsUser user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.Email),
            new(ClaimTypes.Email, user.Email),
        };
        var identity = new ClaimsIdentity(claims, KlassdWorkflowsAuthSchemes.Cookie);
        return ctx.SignInAsync(KlassdWorkflowsAuthSchemes.Cookie, new ClaimsPrincipal(identity));
    }
}
