using Klassd.Workflows.Abstractions;
using Klassd.Workflows.Core.Execution;
using Klassd.Workflows.Core.Model;
using Klassd.Workflows.Core.Storage;

namespace Klassd.Workflows.IntegrationTests;

public class WorkerOutputProcessorTests
{
    private static async Task<(InMemoryJobStore store, JobExecution exec)> NewExecAsync()
    {
        var store = new InMemoryJobStore();
        var exec = await store.CreateAsync(new JobDescriptor("j", "T", new()), "local");
        return (store, exec);
    }

    [Test]
    public async Task Ready_line_marks_ready_and_running()
    {
        var (store, exec) = await NewExecAsync();
        await WorkerOutputProcessor.ProcessLineAsync(store, exec, WorkerProtocol.ReadyPrefix);

        await Assert.That(exec.Ready).IsTrue();
        await Assert.That(exec.ReadyAt).IsNotNull();
        await Assert.That(exec.Status).IsEqualTo(JobStatus.Running);
    }

    [Test]
    public async Task Output_line_captures_key_and_value()
    {
        var (store, exec) = await NewExecAsync();
        await WorkerOutputProcessor.ProcessLineAsync(store, exec, $"{WorkerProtocol.OutputPrefix} address 1.2.3.4:5432");
        await Assert.That(exec.Outputs["address"]).IsEqualTo("1.2.3.4:5432");
    }

    [Test]
    public async Task State_line_sets_terminal_status()
    {
        var (store, exec) = await NewExecAsync();
        await WorkerOutputProcessor.ProcessLineAsync(store, exec, $"{WorkerProtocol.StatePrefix} Succeeded");
        await Assert.That(exec.Status).IsEqualTo(JobStatus.Succeeded);
        await Assert.That(exec.Progress).IsEqualTo(100);
    }
}
