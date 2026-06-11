using TUnit.Core;

namespace Klassd.Workflows.IntegrationTests;

/// <summary>
/// Brings the shared K3s cluster + host up once for the whole assembly (and tears it down), but
/// only when the Kubernetes tests are enabled — otherwise the hooks are a no-op and every test is
/// skipped by <see cref="RequiresKubernetesAttribute"/>.
/// </summary>
public static class AssemblyHooks
{
    [Before(HookType.Assembly)]
    public static async Task SetUpAsync()
    {
        if (KubernetesGate.Enabled) await TestHost.InitializeAsync();
    }

    [After(HookType.Assembly)]
    public static async Task TearDownAsync()
    {
        if (KubernetesGate.Enabled) await TestHost.DisposeAsync();
    }
}
