using Klassd.Workflows.Abstractions;
using Klassd.Workflows.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Klassd.Workflows.Storage.MongoDb;

public static class MongoServiceCollectionExtensions
{
    /// <summary>
    /// Use MongoDB as the durable job/workflow store. Also registers a durable user store
    /// (used by Klassd.Workflows.Auth) in the same database, replacing the in-memory default.
    /// </summary>
    public static WorkflowsBuilder UseMongo(this WorkflowsBuilder builder, string connectionString,
        string database = "klassd_workflows")
    {
        var options = new MongoJobStoreOptions { ConnectionString = connectionString, Database = database };
        builder.Services.Replace(ServiceDescriptor.Singleton<IWorkflowsUserStore>(_ => new MongoUserStore(options)));
        return builder.UseJobStore(_ => new MongoJobStore(options));
    }
}
