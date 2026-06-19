using Klassd.Auth.Core.Modules.Users;
using Klassd.Auth.Core.Security;
using Klassd.Auth.Data.Sqlite;

namespace Klassd.Workflows.IntegrationTests;

/// <summary>
/// Dashboard user management now delegates to Klassd.Auth's <see cref="UserAccountService"/>. These
/// exercise the email-based create / verify / disable / external-provision flows Workflows relies on,
/// backed by the SQLite auth store (file DB, no external infrastructure).
/// </summary>
public class WorkflowsUserServiceTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"klassd-auth-test-{Guid.NewGuid():n}.db");

    private static void Cleanup(string db)
    {
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(db)!, Path.GetFileName(db) + "*"))
            try { File.Delete(f); } catch { /* best effort */ }
    }

    private static async Task<UserAccountService> NewServiceAsync(string db)
    {
        var ctx = new SqliteContext(new SqliteOptions { ConnectionString = $"Data Source={db}" });
        await new SqliteSchemaInitializer(ctx).InitializeAsync();
        return new UserAccountService(new SqliteUserStore(ctx), new Pbkdf2PasswordHasher());
    }

    [Test]
    public async Task Create_then_find_by_email_normalizes_case()
    {
        var db = TempDb();
        try
        {
            var svc = await NewServiceAsync(db);
            var user = await svc.CreateLocalAsync(null, "Jane@Example.com", "pw");
            await Assert.That(user.PrimaryEmail).IsEqualTo("jane@example.com");

            var found = await svc.FindByEmailAsync("JANE@example.com");
            await Assert.That(found!.Id).IsEqualTo(user.Id);
        }
        finally { Cleanup(db); }
    }

    [Test]
    public async Task Verify_password_rejects_wrong()
    {
        var db = TempDb();
        try
        {
            var svc = await NewServiceAsync(db);
            var user = await svc.CreateLocalAsync(null, "a@b.com", "secret");

            await Assert.That(svc.VerifyPassword(user, "secret")).IsTrue();
            await Assert.That(svc.VerifyPassword(user, "nope")).IsFalse();

            await svc.SetDisabledAsync(user.Id, true);
            var disabled = await svc.GetByIdAsync(user.Id);
            await Assert.That(disabled!.Disabled).IsTrue();
        }
        finally { Cleanup(db); }
    }

    [Test]
    public async Task Reset_password_invalidates_old()
    {
        var db = TempDb();
        try
        {
            var svc = await NewServiceAsync(db);
            var user = await svc.CreateLocalAsync(null, "a@b.com", "old");
            await svc.ResetPasswordAsync(user.Id, "new");
            var updated = await svc.GetByIdAsync(user.Id);

            await Assert.That(svc.VerifyPassword(updated!, "old")).IsFalse();
            await Assert.That(svc.VerifyPassword(updated!, "new")).IsTrue();
        }
        finally { Cleanup(db); }
    }

    [Test]
    public async Task Provision_external_links_existing_account_by_verified_email()
    {
        var db = TempDb();
        try
        {
            var svc = await NewServiceAsync(db);
            var local = await svc.CreateLocalAsync(null, "jane@example.com", "pw");

            // SSO sign-in with a provider-VERIFIED matching email attaches to the existing password
            // account (the staff "both methods, one account" flow), when auto-link is enabled.
            var linked = await svc.ProvisionExternalAsync("oidc",
                new ExternalUserInfo("sub-123", Email: "jane@example.com", EmailVerified: true),
                autoProvision: true, autoLinkByVerifiedEmail: true);

            await Assert.That(linked!.Id).IsEqualTo(local.Id);   // same account, not a new one
            await Assert.That((await svc.GetAllAsync()).Count).IsEqualTo(1);
        }
        finally { Cleanup(db); }
    }

    [Test]
    public async Task Provision_external_does_not_link_by_unverified_email()
    {
        var db = TempDb();
        try
        {
            var svc = await NewServiceAsync(db);
            await svc.CreateLocalAsync(null, "jane@example.com", "pw");

            // An UNverified matching email must NOT auto-link (account-takeover guard) — a separate
            // account is provisioned instead.
            var result = await svc.ProvisionExternalAsync("oidc",
                new ExternalUserInfo("sub-123", Email: "jane@example.com", EmailVerified: false),
                autoProvision: true, autoLinkByVerifiedEmail: true);

            await Assert.That(result).IsNotNull();
            await Assert.That((await svc.GetAllAsync()).Count).IsEqualTo(2);
        }
        finally { Cleanup(db); }
    }

    [Test]
    public async Task Provision_external_auto_provision_off_rejects_unknown()
    {
        var db = TempDb();
        try
        {
            var svc = await NewServiceAsync(db);
            var result = await svc.ProvisionExternalAsync("oidc",
                new ExternalUserInfo("sub-x", Email: "new@example.com"), autoProvision: false);
            await Assert.That(result).IsNull();
        }
        finally { Cleanup(db); }
    }

    [Test]
    public async Task Provision_external_auto_provisions_unknown_when_enabled()
    {
        var db = TempDb();
        try
        {
            var svc = await NewServiceAsync(db);
            var result = await svc.ProvisionExternalAsync("oidc",
                new ExternalUserInfo("sub-y", Email: "new@example.com"), autoProvision: true);

            await Assert.That(result).IsNotNull();
            await Assert.That(result!.PrimaryEmail).IsEqualTo("new@example.com");

            // Signing in again with the same identity reuses the account.
            var again = await svc.ProvisionExternalAsync("oidc",
                new ExternalUserInfo("sub-y", Email: "new@example.com"), autoProvision: true);
            await Assert.That(again!.Id).IsEqualTo(result.Id);
        }
        finally { Cleanup(db); }
    }
}
