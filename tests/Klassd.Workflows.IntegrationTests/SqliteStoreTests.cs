using Klassd.Workflows.Abstractions;
using Klassd.Workflows.Core.Model;
using Klassd.Workflows.Storage.Sqlite;

namespace Klassd.Workflows.IntegrationTests;

/// <summary>SQLite adapter round-trips (file DB, no external infrastructure).</summary>
public class SqliteStoreTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"klassd-test-{Guid.NewGuid():n}.db");

    private static void Cleanup(string db)
    {
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(db)!, Path.GetFileName(db) + "*"))
            try { File.Delete(f); } catch { /* best effort */ }
    }

    [Test]
    public async Task Job_store_persists_executions_logs_recurring_and_runs()
    {
        var db = TempDb();
        try
        {
            var store = new SqliteJobStore(new SqliteJobStoreOptions { ConnectionString = $"Data Source={db}" });

            var exec = await store.CreateAsync(new JobDescriptor("job", "Some.Type", new()), "local");
            await store.AppendLogAsync(exec.Id, "hello");
            exec.Status = JobStatus.Succeeded;
            await store.UpdateAsync(exec);

            var fetched = await store.GetAsync(exec.Id);
            await Assert.That(fetched!.Status).IsEqualTo(JobStatus.Succeeded);
            await Assert.That(fetched.Logs).Contains("hello");
            await Assert.That((await store.ListAsync()).Any(e => e.Id == exec.Id)).IsTrue();

            await store.UpsertRecurringAsync(new RecurringJob { Id = "r1", JobTypeName = "T", Cron = "* * * * *" });
            await Assert.That((await store.ListRecurringAsync()).Any(r => r.Id == "r1")).IsTrue();
            await store.RemoveRecurringAsync("r1");
            await Assert.That((await store.ListRecurringAsync()).Any(r => r.Id == "r1")).IsFalse();

            var run = new WorkflowRun { DefinitionName = "wf" };
            run.Nodes.Add(new NodeRun { Name = "n", JobTypeName = "T" });
            await store.SaveWorkflowRunAsync(run);
            var gotRun = await store.GetWorkflowRunAsync(run.Id);
            await Assert.That(gotRun!.DefinitionName).IsEqualTo("wf");
            await Assert.That(gotRun.Nodes.Count).IsEqualTo(1);
        }
        finally { Cleanup(db); }
    }
}
