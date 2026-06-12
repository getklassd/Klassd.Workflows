using Klassd.Workflows.Abstractions;
using Microsoft.Data.Sqlite;

namespace Klassd.Workflows.Storage.Sqlite;

/// <summary>Durable <see cref="IWorkflowsUserStore"/> on SQLite (raw Microsoft.Data.Sqlite).</summary>
public sealed class SqliteUserStore : IWorkflowsUserStore
{
    private const string Columns = "id, email, password_hash, provider, external_id, disabled";
    private readonly string _connectionString;

    public SqliteUserStore(string connectionString)
    {
        _connectionString = connectionString;
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
            CREATE TABLE IF NOT EXISTS workflow_users (
                id TEXT PRIMARY KEY,
                email TEXT NOT NULL UNIQUE,
                password_hash TEXT NOT NULL,
                provider TEXT NOT NULL,
                external_id TEXT,
                disabled INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_workflow_users_external ON workflow_users(provider, external_id);
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task<WorkflowsUser?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM workflow_users WHERE email = @e";
        cmd.Parameters.AddWithValue("@e", email);
        return await ReadOneAsync(cmd, ct);
    }

    public async Task<WorkflowsUser?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM workflow_users WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return await ReadOneAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<WorkflowsUser>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM workflow_users ORDER BY email";
        var list = new List<WorkflowsUser>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Map(r));
        return list;
    }

    public async Task InsertAsync(WorkflowsUser user, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"INSERT INTO workflow_users ({Columns}) VALUES (@id, @e, @p, @pr, @ext, @d)";
        Bind(cmd, user);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<WorkflowsUser?> FindByExternalAsync(string provider, string externalId, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM workflow_users WHERE provider = @pr AND external_id = @ext";
        cmd.Parameters.AddWithValue("@pr", provider);
        cmd.Parameters.AddWithValue("@ext", externalId);
        return await ReadOneAsync(cmd, ct);
    }

    public async Task UpdateAsync(WorkflowsUser user, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE workflow_users SET
              email = @e, password_hash = @p, provider = @pr, external_id = @ext, disabled = @d
            WHERE id = @id
            """;
        Bind(cmd, user);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void Bind(SqliteCommand cmd, WorkflowsUser user)
    {
        cmd.Parameters.AddWithValue("@id", user.Id);
        cmd.Parameters.AddWithValue("@e", user.Email);
        cmd.Parameters.AddWithValue("@p", user.PasswordHash);
        cmd.Parameters.AddWithValue("@pr", user.Provider);
        cmd.Parameters.AddWithValue("@ext", (object?)user.ExternalId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@d", user.Disabled ? 1 : 0);
    }

    private static async Task<WorkflowsUser?> ReadOneAsync(SqliteCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    private static WorkflowsUser Map(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Email = r.GetString(1),
        PasswordHash = r.GetString(2),
        Provider = r.GetString(3),
        ExternalId = r.IsDBNull(4) ? null : r.GetString(4),
        Disabled = r.GetBoolean(5),
    };
}
