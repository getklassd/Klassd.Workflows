using System.Collections.Concurrent;
using Klassd.Workflows.Abstractions;

namespace Klassd.Workflows.Auth;

/// <summary>
/// Default in-process user store. Fine for single-instance/dev use; lost on restart and not shared
/// across replicas — use a durable adapter (UsePostgres / UseMongo / UseSqlite) in production.
/// </summary>
public sealed class InMemoryWorkflowsUserStore : IWorkflowsUserStore
{
    private readonly ConcurrentDictionary<string, WorkflowsUser> _users = new();

    public Task<WorkflowsUser?> FindByEmailAsync(string email, CancellationToken ct = default) =>
        Task.FromResult(_users.Values.FirstOrDefault(
            u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase)));

    public Task<WorkflowsUser?> GetByIdAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_users.GetValueOrDefault(id));

    public Task<IReadOnlyList<WorkflowsUser>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WorkflowsUser>>(_users.Values
            .OrderBy(u => u.Email, StringComparer.OrdinalIgnoreCase).ToList());

    public Task InsertAsync(WorkflowsUser user, CancellationToken ct = default)
    {
        _users[user.Id] = user;
        return Task.CompletedTask;
    }

    public Task<WorkflowsUser?> FindByExternalAsync(string provider, string externalId, CancellationToken ct = default) =>
        Task.FromResult(_users.Values.FirstOrDefault(u => u.Provider == provider && u.ExternalId == externalId));

    public Task UpdateAsync(WorkflowsUser user, CancellationToken ct = default)
    {
        _users[user.Id] = user;
        return Task.CompletedTask;
    }
}
