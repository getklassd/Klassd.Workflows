using Klassd.Auth.Abstractions;
using Klassd.Auth.Data.Postgres;
using Klassd.Workflows.Core;

namespace Klassd.Workflows.Storage.Postgres;

public static class PostgresServiceCollectionExtensions
{
    /// <summary>
    /// Use PostgreSQL as the durable job/workflow store. When Klassd.Workflows.Auth is registered, the
    /// matching Klassd.Auth Postgres user store is attached on the same connection, replacing the default.
    /// </summary>
    public static WorkflowsBuilder UsePostgres(this WorkflowsBuilder builder, string connectionString)
    {
        var options = new PostgresJobStoreOptions { ConnectionString = connectionString };

        var auth = builder.Services
            .LastOrDefault(d => d.ServiceType == typeof(IAuthBuilder))?.ImplementationInstance as IAuthBuilder;
        auth?.UsePostgres(connectionString);

        return builder.UseJobStore(_ => new PostgresJobStore(options));
    }
}
