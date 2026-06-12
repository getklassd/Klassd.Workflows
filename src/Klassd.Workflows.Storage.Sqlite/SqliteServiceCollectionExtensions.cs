using Klassd.Workflows.Abstractions;
using Klassd.Workflows.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Klassd.Workflows.Storage.Sqlite;

public static class SqliteServiceCollectionExtensions
{
    /// <summary>
    /// Use SQLite as the durable job/workflow store — a single file database, zero infrastructure.
    /// Also registers a durable user store (used by Klassd.Workflows.Auth) in the same file,
    /// replacing the in-memory default.
    /// </summary>
    /// <param name="connectionString">e.g. <c>Data Source=klassd-workflows.db</c>.</param>
    public static WorkflowsBuilder UseSqlite(this WorkflowsBuilder builder, string connectionString)
    {
        var options = new SqliteJobStoreOptions { ConnectionString = connectionString };
        builder.Services.Replace(ServiceDescriptor.Singleton<IWorkflowsUserStore>(_ => new SqliteUserStore(connectionString)));
        return builder.UseJobStore(_ => new SqliteJobStore(options));
    }
}
