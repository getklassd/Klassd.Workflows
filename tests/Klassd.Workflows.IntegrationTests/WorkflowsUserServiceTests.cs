using Klassd.Workflows.Auth;

namespace Klassd.Workflows.IntegrationTests;

/// <summary>Unit tests for the dashboard user service (no infrastructure).</summary>
public class WorkflowsUserServiceTests
{
    private static WorkflowsUserService NewService() => new(new InMemoryWorkflowsUserStore());

    [Test]
    public async Task Create_then_find_by_email_normalizes_case()
    {
        var svc = NewService();
        var user = await svc.CreateAsync("Jane@Example.com", "pw");
        await Assert.That(user.Email).IsEqualTo("jane@example.com");
        await Assert.That(user.Provider).IsEqualTo("local");

        var found = await svc.FindByEmailAsync("JANE@example.com");
        await Assert.That(found!.Id).IsEqualTo(user.Id);
    }

    [Test]
    public async Task Duplicate_email_throws()
    {
        var svc = NewService();
        await svc.CreateAsync("a@b.com", "pw");
        await Assert.That(async () => await svc.CreateAsync("a@b.com", "pw2"))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Verify_password_rejects_wrong_and_disabled()
    {
        var svc = NewService();
        var user = await svc.CreateAsync("a@b.com", "secret");

        await Assert.That(svc.VerifyPassword(user, "secret")).IsTrue();
        await Assert.That(svc.VerifyPassword(user, "nope")).IsFalse();

        await svc.SetDisabledAsync(user.Id, true);
        var disabled = await svc.GetByIdAsync(user.Id);
        await Assert.That(svc.VerifyPassword(disabled!, "secret")).IsFalse();
    }

    [Test]
    public async Task Reset_password_invalidates_old()
    {
        var svc = NewService();
        var user = await svc.CreateAsync("a@b.com", "old");
        await svc.ResetPasswordAsync(user.Id, "new");
        var updated = await svc.GetByIdAsync(user.Id);

        await Assert.That(svc.VerifyPassword(updated!, "old")).IsFalse();
        await Assert.That(svc.VerifyPassword(updated!, "new")).IsTrue();
    }

    [Test]
    public async Task Provision_external_links_existing_account_by_email()
    {
        var svc = NewService();
        var local = await svc.CreateAsync("jane@example.com", "pw");

        var linked = await svc.ProvisionExternalAsync("oidc",
            new ExternalUserInfo("sub-123", "jane@example.com"), autoProvision: true);

        await Assert.That(linked!.Id).IsEqualTo(local.Id);   // same account, not a new one
        await Assert.That(linked.Provider).IsEqualTo("oidc");
        await Assert.That(linked.ExternalId).IsEqualTo("sub-123");
        await Assert.That((await svc.GetAllAsync()).Count).IsEqualTo(1);
    }

    [Test]
    public async Task Provision_external_auto_provision_off_rejects_unknown()
    {
        var svc = NewService();
        var result = await svc.ProvisionExternalAsync("oidc",
            new ExternalUserInfo("sub-x", "new@example.com"), autoProvision: false);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Provision_external_auto_provisions_unknown_when_enabled()
    {
        var svc = NewService();
        var result = await svc.ProvisionExternalAsync("oidc",
            new ExternalUserInfo("sub-y", "new@example.com"), autoProvision: true);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Provider).IsEqualTo("oidc");
        await Assert.That(result.ExternalId).IsEqualTo("sub-y");

        // Signing in again with the same identity reuses the account.
        var again = await svc.ProvisionExternalAsync("oidc",
            new ExternalUserInfo("sub-y", "new@example.com"), autoProvision: true);
        await Assert.That(again!.Id).IsEqualTo(result.Id);
    }

    [Test]
    public async Task Seed_only_creates_when_empty()
    {
        var svc = NewService();
        await svc.SeedAsync("admin@example.com", "pw");
        await svc.SeedAsync("other@example.com", "pw"); // no-op: users already exist

        var all = await svc.GetAllAsync();
        await Assert.That(all.Count).IsEqualTo(1);
        await Assert.That(all[0].Email).IsEqualTo("admin@example.com");
    }
}
