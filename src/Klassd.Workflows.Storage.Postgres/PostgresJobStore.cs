using System.Text.Json;
using Klassd.Workflows.Core.Abstractions;
using Klassd.Workflows.Core.Model;
using Npgsql;
using NpgsqlTypes;

namespace Klassd.Workflows.Storage.Postgres;

public sealed class PostgresJobStoreOptions
{
    public string ConnectionString { get; set; } = "";
}

/// <summary>
/// Durable <see cref="IJobStore"/> on PostgreSQL. Executions, recurring entries
/// and workflow runs are stored as <c>jsonb</c> documents; logs are an
/// append-only table so high-frequency log writes never rewrite the document.
/// Change events are raised in-process (single scheduler instance); for multiple
/// scheduler replicas, bridge these to LISTEN/NOTIFY.
/// </summary>
public sealed class PostgresJobStore : IJobStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _db;

    public event Action<JobExecution>? ExecutionChanged;
    public event Action<WorkflowRun>? WorkflowChanged;

    public PostgresJobStore(PostgresJobStoreOptions options)
    {
        _db = NpgsqlDataSource.Create(options.ConnectionString);
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS executions (
                id text PRIMARY KEY, data jsonb NOT NULL,
                created_at timestamptz NOT NULL, workflow_run_id text);
            CREATE TABLE IF NOT EXISTS job_logs (
                seq bigserial PRIMARY KEY, exec_id text NOT NULL, line text NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_job_logs_exec ON job_logs(exec_id, seq);
            CREATE TABLE IF NOT EXISTS recurring (id text PRIMARY KEY, data jsonb NOT NULL);
            CREATE TABLE IF NOT EXISTS workflow_runs (
                id text PRIMARY KEY, data jsonb NOT NULL, created_at timestamptz NOT NULL);
            """;
        using var cmd = _db.CreateCommand(ddl);
        cmd.ExecuteNonQuery();
    }

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

    private async Task PersistExecutionAsync(JobExecution exec)
    {
        const string sql = """
            INSERT INTO executions (id, data, created_at, workflow_run_id)
            VALUES (@id, @data, @created, @wf)
            ON CONFLICT (id) DO UPDATE
              SET data = excluded.data, workflow_run_id = excluded.workflow_run_id;
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", exec.Id);
        cmd.Parameters.Add(new NpgsqlParameter("data", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(exec, Json) });
        cmd.Parameters.Add(new NpgsqlParameter("created", NpgsqlDbType.TimestampTz) { Value = exec.CreatedAt.UtcDateTime });
        cmd.Parameters.AddWithValue("wf", (object?)exec.WorkflowRunId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<JobExecution?> GetAsync(string id)
    {
        var exec = await ReadExecutionAsync(id);
        if (exec is null) return null;

        await using var cmd = _db.CreateCommand("SELECT line FROM job_logs WHERE exec_id = @id ORDER BY seq");
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) exec.Logs.Add(r.GetString(0));
        return exec;
    }

    private async Task<JobExecution?> ReadExecutionAsync(string id)
    {
        await using var cmd = _db.CreateCommand("SELECT data FROM executions WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        var data = (string?)await cmd.ExecuteScalarAsync();
        return data is null ? null : JsonSerializer.Deserialize<JobExecution>(data, Json);
    }

    public async Task<IReadOnlyList<JobExecution>> ListAsync(int limit = 200)
    {
        await using var cmd = _db.CreateCommand("SELECT data FROM executions ORDER BY created_at DESC LIMIT @n");
        cmd.Parameters.AddWithValue("n", limit);
        var list = new List<JobExecution>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var e = JsonSerializer.Deserialize<JobExecution>(r.GetString(0), Json);
            if (e is not null) list.Add(e);
        }
        return list;
    }

    public async Task AppendLogAsync(string id, string line)
    {
        await using (var cmd = _db.CreateCommand("INSERT INTO job_logs (exec_id, line) VALUES (@id, @line)"))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("line", line);
            await cmd.ExecuteNonQueryAsync();
        }
        var exec = await ReadExecutionAsync(id);
        if (exec is not null) ExecutionChanged?.Invoke(exec);
    }

    public async Task UpsertRecurringAsync(RecurringJob job)
    {
        const string sql = "INSERT INTO recurring (id, data) VALUES (@id, @data) ON CONFLICT (id) DO UPDATE SET data = excluded.data;";
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", job.Id);
        cmd.Parameters.Add(new NpgsqlParameter("data", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(job, Json) });
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<RecurringJob>> ListRecurringAsync()
    {
        await using var cmd = _db.CreateCommand("SELECT data FROM recurring ORDER BY id");
        var list = new List<RecurringJob>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var j = JsonSerializer.Deserialize<RecurringJob>(r.GetString(0), Json);
            if (j is not null) list.Add(j);
        }
        return list;
    }

    public async Task RemoveRecurringAsync(string id)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM recurring WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SaveWorkflowRunAsync(WorkflowRun run)
    {
        const string sql = """
            INSERT INTO workflow_runs (id, data, created_at) VALUES (@id, @data, @created)
            ON CONFLICT (id) DO UPDATE SET data = excluded.data;
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", run.Id);
        cmd.Parameters.Add(new NpgsqlParameter("data", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(run, Json) });
        cmd.Parameters.Add(new NpgsqlParameter("created", NpgsqlDbType.TimestampTz) { Value = run.CreatedAt.UtcDateTime });
        await cmd.ExecuteNonQueryAsync();
        WorkflowChanged?.Invoke(run);
    }

    public async Task<WorkflowRun?> GetWorkflowRunAsync(string id)
    {
        await using var cmd = _db.CreateCommand("SELECT data FROM workflow_runs WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        var data = (string?)await cmd.ExecuteScalarAsync();
        return data is null ? null : JsonSerializer.Deserialize<WorkflowRun>(data, Json);
    }

    public async Task<IReadOnlyList<WorkflowRun>> ListWorkflowRunsAsync(int limit = 100)
    {
        await using var cmd = _db.CreateCommand("SELECT data FROM workflow_runs ORDER BY created_at DESC LIMIT @n");
        cmd.Parameters.AddWithValue("n", limit);
        var list = new List<WorkflowRun>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var run = JsonSerializer.Deserialize<WorkflowRun>(r.GetString(0), Json);
            if (run is not null) list.Add(run);
        }
        return list;
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
