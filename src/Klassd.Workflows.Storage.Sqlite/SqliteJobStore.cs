using System.Text.Json;
using Klassd.Workflows.Core.Abstractions;
using Klassd.Workflows.Core.Model;
using Microsoft.Data.Sqlite;

namespace Klassd.Workflows.Storage.Sqlite;

public sealed class SqliteJobStoreOptions
{
    /// <summary>ADO connection string, e.g. <c>Data Source=klassd-workflows.db</c>.</summary>
    public string ConnectionString { get; set; } = "";
}

/// <summary>
/// Durable <see cref="IJobStore"/> on SQLite (raw Microsoft.Data.Sqlite). Executions, recurring
/// entries and workflow runs are stored as JSON in TEXT columns; logs are an append-only table so
/// high-frequency writes never rewrite the document. Change events are raised in-process (single
/// scheduler instance). A new connection is opened per operation (the provider pools file handles).
/// </summary>
public sealed class SqliteJobStore : IJobStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;

    public event Action<JobExecution>? ExecutionChanged;
    public event Action<WorkflowRun>? WorkflowChanged;

    public SqliteJobStore(SqliteJobStoreOptions options)
    {
        _connectionString = options.ConnectionString;
        EnsureSchema();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void EnsureSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS executions (
                id TEXT PRIMARY KEY, data TEXT NOT NULL,
                created_at INTEGER NOT NULL, workflow_run_id TEXT);
            CREATE TABLE IF NOT EXISTS job_logs (
                seq INTEGER PRIMARY KEY AUTOINCREMENT, exec_id TEXT NOT NULL, line TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_job_logs_exec ON job_logs(exec_id, seq);
            CREATE TABLE IF NOT EXISTS recurring (id TEXT PRIMARY KEY, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS workflow_runs (
                id TEXT PRIMARY KEY, data TEXT NOT NULL, created_at INTEGER NOT NULL);
            """;
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
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO executions (id, data, created_at, workflow_run_id)
            VALUES (@id, @data, @created, @wf)
            ON CONFLICT (id) DO UPDATE
              SET data = excluded.data, workflow_run_id = excluded.workflow_run_id;
            """;
        cmd.Parameters.AddWithValue("@id", exec.Id);
        cmd.Parameters.AddWithValue("@data", JsonSerializer.Serialize(exec, Json));
        cmd.Parameters.AddWithValue("@created", exec.CreatedAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("@wf", (object?)exec.WorkflowRunId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<JobExecution?> GetAsync(string id)
    {
        await using var conn = Open();
        var exec = await ReadExecutionAsync(conn, id);
        if (exec is null) return null;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT line FROM job_logs WHERE exec_id = @id ORDER BY seq";
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) exec.Logs.Add(r.GetString(0));
        return exec;
    }

    private static async Task<JobExecution?> ReadExecutionAsync(SqliteConnection conn, string id)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM executions WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        var data = (string?)await cmd.ExecuteScalarAsync();
        return data is null ? null : JsonSerializer.Deserialize<JobExecution>(data, Json);
    }

    public async Task<IReadOnlyList<JobExecution>> ListAsync(int limit = 200)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM executions ORDER BY created_at DESC LIMIT @n";
        cmd.Parameters.AddWithValue("@n", limit);
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
        await using var conn = Open();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO job_logs (exec_id, line) VALUES (@id, @line)";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@line", line);
            await cmd.ExecuteNonQueryAsync();
        }
        var exec = await ReadExecutionAsync(conn, id);
        if (exec is not null) ExecutionChanged?.Invoke(exec);
    }

    public async Task UpsertRecurringAsync(RecurringJob job)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO recurring (id, data) VALUES (@id, @data) ON CONFLICT (id) DO UPDATE SET data = excluded.data;";
        cmd.Parameters.AddWithValue("@id", job.Id);
        cmd.Parameters.AddWithValue("@data", JsonSerializer.Serialize(job, Json));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<RecurringJob>> ListRecurringAsync()
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM recurring ORDER BY id";
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
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM recurring WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SaveWorkflowRunAsync(WorkflowRun run)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO workflow_runs (id, data, created_at) VALUES (@id, @data, @created)
            ON CONFLICT (id) DO UPDATE SET data = excluded.data;
            """;
        cmd.Parameters.AddWithValue("@id", run.Id);
        cmd.Parameters.AddWithValue("@data", JsonSerializer.Serialize(run, Json));
        cmd.Parameters.AddWithValue("@created", run.CreatedAt.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync();
        WorkflowChanged?.Invoke(run);
    }

    public async Task<WorkflowRun?> GetWorkflowRunAsync(string id)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM workflow_runs WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        var data = (string?)await cmd.ExecuteScalarAsync();
        return data is null ? null : JsonSerializer.Deserialize<WorkflowRun>(data, Json);
    }

    public async Task<IReadOnlyList<WorkflowRun>> ListWorkflowRunsAsync(int limit = 100)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM workflow_runs ORDER BY created_at DESC LIMIT @n";
        cmd.Parameters.AddWithValue("@n", limit);
        var list = new List<WorkflowRun>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var run = JsonSerializer.Deserialize<WorkflowRun>(r.GetString(0), Json);
            if (run is not null) list.Add(run);
        }
        return list;
    }
}
