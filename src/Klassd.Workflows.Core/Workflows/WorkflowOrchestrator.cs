using System.Collections.Concurrent;
using System.Text.Json;
using Klassd.Workflows.Core.Abstractions;
using Klassd.Workflows.Core.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Klassd.Workflows.Core.Workflows;

/// <summary>
/// Drives DAG runs. Roots start immediately; a node starts once every dependency
/// is satisfied (succeeded or benignly omitted), fanning out into one execution
/// per item and retrying failed executions up to the node's limit. A node whose
/// <c>when</c> condition is false is omitted. Each execution runs through the
/// normal <see cref="IJobExecutor"/>, so nodes are pods/processes with their own
/// live console.
///
/// Execution state changes (from the store) re-evaluate the owning run; all
/// evaluation for a run is serialized by a per-run lock.
/// </summary>
public sealed class WorkflowOrchestrator : IWorkflowOrchestrator, IHostedService, IDisposable
{
    private readonly IJobStore _store;
    private readonly IJobExecutor _executor;
    private readonly IWorkflowRegistry _registry;
    private readonly ILogger<WorkflowOrchestrator> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public WorkflowOrchestrator(IJobStore store, IJobExecutor executor,
        IWorkflowRegistry registry, ILogger<WorkflowOrchestrator> logger)
    {
        _store = store;
        _executor = executor;
        _registry = registry;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _store.ExecutionChanged += OnExecutionChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _store.ExecutionChanged -= OnExecutionChanged;
        return Task.CompletedTask;
    }

    public void Dispose() => _store.ExecutionChanged -= OnExecutionChanged;

    public async Task<string> StartAsync(string definitionName)
    {
        var def = _registry.Get(definitionName)
            ?? throw new InvalidOperationException($"Unknown workflow '{definitionName}'.");

        var run = new WorkflowRun { DefinitionName = def.Name };
        foreach (var node in def.Nodes)
        {
            run.Nodes.Add(new NodeRun
            {
                Name = node.Name,
                JobTypeName = node.JobTypeName,
                Dependencies = node.Dependencies.ToList(),
                IsFanOut = node.FanOut is not null
            });
        }

        await _store.SaveWorkflowRunAsync(run);
        _logger.LogInformation("Started workflow {Name} as run {Id}", def.Name, run.Id);
        await EvaluateAsync(run);
        return run.Id;
    }

    private void OnExecutionChanged(JobExecution e)
    {
        if (e.WorkflowRunId is null) return;
        // Run off-thread so we never re-enter the per-run lock from within it
        // (CreateAsync/UpdateAsync raise this event synchronously while we hold it).
        _ = Task.Run(async () =>
        {
            try
            {
                var run = await _store.GetWorkflowRunAsync(e.WorkflowRunId);
                if (run is not null) await EvaluateAsync(run);
            }
            catch (Exception ex) { _logger.LogError(ex, "Workflow evaluation failed"); }
        });
    }

    private async Task EvaluateAsync(WorkflowRun run)
    {
        var gate = _locks.GetOrAdd(run.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (run.IsTerminal) return;
            var def = _registry.Get(run.DefinitionName);
            if (def is null) return;

            var changed = false;

            // 1. Settle running nodes: retry failed tasks, or mark the node done.
            foreach (var node in run.Nodes.Where(n => n.Status == NodeRunStatus.Running))
                changed |= await SettleNodeAsync(node, def);

            // 2. Omit/skip/start pending nodes.
            foreach (var node in run.Nodes.Where(n => n.Status == NodeRunStatus.Pending))
            {
                var deps = node.Dependencies.Select(run.Node).Where(d => d is not null).Cast<NodeRun>().ToList();

                if (deps.Any(d => d.Status is NodeRunStatus.Failed or NodeRunStatus.Skipped))
                {
                    node.Status = NodeRunStatus.Skipped;
                    changed = true;
                    continue;
                }

                if (!deps.All(d => d.SatisfiesDependents)) continue; // still waiting

                var defNode = def.Node(node.Name)!;
                if (defNode.Condition is not null && !defNode.Condition(new Outputs(run, _store)))
                {
                    node.Status = NodeRunStatus.Omitted;
                    _logger.LogInformation("Run {Run} node {Node}: omitted by condition", run.Id, node.Name);
                    changed = true;
                    continue;
                }

                await StartNodeAsync(run, node, defNode);
                changed = true;
            }

            // 3. Recompute the run's overall status.
            if (run.Nodes.All(n => n.IsTerminal) && !run.IsTerminal)
            {
                run.Status = run.Nodes.Any(n => n.Status is NodeRunStatus.Failed or NodeRunStatus.Skipped)
                    ? WorkflowRunStatus.Failed
                    : WorkflowRunStatus.Succeeded;
                run.FinishedAt = DateTimeOffset.UtcNow;
                changed = true;
            }

            if (changed) await _store.SaveWorkflowRunAsync(run);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Advances a running node: retries failed attempts, settles to Succeeded/Failed when stable.</summary>
    private async Task<bool> SettleNodeAsync(NodeRun node, WorkflowDefinition def)
    {
        var maxAttempts = def.Node(node.Name)!.MaxAttempts;
        var changed = false;
        var anyInFlight = false;

        foreach (var task in node.Tasks)
        {
            var current = task.Current is null ? null : await _store.GetAsync(task.Current);
            if (current is null || !current.IsTerminal) { anyInFlight = true; continue; }
            if (current.Status == JobStatus.Succeeded) continue;

            // Failed/Stopped — retry if attempts remain.
            if (task.Attempts.Count < maxAttempts)
            {
                _logger.LogInformation("Run {Run} node {Node}: retrying (attempt {N}/{Max})",
                    current.WorkflowRunId, node.Name, task.Attempts.Count + 1, maxAttempts);
                await StartAttemptAsync(current.WorkflowRunId!, node, task);
                anyInFlight = true;
                changed = true;
            }
            // else: this task has permanently failed.
        }

        if (anyInFlight) return changed;

        // All tasks settled.
        var allSucceeded = true;
        foreach (var task in node.Tasks)
        {
            var cur = task.Current is null ? null : await _store.GetAsync(task.Current);
            if (cur is null || cur.Status != JobStatus.Succeeded) { allSucceeded = false; break; }
        }
        node.Status = allSucceeded ? NodeRunStatus.Succeeded : NodeRunStatus.Failed;
        return true;
    }

    private async Task StartNodeAsync(WorkflowRun run, NodeRun node, WorkflowNode defNode)
    {
        // Base args = static args + inputs bound from upstream outputs.
        var args = new Dictionary<string, string>(defNode.Arguments);
        foreach (var (arg, source) in defNode.InputBindings)
        {
            var value = ResolveBinding(run, source);
            if (value is not null) args[arg] = value;
        }

        if (defNode.FanOut is { } fan)
        {
            var items = ParseArray(NodeOutput(run, fan.SourceNode, fan.OutputKey));
            if (items.Count == 0)
            {
                node.Status = NodeRunStatus.Succeeded; // nothing to fan out over
                _logger.LogInformation("Run {Run} node {Node}: fan-out over empty set", run.Id, node.Name);
                return;
            }

            foreach (var item in items)
                node.Tasks.Add(new NodeTask { Arguments = new(args) { [fan.ItemArgument] = item } });
            _logger.LogInformation("Run {Run} node {Node}: fanned out into {Count}", run.Id, node.Name, items.Count);
        }
        else
        {
            node.Tasks.Add(new NodeTask { Arguments = args });
        }

        foreach (var task in node.Tasks)
            await StartAttemptAsync(run.Id, node, task);

        node.Status = NodeRunStatus.Running;
    }

    private async Task StartAttemptAsync(string runId, NodeRun node, NodeTask task)
    {
        var args = new Dictionary<string, string>(task.Arguments)
        {
            ["__attempt"] = task.Attempts.Count.ToString() // 0-based attempt index
        };
        var descriptor = new JobDescriptor(node.Name, node.JobTypeName, args);
        var exec = await _store.CreateAsync(descriptor, _executor.Name);
        exec.WorkflowRunId = runId;
        exec.NodeName = node.Name;
        await _store.UpdateAsync(exec);
        task.Attempts.Add(exec.Id);
        await _executor.StartAsync(exec);
    }

    /// <summary>Resolves "nodeName.outputKey" against a completed node's outputs.</summary>
    private string? ResolveBinding(WorkflowRun run, string source)
    {
        var dot = source.IndexOf('.');
        return dot < 0 ? null : NodeOutput(run, source[..dot], source[(dot + 1)..]);
    }

    private string? NodeOutput(WorkflowRun run, string nodeName, string key)
    {
        var node = run.Node(nodeName);
        if (node is null) return null;
        string? found = null;
        foreach (var id in node.ExecutionIds)
        {
            var e = _store.GetAsync(id).GetAwaiter().GetResult();
            if (e is not null && e.Outputs.TryGetValue(key, out var v)) found = v; // newest wins
        }
        return found;
    }

    /// <summary>Parses a JSON array into per-item argument strings (scalars unquoted, objects raw JSON).</summary>
    private static List<string> ParseArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return new();
            return doc.RootElement.EnumerateArray()
                .Select(el => el.ValueKind == JsonValueKind.String ? el.GetString()! : el.GetRawText())
                .ToList();
        }
        catch (JsonException) { return new(); }
    }

    /// <summary>IWorkflowOutputs view used to evaluate a node's when-condition.</summary>
    private sealed class Outputs : IWorkflowOutputs
    {
        private readonly WorkflowRun _run;
        private readonly IJobStore _store;
        public Outputs(WorkflowRun run, IJobStore store) { _run = run; _store = store; }

        public string? Get(string node, string key)
        {
            var n = _run.Node(node);
            if (n is null) return null;
            string? found = null;
            foreach (var id in n.ExecutionIds)
            {
                var e = _store.GetAsync(id).GetAwaiter().GetResult();
                if (e is not null && e.Outputs.TryGetValue(key, out var v)) found = v;
            }
            return found;
        }
    }
}
