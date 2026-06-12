using Klassd.Auth.Abstractions;
using Klassd.Auth.Data.MongoDb;
using Klassd.Workflows.Core;

namespace Klassd.Workflows.Storage.MongoDb;

public static class MongoServiceCollectionExtensions
{
    /// <summary>
    /// Use MongoDB as the durable job/workflow store. When Klassd.Workflows.Auth is registered, the
    /// matching Klassd.Auth Mongo user store is attached in the same database, replacing the default.
    /// </summary>
    public static WorkflowsBuilder UseMongo(this WorkflowsBuilder builder, string connectionString,
        string database = "klassd_workflows")
    {
        var options = new MongoJobStoreOptions { ConnectionString = connectionString, Database = database };

        var auth = builder.Services
            .LastOrDefault(d => d.ServiceType == typeof(IAuthBuilder))?.ImplementationInstance as IAuthBuilder;
        auth?.UseMongoDb(connectionString, database);

        return builder.UseJobStore(_ => new MongoJobStore(options));
    }
}
