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
        if (line.StartsWith(WorkerProtocol.ProgressPrefix, StringComparison.Ordinal))
        {
            var rest = line[WorkerProtocol.ProgressPrefix.Length..].Trim();
            var space = rest.IndexOf(' ');
            var numText = space < 0 ? rest : rest[..space];
            if (!int.TryParse(numText, out var pct)) return;
            exec.Progress = Math.Clamp(pct, 0, 100);
            exec.ProgressMessage = space < 0 ? null : rest[(space + 1)..];
            await store.UpdateAsync(exec);
            return;
        }

        if (line.StartsWith(WorkerProtocol.ProgressBarPrefix, StringComparison.Ordinal))
        {
            var parts = line[WorkerProtocol.ProgressBarPrefix.Length..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 3
                || !int.TryParse(parts[0], out var barId)
                || !int.TryParse(parts[1], out var current)
                || !int.TryParse(parts[2], out var total) || total <= 0) return;
            // First bar output flips us into Running, like any other line.
            if (exec.Status is JobStatus.Starting or JobStatus.Enqueued)
            {
                exec.Status = JobStatus.Running;
                exec.StartedAt ??= DateTimeOffset.UtcNow;
            }

            var marker = $"{WorkerProtocol.ConsoleBarMarker} {barId} {Math.Clamp(current, 0, total)} {total}";
            var idPrefix = $"{WorkerProtocol.ConsoleBarMarker} {barId} ";
            lock (exec.Logs)
            {
                var idx = -1;
                for (var k = 0; k < exec.Logs.Count; k++)
                    if (exec.Logs[k].StartsWith(idPrefix, StringComparison.Ordinal)) { idx = k; break; }
                if (idx >= 0) exec.Logs[idx] = marker; // advance in place
                else exec.Logs.Add(marker);            // first tick fixes the bar's position
            }
            await store.UpdateAsync(exec);
            return;
        }

        if (line.StartsWith(WorkerProtocol.OutputPrefix, StringComparison.Ordinal))
        {
            var rest = line[WorkerProtocol.OutputPrefix.Length..].Trim();
            var space = rest.IndexOf(' ');
            if (space <= 0) return;
            var key = rest[..space];
            var value = rest[(space + 1)..];
            exec.Outputs[key] = value;
            await store.UpdateAsync(exec);
            return;
        }

        if (line.StartsWith(WorkerProtocol.ReadyPrefix, StringComparison.Ordinal))
        {
            if (exec.Status is JobStatus.Starting or JobStatus.Enqueued)
            {
                exec.Status = JobStatus.Running;
                exec.StartedAt ??= DateTimeOffset.UtcNow;
            }
            if (!exec.Ready)
            {
                exec.Ready = true;
                exec.ReadyAt = DateTimeOffset.UtcNow;
            }
            await store.UpdateAsync(exec);
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
