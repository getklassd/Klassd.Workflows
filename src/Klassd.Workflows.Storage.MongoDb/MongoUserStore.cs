using Klassd.Workflows.Abstractions;
using MongoDB.Driver;

namespace Klassd.Workflows.Storage.MongoDb;

/// <summary>Durable <see cref="IWorkflowsUserStore"/> on MongoDB.</summary>
public sealed class MongoUserStore : IWorkflowsUserStore
{
    private static readonly FilterDefinitionBuilder<WorkflowsUser> F = Builders<WorkflowsUser>.Filter;
    private readonly IMongoCollection<WorkflowsUser> _users;

    public MongoUserStore(MongoJobStoreOptions options)
    {
        var db = new MongoClient(options.ConnectionString).GetDatabase(options.Database);
        _users = db.GetCollection<WorkflowsUser>("workflow_users");
        _users.Indexes.CreateMany(
        [
            new CreateIndexModel<WorkflowsUser>(Builders<WorkflowsUser>.IndexKeys.Ascending(x => x.Email),
                new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<WorkflowsUser>(Builders<WorkflowsUser>.IndexKeys
                .Ascending(x => x.Provider).Ascending(x => x.ExternalId)),
        ]);
    }

    public async Task<WorkflowsUser?> FindByEmailAsync(string email, CancellationToken ct = default) =>
        await _users.Find(F.Eq(x => x.Email, email)).FirstOrDefaultAsync(ct);

    public async Task<WorkflowsUser?> GetByIdAsync(string id, CancellationToken ct = default) =>
        await _users.Find(F.Eq(x => x.Id, id)).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<WorkflowsUser>> GetAllAsync(CancellationToken ct = default) =>
        await _users.Find(F.Empty).SortBy(x => x.Email).ToListAsync(ct);

    public Task InsertAsync(WorkflowsUser user, CancellationToken ct = default) =>
        _users.InsertOneAsync(user, cancellationToken: ct);

    public async Task<WorkflowsUser?> FindByExternalAsync(string provider, string externalId, CancellationToken ct = default) =>
        await _users.Find(F.Eq(x => x.Provider, provider) & F.Eq(x => x.ExternalId, externalId)).FirstOrDefaultAsync(ct);

    public Task UpdateAsync(WorkflowsUser user, CancellationToken ct = default) =>
        _users.ReplaceOneAsync(F.Eq(x => x.Id, user.Id), user, cancellationToken: ct);
}
