using Klassd.Workflows.Core;

namespace Klassd.Workflows.Storage.MongoDb;

public static class MongoServiceCollectionExtensions
{
    /// <summary>Use MongoDB as the durable job/workflow store.</summary>
    public static WorkflowsBuilder UseMongo(this WorkflowsBuilder builder, string connectionString,
        string database = "klassd_workflows")
    {
        var options = new MongoJobStoreOptions { ConnectionString = connectionString, Database = database };
        return builder.UseJobStore(_ => new MongoJobStore(options));
    }
}
