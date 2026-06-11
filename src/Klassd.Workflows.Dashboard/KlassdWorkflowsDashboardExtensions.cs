using Klassd.Workflows.Dashboard.Components;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Klassd.Workflows.Dashboard;

/// <summary>
/// Mounts the Klassd.Workflows dashboard (Blazor Interactive Server) into an ASP.NET Core host.
/// </summary>
/// <example>
/// <code>
/// builder.Services.AddKlassdWorkflowsCore();      // scheduler, store, …
/// builder.Services.AddLocalExecutor(workerDll);   // or AddKubernetesExecutor(…)
/// builder.Services.AddKlassdWorkflowsDashboard();  // the UI
///
/// var app = builder.Build();
/// app.UseAntiforgery();
/// app.MapKlassdWorkflowsDashboard();
/// </code>
/// The host must also set <c>&lt;RequiresAspNetWebAssets&gt;true&lt;/RequiresAspNetWebAssets&gt;</c>
/// in its csproj if it has no Blazor components of its own, and link the dashboard's stylesheets —
/// done for you by <see cref="MapKlassdWorkflowsDashboard"/> rendering its own root document.
/// </example>
public static class KlassdWorkflowsDashboardExtensions
{
    /// <summary>Registers the Razor components + Interactive Server services the dashboard needs.</summary>
    public static IServiceCollection AddKlassdWorkflowsDashboard(this IServiceCollection services)
    {
        services.AddRazorComponents().AddInteractiveServerComponents();
        return services;
    }

    /// <summary>Maps the dashboard's static assets and Razor component endpoints.</summary>
    public static IEndpointRouteBuilder MapKlassdWorkflowsDashboard(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapStaticAssets();
        endpoints.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
        return endpoints;
    }
}
