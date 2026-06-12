using Klassd.Auth.Abstractions;
using Klassd.Auth.Data.Sqlite;
using Klassd.Workflows.Core;

namespace Klassd.Workflows.Storage.Sqlite;

public static class SqliteServiceCollectionExtensions
{
    /// <summary>
    /// Use SQLite as the durable job/workflow store — a single file database, zero infrastructure.
    /// When Klassd.Workflows.Auth is registered, the matching Klassd.Auth SQLite user store is attached
    /// to the same file, replacing the default.
    /// </summary>
    /// <param name="connectionString">e.g. <c>Data Source=klassd-workflows.db</c>.</param>
    public static WorkflowsBuilder UseSqlite(this WorkflowsBuilder builder, string connectionString)
    {
        var options = new SqliteJobStoreOptions { ConnectionString = connectionString };

        // Attach the auth user store (and friends) to the stashed auth builder, if auth is wired.
        var auth = builder.Services
            .LastOrDefault(d => d.ServiceType == typeof(IAuthBuilder))?.ImplementationInstance as IAuthBuilder;
        auth?.UseSqlite(connectionString);

        return builder.UseJobStore(_ => new SqliteJobStore(options));
    }
}
