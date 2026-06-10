using Klassd.Workflows.Abstractions;
using Klassd.Workflows.Core.Abstractions;
using Klassd.Workflows.Core.Model;

namespace Klassd.Workflows.Core.Execution;

/// <summary>
/// Parses a single worker stdout line (pod log or local process output) using
/// <see cref="WorkerProtocol"/> and applies the change to the store. Shared by
/// every executor so log/progress/state handling is identical everywhere.
/// </summary>
public static class WorkerOutputProcessor
{
    public static async Task ProcessLineAsync(IJobStore store, JobExecution exec, string line)
    {
        if (line is null) return;

        if (line.StartsWith(WorkerProtocol.ProgressPrefix, StringComparison.Ordinal))
        {
            var rest = line[WorkerProtocol.ProgressPrefix.Length..].Trim();
            var space = rest.IndexOf(' ');
            var numText = space < 0 ? rest : rest[..space];
            if (int.TryParse(numText, out var pct))
            {
                exec.Progress = Math.Clamp(pct, 0, 100);
                exec.ProgressMessage = space < 0 ? null : rest[(space + 1)..];
                await store.UpdateAsync(exec);
            }
            return;
        }

        if (line.StartsWith(WorkerProtocol.OutputPrefix, StringComparison.Ordinal))
        {
            var rest = line[WorkerProtocol.OutputPrefix.Length..].Trim();
            var space = rest.IndexOf(' ');
            if (space > 0)
            {
                var key = rest[..space];
                var value = rest[(space + 1)..];
                exec.Outputs[key] = value;
                await store.UpdateAsync(exec);
            }
            return;
        }

        if (line.StartsWith(WorkerProtocol.StatePrefix, StringComparison.Ordinal))
        {
            var rest = line[WorkerProtocol.StatePrefix.Length..].Trim();
            var space = rest.IndexOf(' ');
            var state = space < 0 ? rest : rest[..space];
            var message = space < 0 ? null : rest[(space + 1)..];

            if (state.Equals("Succeeded", StringComparison.OrdinalIgnoreCase))
            {
                exec.Status = JobStatus.Succeeded;
                exec.Progress = 100;
            }
            else if (state.Equals("Failed", StringComparison.OrdinalIgnoreCase))
            {
                exec.Status = JobStatus.Failed;
                exec.Error = message;
            }
            exec.FinishedAt = DateTimeOffset.UtcNow;
            await store.UpdateAsync(exec);
            return;
        }

        if (line.StartsWith(WorkerProtocol.LogPrefix, StringComparison.Ordinal))
        {
            line = line[WorkerProtocol.LogPrefix.Length..].TrimStart();
        }

        // First real output flips us into Running.
        if (exec.Status is JobStatus.Starting or JobStatus.Enqueued)
        {
            exec.Status = JobStatus.Running;
            exec.StartedAt ??= DateTimeOffset.UtcNow;
            await store.UpdateAsync(exec);
        }

        await store.AppendLogAsync(exec.Id, $"[{DateTimeOffset.Now:HH:mm:ss}] {line}");
    }
}
