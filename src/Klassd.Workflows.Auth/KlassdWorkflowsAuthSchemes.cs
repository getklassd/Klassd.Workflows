using Microsoft.AspNetCore.Authentication.Cookies;

namespace Klassd.Workflows.Auth;

/// <summary>Authentication scheme names used by the Klassd.Workflows dashboard.</summary>
public static class KlassdWorkflowsAuthSchemes
{
    /// <summary>Primary cookie the dashboard is authenticated with.</summary>
    public const string Cookie = CookieAuthenticationDefaults.AuthenticationScheme;

    /// <summary>
    /// Temporary cookie that external (SSO) handlers sign into. The external-login callback
    /// reads it, provisions/links a dashboard user, then signs into <see cref="Cookie"/> and
    /// clears it. External handlers must set <c>SignInScheme = KlassdWorkflowsAuthSchemes.External</c>.
    /// </summary>
    public const string External = "klassd_wf_external";
}

/// <summary>A configured external login provider, surfaced as a button on the login page.</summary>
public sealed record ExternalLoginDescriptor(string Scheme, string DisplayName);

/// <summary>The identity extracted from an external provider's claims.</summary>
public sealed record ExternalUserInfo(string ExternalId, string? Email);
