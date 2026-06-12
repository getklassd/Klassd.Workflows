namespace Klassd.Workflows.Abstractions;

/// <summary>
/// Persistence for dashboard users. An in-memory implementation ships by default; the
/// Postgres / MongoDB / SQLite storage adapters replace it with a durable one.
/// </summary>
public interface IWorkflowsUserStore
{
    /// <summary>Looks up a user by email (login + the SSO-link lookup). Null if none.</summary>
    Task<WorkflowsUser?> FindByEmailAsync(string email, CancellationToken ct = default);

    Task<WorkflowsUser?> GetByIdAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowsUser>> GetAllAsync(CancellationToken ct = default);

    Task InsertAsync(WorkflowsUser user, CancellationToken ct = default);

    /// <summary>Looks up a user by external (SSO) identity. Null if no account is linked.</summary>
    Task<WorkflowsUser?> FindByExternalAsync(string provider, string externalId, CancellationToken ct = default);

    /// <summary>Replaces an existing user's mutable fields (email, password hash, provider link, disabled).</summary>
    Task UpdateAsync(WorkflowsUser user, CancellationToken ct = default);
}
