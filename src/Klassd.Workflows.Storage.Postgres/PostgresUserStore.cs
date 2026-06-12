using Klassd.Workflows.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Klassd.Workflows.Storage.Postgres;

/// <summary>Durable <see cref="IWorkflowsUserStore"/> on PostgreSQL (raw Npgsql).</summary>
public sealed class PostgresUserStore : IWorkflowsUserStore, IAsyncDisposable
{
    private const string Columns = "id, email, password_hash, provider, external_id, disabled";
    private readonly NpgsqlDataSource _db;

    public PostgresUserStore(string connectionString)
    {
        _db = NpgsqlDataSource.Create(connectionString);
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS workflow_users (
                id text PRIMARY KEY,
                email text NOT NULL UNIQUE,
                password_hash text NOT NULL,
                provider text NOT NULL,
                external_id text,
                disabled boolean NOT NULL DEFAULT false);
            CREATE INDEX IF NOT EXISTS ix_workflow_users_external ON workflow_users(provider, external_id);
            """;
        using var cmd = _db.CreateCommand(ddl);
        cmd.ExecuteNonQuery();
    }

    public async Task<WorkflowsUser?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"SELECT {Columns} FROM workflow_users WHERE email = @e");
        cmd.Parameters.AddWithValue("e", email);
        return await ReadOneAsync(cmd, ct);
    }

    public async Task<WorkflowsUser?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"SELECT {Columns} FROM workflow_users WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        return await ReadOneAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<WorkflowsUser>> GetAllAsync(CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"SELECT {Columns} FROM workflow_users ORDER BY email");
        var list = new List<WorkflowsUser>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Map(r));
        return list;
    }

    public async Task InsertAsync(WorkflowsUser user, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            $"INSERT INTO workflow_users ({Columns}) VALUES (@id, @e, @p, @pr, @ext, @d)");
        Bind(cmd, user);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<WorkflowsUser?> FindByExternalAsync(string provider, string externalId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"SELECT {Columns} FROM workflow_users WHERE provider = @pr AND external_id = @ext");
        cmd.Parameters.AddWithValue("pr", provider);
        cmd.Parameters.AddWithValue("ext", externalId);
        return await ReadOneAsync(cmd, ct);
    }

    public async Task UpdateAsync(WorkflowsUser user, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("""
            UPDATE workflow_users SET
              email = @e, password_hash = @p, provider = @pr, external_id = @ext, disabled = @d
            WHERE id = @id
            """);
        Bind(cmd, user);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void Bind(NpgsqlCommand cmd, WorkflowsUser user)
    {
        cmd.Parameters.AddWithValue("id", user.Id);
        cmd.Parameters.AddWithValue("e", user.Email);
        cmd.Parameters.AddWithValue("p", user.PasswordHash);
        cmd.Parameters.AddWithValue("pr", user.Provider);
        cmd.Parameters.Add(new NpgsqlParameter("ext", NpgsqlDbType.Text) { Value = (object?)user.ExternalId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("d", user.Disabled);
    }

    private static async Task<WorkflowsUser?> ReadOneAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    private static WorkflowsUser Map(NpgsqlDataReader r) => new()
    {
        Id = r.GetString(0),
        Email = r.GetString(1),
        PasswordHash = r.GetString(2),
        Provider = r.GetString(3),
        ExternalId = r.IsDBNull(4) ? null : r.GetString(4),
        Disabled = r.GetBoolean(5),
    };

    public ValueTask DisposeAsync() => _db.DisposeAsync();
}
