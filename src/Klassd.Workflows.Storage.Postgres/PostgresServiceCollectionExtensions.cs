using Klassd.Workflows.Abstractions;
using Klassd.Workflows.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Klassd.Workflows.Storage.Postgres;

public static class PostgresServiceCollectionExtensions
{
    /// <summary>
    /// Use PostgreSQL as the durable job/workflow store. Also registers a durable user store
    /// (used by Klassd.Workflows.Auth) on the same connection, replacing the in-memory default.
    /// </summary>
    public static WorkflowsBuilder UsePostgres(this WorkflowsBuilder builder, string connectionString)
    {
        var options = new PostgresJobStoreOptions { ConnectionString = connectionString };
        builder.Services.Replace(ServiceDescriptor.Singleton<IWorkflowsUserStore>(_ => new PostgresUserStore(connectionString)));
        return builder.UseJobStore(_ => new PostgresJobStore(options));
    }
}
