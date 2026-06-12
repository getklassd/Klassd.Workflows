using Klassd.Workflows.Abstractions;

namespace Klassd.Workflows.SampleJobs.Dag;

/// <summary>
/// A long-running "service" (daemon) node, the IJob equivalent of running a cloud-sql-proxy
/// sidecar. It advertises its address (this pod's IP + a port) as the <c>address</c> output, signals
/// readiness, then stays up serving until the engine tears it down once the rest of the run finishes.
/// Dependents bind its address with <c>BindInput("db_host", "sql-proxy", "address")</c>.
///
/// For a real proxy you'd use a container node instead:
/// <code>
/// .AddContainer("sql-proxy", "gcr.io/cloud-sql-connectors/cloud-sql-proxy:2.11.0", c => c
///     .WithArgs("--address=0.0.0.0", "--port=5432", "my-project:region:instance")
///     .ServicePort(5432).ReadyOnTcp(5432))
/// .AsService()
/// </code>
/// </summary>
public sealed class SqlProxyServiceJob : IJob
{
    public async Task RunAsync(IJobContext context)
    {
        const int port = 5432;
        var address = $"{context.PodIp}:{port}";

        context.Log($"Starting SQL proxy, listening on {address}");
        context.SetOutput("address", address);
        context.SetOutput("ip", context.PodIp);

        // Tell the DAG we're up: dependents start now, while this keeps running.
        context.SignalReady();
        context.Log("SQL proxy ready — dependents may connect.");

        try
        {
            // Serve until torn down (cancellation is the normal end of a service node's life).
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            context.Log("SQL proxy shutting down.");
        }
    }
}
