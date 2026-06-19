namespace Klassd.Workflows.Auth;

/// <summary>Knobs for the Klassd.Workflows dashboard authentication.</summary>
public sealed class KlassdWorkflowsAuthOptions
{
    /// <summary>
    /// Symmetric signing key for issued sessions (HS256). Must be at least 32 characters. When unset a
    /// fixed development key is used — set this (e.g. from configuration) for any real deployment.
    /// </summary>
    public string? SigningKey { get; set; }

    /// <summary>Name of the auth cookie issued after sign-in. Defaults to <c>klassd_wf_auth</c>.</summary>
    public string CookieName { get; set; } = "klassd_wf_auth";

    /// <summary>How long a sign-in stays valid. Defaults to 7 days, sliding.</summary>
    public TimeSpan ExpireTimeSpan { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// When true (default), requests whose connection comes from a loopback address
    /// (127.0.0.1 / ::1) skip authentication entirely. This covers local development AND
    /// <c>kubectl port-forward</c> (the forwarded connection reaches the pod over loopback),
    /// so neither needs an SSO login.
    /// </summary>
    /// <remarks>
    /// The check reads <c>HttpContext.Connection.RemoteIpAddress</c> — the real transport peer.
    /// Behind an Ingress/Service the peer is the proxy's address, never loopback, so normal
    /// traffic is always authenticated. Do NOT enable forwarded-headers processing that rewrites
    /// <c>RemoteIpAddress</c> from a client-supplied header, or the bypass could be spoofed.
    /// </remarks>
    public bool BypassOnLoopback { get; set; } = true;

    /// <summary>Allow email + password sign-in. Default true.</summary>
    public bool AllowLocalLogin { get; set; } = true;

    /// <summary>
    /// When true (default), the dashboard auth owns the whole host: its cookie becomes the app's default
    /// authentication scheme and authentication/authorization + the loopback bypass are wired globally.
    /// This is correct for a dedicated dashboard host (the Klassd.Workflows.DashboardHost sample).
    /// <para>Set false to drop the dashboard into a host that ALREADY has its own authentication (e.g. a
    /// storefront monolith). In shared-host mode the cookie is registered as a NAMED, non-default scheme
    /// and is authenticated (plus the loopback bypass) only on the dashboard's own routes — see
    /// <see cref="BasePath"/> — so the host's existing scheme keeps handling every other request. Requires
    /// <see cref="BasePath"/> and <see cref="HostAuthenticationScheme"/> to be set.</para>
    /// </summary>
    public bool OwnsHost { get; set; } = true;

    /// <summary>
    /// The sub-path the dashboard is mounted under (matching the value passed to
    /// <c>AddKlassdWorkflowsDashboard</c>), e.g. <c>"/jobs"</c>. Required when <see cref="OwnsHost"/> is
    /// false — it scopes the dashboard's authentication to those routes. In shared-host mode do NOT pair
    /// it with <c>UsePathBase</c>; host the dashboard by its <c>&lt;base href&gt;</c> so dashboard request
    /// paths keep the prefix and stay distinguishable from the host's own routes. Ignored when
    /// <see cref="OwnsHost"/> is true.
    /// </summary>
    public string BasePath { get; set; } = "";

    /// <summary>
    /// Optional override for the host's default authentication scheme to restore in shared-host mode.
    /// Normally leave this null: registering the dashboard cookie promotes it to the app default, and the
    /// host's previous default is captured and restored automatically so the host's own authorization is
    /// left untouched. Set it only if the host configures its default scheme in a way the automatic
    /// capture can't see. The dashboard NEVER authenticates against this scheme — it is staff-only and
    /// authenticates its own cookie + SSO. (For example, a storefront's ecom-customer token scheme is
    /// preserved for the storefront's routes but is never honoured on the dashboard.) Ignored when
    /// <see cref="OwnsHost"/> is true.
    /// </summary>
    public string? HostAuthenticationScheme { get; set; }

    /// <summary>
    /// Auto-create a dashboard user the first time an unknown SSO identity signs in (after trying to
    /// link by email). Default true; set false to only admit SSO identities that match an existing user.
    /// </summary>
    public bool AutoProvisionExternalUsers { get; set; } = true;

    /// <summary>If set (with <see cref="SeedAdminPassword"/>), a user with this email is created on
    /// startup when the store has no users yet — so a fresh deployment isn't locked out.</summary>
    public string? SeedAdminEmail { get; set; }

    /// <summary>Password for the seeded admin (see <see cref="SeedAdminEmail"/>).</summary>
    public string? SeedAdminPassword { get; set; }
}
