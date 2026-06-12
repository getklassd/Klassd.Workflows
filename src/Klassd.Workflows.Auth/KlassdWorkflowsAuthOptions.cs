using System.Security.Claims;

namespace Klassd.Workflows.Auth;

/// <summary>Knobs for the Klassd.Workflows dashboard authentication.</summary>
public sealed class KlassdWorkflowsAuthOptions
{
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
    /// Auto-create a dashboard user the first time an unknown SSO identity signs in (after trying to
    /// link by email). Default true; set false to only admit SSO identities that match an existing user.
    /// </summary>
    public bool AutoProvisionExternalUsers { get; set; } = true;

    /// <summary>If set (with <see cref="SeedAdminPassword"/>), a user with this email is created on
    /// startup when the store has no users yet — so a fresh deployment isn't locked out.</summary>
    public string? SeedAdminEmail { get; set; }

    /// <summary>Password for the seeded admin (see <see cref="SeedAdminEmail"/>).</summary>
    public string? SeedAdminPassword { get; set; }

    /// <summary>Maps external (SSO) claims to an <see cref="ExternalUserInfo"/>. Defaults to standard
    /// OIDC claims (sub / NameIdentifier + email).</summary>
    public Func<ClaimsPrincipal, ExternalUserInfo>? MapExternalUser { get; set; }
}

/// <summary>
/// Holds the configured external login providers so the login page can render a button per provider.
/// Registered as a singleton; populated by <c>AddExternalLogin</c> (e.g. via the OIDC package).
/// </summary>
public sealed class ExternalLoginRegistry
{
    private readonly List<ExternalLoginDescriptor> _providers = [];

    public IReadOnlyList<ExternalLoginDescriptor> Providers => _providers;

    public void Add(ExternalLoginDescriptor descriptor)
    {
        if (_providers.Any(p => p.Scheme == descriptor.Scheme))
            throw new InvalidOperationException($"An external login with scheme '{descriptor.Scheme}' is already registered.");
        _providers.Add(descriptor);
    }
}
