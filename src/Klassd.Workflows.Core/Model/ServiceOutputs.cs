namespace Klassd.Workflows.Core.Model;

/// <summary>
/// Output keys the engine publishes automatically for a container/service node (so you don't have
/// to memorise the strings). Bind them with <c>NodeBuilder.BindServiceAddress</c> /
/// <c>BindServiceIp</c>, or pass a constant to <c>BindInput</c>.
/// </summary>
public static class ServiceOutputs
{
    /// <summary>The pod IP of a container/service node (Argo's <c>{{tasks.x.ip}}</c>).</summary>
    public const string Ip = "ip";

    /// <summary>The node's <c>ip:port</c> (when a <c>ServicePort</c> is set) — what dependents connect to.</summary>
    public const string Address = "address";
}
