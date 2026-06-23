using System.Text.Json;
using Klassd.Workflows.Core.Abstractions;
using Klassd.Workflows.Core.Model;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Klassd.Workflows.Storage.MongoDb;

public sealed class MongoJobStoreOptions
{
    public string ConnectionString { get; set; } = "";
    public string Database { get; set; } = "klassd_workflows";
}

/// <summary>
/// Durable <see cref="IJobStore"/> on MongoDB. Each execution / recurring entry /
/// workflow run is stored as a document carrying the JSON payload of the model;
/// logs live in their own append-only collection. Change events are raised
/// in-process; for multiple scheduler replicas, bridge them to change streams.
/// </summary>
public sealed class MongoJobStore : IJobStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IMongoCollection<BsonDocument> _executions;
    private readonly IMongoCollection<BsonDocument> _logs;
    private readonly IMongoCollection<BsonDocument> _recurring;
    private readonly IMongoCollection<BsonDocument> _runs;

    public event Action<JobExecution>? ExecutionChanged;
    public event Action<WorkflowRun>? WorkflowChanged;

    public MongoJobStore(MongoJobStoreOptions options)
    {
        var db = new MongoClient(options.ConnectionString).GetDatabase(options.Database);
        _executions = db.GetCollection<BsonDocument>("executions");
        _logs = db.GetCollection<BsonDocument>("job_logs");
        _recurring = db.GetCollection<BsonDocument>("recurring");
        _runs = db.GetCollection<BsonDocument>("workflow_runs");
    }

    private static FilterDefinition<BsonDocument> ById(string id) =>
        Builders<BsonDocument>.Filter.Eq("_id", id);

    public async Task<JobExecution> CreateAsync(JobDescriptor descriptor, string executorName)
    {
        var exec = new JobExecution
        {
            JobName = descriptor.JobName,
            JobTypeName = descriptor.JobTypeName,
            Arguments = new Dictionary<string, string>(descriptor.Arguments),
            Tenant = descriptor.Tenant,
            ExecutorName = executorName,
            Status = JobStatus.Enqueued
        };
        await PersistExecutionAsync(exec);
        ExecutionChanged?.Invoke(exec);
        return exec;
    }

    public async Task UpdateAsync(JobExecution execution)
    {
        await PersistExecutionAsync(execution);
        ExecutionChanged?.Invoke(execution);
    }

    private Task PersistExecutionAsync(JobExecution exec)
    {
        var doc = new BsonDocument
        {
            { "_id", exec.Id },
            { "_created", exec.CreatedAt.UtcDateTime },
            { "payload", JsonSerializer.Serialize(exec, Json) }
        };
        return _executions.ReplaceOneAsync(ById(exec.Id), doc, new ReplaceOptions { IsUpsert = true });
    }

    public async Task<JobExecution?> GetAsync(string id)
    {
        var doc = await _executions.Find(ById(id)).FirstOrDefaultAsync();
        if (doc is null) return null;
        var exec = JsonSerializer.Deserialize<JobExecution>(doc["payload"].AsString, Json)!;

        var logs = await _logs.Find(Builders<BsonDocument>.Filter.Eq("exec_id", id))
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync();
        foreach (var l in logs) exec.Logs.Add(l["line"].AsString);
        return exec;
    }

    private async Task<JobExecution?> ReadExecutionAsync(string id)
    {
        var doc = await _executions.Find(ById(id)).FirstOrDefaultAsync();
        return doc is null ? null : JsonSerializer.Deserialize<JobExecution>(doc["payload"].AsString, Json);
    }

    public async Task<IReadOnlyList<JobExecution>> ListAsync(int limit = 200)
    {
        var docs = await _executions.Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Descending("_created")).Limit(limit).ToListAsync();
        return docs.Select(d => JsonSerializer.Deserialize<JobExecution>(d["payload"].AsString, Json)!).ToList();
    }

    public async Task AppendLogAsync(string id, string line)
    {
        await _logs.InsertOneAsync(new BsonDocument { { "exec_id", id }, { "line", line } });
        var exec = await ReadExecutionAsync(id);
        if (exec is not null) ExecutionChanged?.Invoke(exec);
    }

    public Task UpsertRecurringAsync(RecurringJob job)
    {
        var doc = new BsonDocument { { "_id", job.Id }, { "payload", JsonSerializer.Serialize(job, Json) } };
        return _recurring.ReplaceOneAsync(ById(job.Id), doc, new ReplaceOptions { IsUpsert = true });
    }

    public async Task<IReadOnlyList<RecurringJob>> ListRecurringAsync()
    {
        var docs = await _recurring.Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync();
        return docs.Select(d => JsonSerializer.Deserialize<RecurringJob>(d["payload"].AsString, Json)!).ToList();
    }

    public Task RemoveRecurringAsync(string id) => _recurring.DeleteOneAsync(ById(id));

    public async Task SaveWorkflowRunAsync(WorkflowRun run)
    {
        var doc = new BsonDocument
        {
            { "_id", run.Id },
            { "_created", run.CreatedAt.UtcDateTime },
            { "payload", JsonSerializer.Serialize(run, Json) }
        };
        await _runs.ReplaceOneAsync(ById(run.Id), doc, new ReplaceOptions { IsUpsert = true });
        WorkflowChanged?.Invoke(run);
    }

    public async Task<WorkflowRun?> GetWorkflowRunAsync(string id)
    {
        var doc = await _runs.Find(ById(id)).FirstOrDefaultAsync();
        return doc is null ? null : JsonSerializer.Deserialize<WorkflowRun>(doc["payload"].AsString, Json);
    }

    public async Task<IReadOnlyList<WorkflowRun>> ListWorkflowRunsAsync(int limit = 100)
    {
        var docs = await _runs.Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Descending("_created")).Limit(limit).ToListAsync();
        return docs.Select(d => JsonSerializer.Deserialize<WorkflowRun>(d["payload"].AsString, Json)!).ToList();
    }
}
