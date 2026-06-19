using Klassd.Workflows.Dashboard.Components;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Klassd.Workflows.Dashboard;

/// <summary>Configuration for the Klassd.Workflows dashboard.</summary>
public sealed class WorkflowsDashboardOptions
{
    /// <summary>Sub-path the dashboard is hosted under (no trailing slash), or empty for the root.</summary>
    public string BasePath { get; init; } = "";

    /// <summary>The <c>&lt;base href&gt;</c> value — the base path with a required trailing slash.</summary>
    public string BaseHref => string.IsNullOrEmpty(BasePath) ? "/" : $"{BasePath.TrimEnd('/')}/";
}

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
/// <para>To mount the dashboard under a sub-path, pass it to <c>AddKlassdWorkflowsDashboard</c> and
/// call <c>UsePathBase</c> first (it must run before routing):</para>
/// <code>
/// builder.Services.AddKlassdWorkflowsDashboard("/workflows");
/// var app = builder.Build();
/// app.UsePathBase("/workflows");   // FIRST, immediately after Build()
/// app.UseAntiforgery();
/// app.MapKlassdWorkflowsDashboard();
/// </code>
/// </example>
public static class KlassdWorkflowsDashboardExtensions
{
    /// <summary>
    /// Registers the Razor components + Interactive Server services the dashboard needs.
    /// <paramref name="basePath"/> sets the app's <c>&lt;base href&gt;</c> for hosting under a sub-path
    /// (pair it with <c>app.UsePathBase(basePath)</c>); empty ⇒ served at the root.
    /// </summary>
    public static IServiceCollection AddKlassdWorkflowsDashboard(this IServiceCollection services, string basePath = "")
    {
        services.AddSingleton(new WorkflowsDashboardOptions { BasePath = basePath });
        services.AddRazorComponents().AddInteractiveServerComponents();
        return services;
    }

    /// <summary>Maps the dashboard's static assets and Razor component endpoints.</summary>
    public static IEndpointRouteBuilder MapKlassdWorkflowsDashboard(this IEndpointRouteBuilder endpoints)
    {
        // Static assets stay anonymous so CSS/JS load on the login page (before sign-in).
        // No-op when no authorization is configured.
        endpoints.MapStaticAssets().AllowAnonymous();
        endpoints.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
        return endpoints;
    }
}
