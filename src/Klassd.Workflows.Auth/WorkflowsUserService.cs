using System.Security.Claims;
using System.Security.Cryptography;
using Klassd.Workflows.Abstractions;

namespace Klassd.Workflows.Auth;

/// <summary>
/// Dashboard user management over an <see cref="IWorkflowsUserStore"/>: create/list local users,
/// verify passwords, enable/disable, reset password, and find-or-link-or-create for SSO sign-ins.
/// Mirrors the Klassd CMS user service (identity is the email; no roles).
/// </summary>
public sealed class WorkflowsUserService(IWorkflowsUserStore store)
{
    public Task<IReadOnlyList<WorkflowsUser>> GetAllAsync(CancellationToken ct = default) => store.GetAllAsync(ct);

    public Task<WorkflowsUser?> GetByIdAsync(string id, CancellationToken ct = default) => store.GetByIdAsync(id, ct);

    public Task<WorkflowsUser?> FindByEmailAsync(string email, CancellationToken ct = default) =>
        store.FindByEmailAsync(NormalizeEmail(email), ct);

    /// <summary>Creates a local (password) user. Throws if the email is already taken.</summary>
    public async Task<WorkflowsUser> CreateAsync(string email, string password, CancellationToken ct = default)
    {
        email = NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(email))
            throw new InvalidOperationException("Email is required.");
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException("Password is required.");
        if (await store.FindByEmailAsync(email, ct) is not null)
            throw new InvalidOperationException($"A user with email '{email}' already exists.");

        var user = new WorkflowsUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = email,
            PasswordHash = HashPassword(password),
            Provider = "local",
        };
        await store.InsertAsync(user, ct);
        return user;
    }

    /// <summary>Enables/disables a user. Disabled users cannot sign in (local or SSO).</summary>
    public async Task<bool> SetDisabledAsync(string id, bool disabled, CancellationToken ct = default)
    {
        var user = await store.GetByIdAsync(id, ct);
        if (user is null) return false;
        user.Disabled = disabled;
        await store.UpdateAsync(user, ct);
        return true;
    }

    /// <summary>Resets a user's password (and makes them a local user if they weren't).</summary>
    public async Task<bool> ResetPasswordAsync(string id, string newPassword, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(newPassword))
            throw new InvalidOperationException("Password is required.");
        var user = await store.GetByIdAsync(id, ct);
        if (user is null) return false;
        user.PasswordHash = HashPassword(newPassword);
        await store.UpdateAsync(user, ct);
        return true;
    }

    /// <summary>
    /// Find-or-link-or-create a dashboard user for an external (SSO) identity. Returns the user, or
    /// null when the matched account is disabled, or when unknown and auto-provisioning is off.
    /// </summary>
    public async Task<WorkflowsUser?> ProvisionExternalAsync(string provider, ExternalUserInfo info, bool autoProvision, CancellationToken ct = default)
    {
        // 1) Already linked by (provider, external id).
        var linked = await store.FindByExternalAsync(provider, info.ExternalId, ct);
        if (linked is not null) return linked.Disabled ? null : linked;

        // 2) Link to an existing account that shares the email.
        if (!string.IsNullOrWhiteSpace(info.Email))
        {
            var byEmail = await store.FindByEmailAsync(NormalizeEmail(info.Email), ct);
            if (byEmail is not null)
            {
                if (byEmail.Disabled) return null;
                byEmail.Provider = provider;
                byEmail.ExternalId = info.ExternalId;
                await store.UpdateAsync(byEmail, ct);
                return byEmail;
            }
        }

        // 3) Auto-provision a new external user.
        if (!autoProvision) return null;
        var user = new WorkflowsUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = string.IsNullOrWhiteSpace(info.Email) ? info.ExternalId : NormalizeEmail(info.Email),
            Provider = provider,
            ExternalId = info.ExternalId,
            PasswordHash = string.Empty,
        };
        await store.InsertAsync(user, ct);
        return user;
    }

    /// <summary>Creates the seed user if no users exist yet. No-op if any user is present.</summary>
    public async Task SeedAsync(string email, string password, CancellationToken ct = default)
    {
        if ((await store.GetAllAsync(ct)).Count > 0) return;
        await CreateAsync(email, password, ct);
    }

    /// <summary>True only for enabled local users with a matching password (external users have no password).</summary>
    public bool VerifyPassword(WorkflowsUser user, string password) =>
        !user.Disabled && !string.IsNullOrEmpty(user.PasswordHash) && VerifyPasswordHash(password, user.PasswordHash);

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    // ── PBKDF2-SHA256, no extra packages ──────────────────────────────────────
    private static string HashPassword(string password)
    {
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);
        var hash = Pbkdf2(password, salt);
        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPasswordHash(string password, string storedHash)
    {
        var parts = storedHash.Split(':');
        if (parts.Length != 2) return false;
        var salt = Convert.FromBase64String(parts[0]);
        var expected = Convert.FromBase64String(parts[1]);
        var actual = Pbkdf2(password, salt);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Pbkdf2(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);

    /// <summary>Default external claims → identity mapping (OIDC <c>sub</c>/NameIdentifier + email).</summary>
    public static ExternalUserInfo DefaultExternalMapping(ClaimsPrincipal principal)
    {
        var externalId = principal.FindFirstValue("sub")
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? string.Empty;
        var email = principal.FindFirstValue("email") ?? principal.FindFirstValue(ClaimTypes.Email);
        return new ExternalUserInfo(externalId, email);
    }
}
