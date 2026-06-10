using Klassd.Workflows.Core;

namespace Klassd.Workflows.Storage.Postgres;

public static class PostgresServiceCollectionExtensions
{
    /// <summary>Use PostgreSQL as the durable job/workflow store.</summary>
    public static WorkflowsBuilder UsePostgres(this WorkflowsBuilder builder, string connectionString)
    {
        var options = new PostgresJobStoreOptions { ConnectionString = connectionString };
        return builder.UseJobStore(_ => new PostgresJobStore(options));
    }
}
