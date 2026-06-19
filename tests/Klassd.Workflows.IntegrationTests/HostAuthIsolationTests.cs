using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Klassd.Workflows.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Klassd.Workflows.IntegrationTests;

/// <summary>
/// Regression coverage for the shared-host (<c>OwnsHost = false</c>) authentication mode: dropping the
/// Workflows dashboard auth into a host that already has its own auth scheme must NOT take over that
/// host's authorization. The dashboard cookie + loopback bypass are scoped to the dashboard routes; a
/// protected non-dashboard endpoint keeps being guarded by the host's own scheme.
/// </summary>
public class HostAuthIsolationTests
{
    private const string StorefrontScheme = "Storefront";
    private const string StorefrontHeader = "x-storefront-token";

    /// <summary>The original bug: a protected host endpoint stayed 401 without a host token — it did NOT
    /// flip to 200 because the dashboard auth was wired in (even with the loopback peer present).</summary>
    [Test]
    public async Task Protected_host_endpoint_is_not_bypassed_by_dashboard_auth()
    {
        await using var host = await BuildSharedHostAsync();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/api/orders");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Host_endpoint_still_passes_with_the_hosts_own_token()
    {
        await using var host = await BuildSharedHostAsync();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(StorefrontHeader, "present");

        var response = await client.GetAsync("/api/orders");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Loopback_bypass_does_not_leak_onto_host_routes()
    {
        await using var host = await BuildSharedHostAsync();
        var client = host.GetTestClient();

        // Loopback peer is present (BuildSharedHostAsync forces it), yet a host route sees no forged user.
        var who = await (await client.GetAsync("/api/whoami")).Content.ReadAsStringAsync();

        await Assert.That(who).IsEqualTo("(anon)");
    }

    [Test]
    public async Task Loopback_bypass_still_authenticates_dashboard_routes()
    {
        await using var host = await BuildSharedHostAsync();
        var client = host.GetTestClient();

        var who = await (await client.GetAsync("/jobs/whoami")).Content.ReadAsStringAsync();

        await Assert.That(who).IsEqualTo("localhost");
    }

    /// <summary>The staff-only invariant: an ecom customer holding a valid host (customer) token must NOT
    /// be admitted to the dashboard. The dashboard authenticates its own cookie exclusively, so the
    /// customer principal is reset to anonymous on dashboard routes.</summary>
    [Test]
    public async Task Customer_token_does_not_authenticate_the_dashboard()
    {
        // Disable the loopback bypass so the only thing that could let the customer in is the token itself.
        await using var host = await BuildSharedHostAsync(bypassOnLoopback: false);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(StorefrontHeader, "present");

        // The customer token authenticates a host route...
        var orders = await client.GetAsync("/api/orders");
        await Assert.That(orders.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // ...but the SAME token must not leak a principal onto the dashboard.
        var who = await (await client.GetAsync("/jobs/whoami")).Content.ReadAsStringAsync();
        await Assert.That(who).IsEqualTo("(anon)");
    }

    /// <summary>The signed-in-only cookie endpoints (e.g. account linking / <c>/me/*</c>) must remain
    /// reachable in shared-host mode and authorize against the cookie scheme — not the host's default
    /// (customer) scheme. Proven by the cookie handler's challenge (redirect to its login path) rather
    /// than the host scheme's 401.</summary>
    [Test]
    public async Task Account_linking_endpoint_is_cookie_scheme_bound_in_shared_host()
    {
        // Bypass off, so an unauthenticated call really is challenged (not silently let through).
        await using var host = await BuildSharedHostAsync(bypassOnLoopback: false);
        var client = host.GetTestClient();

        var response = await client.GetAsync("/auth/me/methods");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location!.OriginalString).Contains("login");
    }

    /// <summary>
    /// A host that mirrors a storefront monolith: a default "Storefront" auth scheme guarding its own
    /// endpoints, with the Workflows dashboard auth layered in under <c>/jobs</c> in shared-host mode.
    /// Every request is given a loopback transport peer so the dashboard's loopback bypass is in play.
    /// </summary>
    private static async Task<WebApplication> BuildSharedHostAsync(bool bypassOnLoopback = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddRouting();
        builder.Services.AddAuthentication(StorefrontScheme)
            .AddScheme<AuthenticationSchemeOptions, StorefrontHandler>(StorefrontScheme, _ => { });
        builder.Services.AddAuthorization();

        // Note: no HostAuthenticationScheme — the host's default (StorefrontScheme) is captured + restored
        // automatically, so the dashboard never needs to be told the customer scheme's name.
        builder.Services.AddKlassdWorkflowsAuth(o =>
        {
            o.OwnsHost = false;
            o.BasePath = "/jobs";
            o.BypassOnLoopback = bypassOnLoopback;
            o.SigningKey = "test-signing-key-that-is-at-least-32-chars";
        });

        var app = builder.Build();

        // TestServer has no real transport peer; simulate a loopback connection (local dev / port-forward).
        app.Use(async (ctx, next) =>
        {
            ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
            await next(ctx);
        });

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseKlassdWorkflowsAuth();

        app.MapGet("/api/orders", () => Results.Ok("orders")).RequireAuthorization();
        app.MapGet("/api/whoami", (HttpContext c) => c.User.Identity?.Name ?? "(anon)");
        app.MapGet("/jobs/whoami", (HttpContext c) => c.User.Identity?.Name ?? "(anon)");

        await app.StartAsync();
        return app;
    }

    /// <summary>Stand-in for the host's own scheme: authenticates only when a token header is present.</summary>
    private sealed class StorefrontHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey(StorefrontHeader))
                return Task.FromResult(AuthenticateResult.NoResult());

            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "storefront-user")], StorefrontScheme));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, StorefrontScheme)));
        }
    }
}
