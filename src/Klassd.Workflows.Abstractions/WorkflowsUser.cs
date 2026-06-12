namespace Klassd.Workflows.Abstractions;

/// <summary>
/// A dashboard user. Identity is the <see cref="Email"/> (there is no separate username).
/// Local users have a password hash; external (SSO) users are linked by
/// <see cref="Provider"/> + <see cref="ExternalId"/> and carry no password.
/// </summary>
public sealed class WorkflowsUser
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Login identity and the join key when linking an external (SSO) sign-in to an account.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>PBKDF2 hash for local users; empty for external-only (SSO) users.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Login provider: <c>"local"</c> or an external scheme name (e.g. <c>"oidc"</c>).</summary>
    public string Provider { get; set; } = "local";

    /// <summary>Stable subject from the identity provider (OIDC <c>sub</c>); null for local users.</summary>
    public string? ExternalId { get; set; }

    /// <summary>Disabled users cannot sign in (local or SSO). Kept rather than deleted.</summary>
    public bool Disabled { get; set; }
}
