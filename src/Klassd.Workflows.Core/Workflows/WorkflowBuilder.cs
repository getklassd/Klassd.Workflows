using Klassd.Workflows.Abstractions;
using Klassd.Workflows.Core.Model;

namespace Klassd.Workflows.Core.Workflows;

/// <summary>
/// Fluent builder for a <see cref="WorkflowDefinition"/>. DAGs are declared in
/// code, e.g.:
/// <code>
/// var wf = new WorkflowBuilder("catalog-integration")
///     .Add&lt;MarketFinderJob&gt;("markets")
///     .Add&lt;DataProxyJob&gt;("data-proxy")
///     .Add&lt;IntegrationJob&gt;("integration", n => n
///         .DependsOn("markets", "data-proxy")
///         .FanOutOver("markets", "market_ids", itemArgument: "market"))
///     .Add&lt;FinalizerJob&gt;("finalizer", n => n.DependsOn("integration"))
///     .Build();
/// </code>
/// </summary>
public sealed class WorkflowBuilder
{
    private readonly string _name;
    private readonly List<NodeBuilder> _nodes = new();

    public WorkflowBuilder(string name) => _name = name;

    public WorkflowBuilder Add<TJob>(string nodeName, Action<NodeBuilder>? configure = null) where TJob : IJob =>
        Add(nodeName, typeof(TJob).FullName!, configure);

    public WorkflowBuilder Add(string nodeName, string jobTypeName, Action<NodeBuilder>? configure = null)
    {
        var nb = new NodeBuilder(nodeName, jobTypeName);
        configure?.Invoke(nb);
        _nodes.Add(nb);
        return this;
    }

    /// <summary>
    /// Add a node that runs an arbitrary container image (e.g. <c>cloud-sql-proxy</c>) instead of an
    /// <c>IJob</c>. Combine with <c>.AsService()</c> for a long-running sidecar whose address is
    /// forwarded to dependents.
    /// </summary>
    public WorkflowBuilder AddContainer(string nodeName, string image, Action<NodeBuilder>? configure = null)
    {
        var nb = new NodeBuilder(nodeName, image, isContainer: true);
        configure?.Invoke(nb);
        _nodes.Add(nb);
        return this;
    }

    public WorkflowDefinition Build()
    {
        var nodes = _nodes.Select(n => n.Build()).ToList();
        Validate(nodes);
        return new WorkflowDefinition { Name = _name, Nodes = nodes };
    }

    private void Validate(IReadOnlyList<WorkflowNode> nodes)
    {
        var names = new HashSet<string>(nodes.Select(n => n.Name));
        foreach (var n in nodes)
        {
            foreach (var dep in n.Dependencies)
                if (!names.Contains(dep))
                    throw new InvalidOperationException($"Node '{n.Name}' depends on unknown node '{dep}'.");

            if (n.FanOut is { } f && !n.Dependencies.Contains(f.SourceNode))
                throw new InvalidOperationException(
                    $"Node '{n.Name}' fans out over '{f.SourceNode}' but does not depend on it.");

            if (n.IsService && n.FanOut is not null)
                throw new InvalidOperationException($"Service node '{n.Name}' cannot fan out.");

            if (string.IsNullOrEmpty(n.JobTypeName) == (n.Container is null))
                throw new InvalidOperationException(
                    $"Node '{n.Name}' must run exactly one of an IJob type or a container image.");
        }
        DetectCycles(nodes);
    }

    private static void DetectCycles(IReadOnlyList<WorkflowNode> nodes)
    {
        var map = nodes.ToDictionary(n => n.Name);
        var state = new Dictionary<string, int>(); // 0=unseen,1=visiting,2=done

        void Visit(string name)
        {
            state[name] = 1;
            foreach (var dep in map[name].Dependencies)
            {
                var s = state.GetValueOrDefault(dep);
                if (s == 1) throw new InvalidOperationException($"Workflow has a cycle involving '{dep}'.");
                if (s == 0) Visit(dep);
            }
            state[name] = 2;
        }

        foreach (var n in nodes)
            if (state.GetValueOrDefault(n.Name) == 0) Visit(n.Name);
    }
}

public sealed class NodeBuilder
{
    private readonly string _name;
    private readonly string _jobType;
    private readonly bool _isContainer;
    private readonly string? _image;
    private readonly List<string> _deps = new();
    private readonly Dictionary<string, string> _args = new();
    private readonly Dictionary<string, string> _bindings = new();
    private FanOutSpec? _fanOut;
    private int _maxRetries;
    private Func<IWorkflowOutputs, bool>? _condition;
    private bool _isService;
    private readonly List<InitContainerSpec> _initContainers = new();
    private readonly List<VolumeSpec> _volumes = new();
    private readonly List<VolumeMountSpec> _volumeMounts = new();
    private SecurityContextSpec? _securityContext;
    private PodSecurityContextSpec? _podSecurityContext;
    private readonly List<EnvFromSpec> _envFrom = new();
    private readonly Dictionary<string, string> _nodeSelector = new();
    private readonly List<TolerationSpec> _tolerations = new();
    private AffinitySpec? _affinity;

    // Container-node fields (only when _isContainer).
    private readonly List<string> _command = new();
    private readonly List<string> _containerArgs = new();
    private readonly Dictionary<string, string> _containerEnv = new();
    private int? _servicePort;
    private int? _readyTcpPort;
    private string? _imagePullPolicy;

    internal NodeBuilder(string name, string jobType) { _name = name; _jobType = jobType; }

    internal NodeBuilder(string name, string image, bool isContainer)
    {
        _name = name;
        _jobType = "";
        _isContainer = isContainer;
        _image = image;
    }

    public NodeBuilder DependsOn(params string[] nodes) { _deps.AddRange(nodes); return this; }

    public NodeBuilder WithArgument(string key, string value) { _args[key] = value; return this; }

    /// <summary>
    /// Make this a long-running "service" (daemon) node: it starts, becomes ready, and stays running
    /// while dependents use it; the engine tears it down once the rest of the run finishes. Readiness
    /// (not exit) unblocks dependents. Cannot be combined with fan-out.
    /// </summary>
    public NodeBuilder AsService() { _isService = true; return this; }

    // ── Container-node configuration (only valid on AddContainer nodes) ──────────
    /// <summary>Override the image entrypoint (container <c>command</c>).</summary>
    public NodeBuilder WithCommand(params string[] command) { RequireContainer(); _command.AddRange(command); return this; }

    /// <summary>Set the container <c>args</c>.</summary>
    public NodeBuilder WithArgs(params string[] args) { RequireContainer(); _containerArgs.AddRange(args); return this; }

    /// <summary>Add a static environment variable to the container.</summary>
    public NodeBuilder WithEnv(string key, string value) { RequireContainer(); _containerEnv[key] = value; return this; }

    /// <summary>The port the container serves on; the engine publishes <c>address</c>=<c>ip:port</c> + <c>ip</c> outputs.</summary>
    public NodeBuilder ServicePort(int port) { RequireContainer(); _servicePort = port; return this; }

    /// <summary>Consider the node ready only once this TCP port accepts connections.</summary>
    public NodeBuilder ReadyOnTcp(int port) { RequireContainer(); _readyTcpPort = port; return this; }

    /// <summary>Set the container imagePullPolicy ("Always" | "IfNotPresent" | "Never").</summary>
    public NodeBuilder WithImagePullPolicy(string policy) { RequireContainer(); _imagePullPolicy = policy; return this; }

    private void RequireContainer()
    {
        if (!_isContainer)
            throw new InvalidOperationException($"Node '{_name}' is not a container node; use AddContainer(...).");
    }

    // ── Init containers (valid on any node — IJob or container) ──────────────────
    /// <summary>
    /// Add an init container that runs (to completion, in order) before this node's pod starts.
    /// Valid on both <c>IJob</c> and container nodes. Combined with any executor-wide init containers.
    /// </summary>
    public NodeBuilder WithInitContainer(InitContainerSpec init) { _initContainers.Add(init); return this; }

    /// <summary>Convenience overload: an init container that runs <paramref name="image"/> with the given args.</summary>
    public NodeBuilder WithInitContainer(string name, string image, params string[] args) =>
        WithInitContainer(new InitContainerSpec { Name = name, Image = image, Args = args });

    // ── Volumes (valid on any node — IJob or container) ──────────────────────────
    /// <summary>Declare a pod-level volume for this node. Mount it into containers with <see cref="WithVolumeMount(VolumeMountSpec)"/>.</summary>
    public NodeBuilder WithVolume(VolumeSpec volume) { _volumes.Add(volume); return this; }

    /// <summary>Convenience: declare an ephemeral <see cref="VolumeKind.EmptyDir"/> volume by name.</summary>
    public NodeBuilder WithEmptyDir(string name) => WithVolume(new VolumeSpec { Name = name, Kind = VolumeKind.EmptyDir });

    /// <summary>Mount a declared volume into this node's main container (the worker or the container image).</summary>
    public NodeBuilder WithVolumeMount(VolumeMountSpec mount) { _volumeMounts.Add(mount); return this; }

    /// <summary>Convenience overload: mount volume <paramref name="name"/> at <paramref name="mountPath"/> on the main container.</summary>
    public NodeBuilder WithVolumeMount(string name, string mountPath, bool readOnly = false) =>
        WithVolumeMount(new VolumeMountSpec { Name = name, MountPath = mountPath, ReadOnly = readOnly });

    // ── Security contexts (valid on any node — IJob or container) ────────────────
    /// <summary>Set the security context of this node's main container (worker or container image).</summary>
    public NodeBuilder WithSecurityContext(SecurityContextSpec securityContext) { _securityContext = securityContext; return this; }

    /// <summary>Set the pod-level security context for this node's pod.</summary>
    public NodeBuilder WithPodSecurityContext(PodSecurityContextSpec podSecurityContext) { _podSecurityContext = podSecurityContext; return this; }

    // ── envFrom (valid on any node — imports into the main container) ─────────────
    /// <summary>Import a ConfigMap or Secret as environment variables of this node's main container.</summary>
    public NodeBuilder WithEnvFrom(EnvFromSpec envFrom) { _envFrom.Add(envFrom); return this; }

    /// <summary>Convenience: import all keys of a ConfigMap as env vars.</summary>
    public NodeBuilder WithEnvFromConfigMap(string name, bool optional = false) =>
        WithEnvFrom(new EnvFromSpec { Kind = EnvFromKind.ConfigMap, Name = name, Optional = optional });

    /// <summary>Convenience: import all keys of a Secret as env vars.</summary>
    public NodeBuilder WithEnvFromSecret(string name, bool optional = false) =>
        WithEnvFrom(new EnvFromSpec { Kind = EnvFromKind.Secret, Name = name, Optional = optional });

    // ── Scheduling: node selector / tolerations / affinity (pod-level) ───────────
    /// <summary>Require this node's pod to schedule onto nodes carrying the label <paramref name="key"/>=<paramref name="value"/>.</summary>
    public NodeBuilder WithNodeSelector(string key, string value) { _nodeSelector[key] = value; return this; }

    /// <summary>Add a toleration so this node's pod can schedule onto tainted nodes.</summary>
    public NodeBuilder WithToleration(TolerationSpec toleration) { _tolerations.Add(toleration); return this; }

    /// <summary>Set pod affinity/anti-affinity (node and/or pod (anti-)affinity) for this node's pod.</summary>
    public NodeBuilder WithAffinity(AffinitySpec affinity) { _affinity = affinity; return this; }

    /// <summary>Retry failed executions of this node up to <paramref name="maxRetries"/> times.</summary>
    public NodeBuilder WithRetries(int maxRetries) { _maxRetries = Math.Max(0, maxRetries); return this; }

    /// <summary>Run only if <paramref name="condition"/> (over upstream outputs) is true; else omit the node.</summary>
    public NodeBuilder When(Func<IWorkflowOutputs, bool> condition) { _condition = condition; return this; }

    /// <summary>Run only if a specific upstream output equals <paramref name="expected"/>.</summary>
    public NodeBuilder When(string sourceNode, string outputKey, string expected)
    {
        _condition = o => string.Equals(o.Get(sourceNode, outputKey), expected, StringComparison.Ordinal);
        return this;
    }

    /// <summary>Bind one of this node's arguments to an upstream node's output.</summary>
    public NodeBuilder BindInput(string argument, string sourceNode, string outputKey)
    {
        _bindings[argument] = $"{sourceNode}.{outputKey}";
        return this;
    }

    /// <summary>Fan out: one execution per element of sourceNode's JSON-array output.</summary>
    public NodeBuilder FanOutOver(string sourceNode, string outputKey, string itemArgument)
    {
        _fanOut = new FanOutSpec(sourceNode, outputKey, itemArgument);
        if (!_deps.Contains(sourceNode)) _deps.Add(sourceNode);
        return this;
    }

    internal WorkflowNode Build() => new()
    {
        Name = _name,
        JobTypeName = _jobType,
        Container = _isContainer
            ? new ContainerSpec
            {
                Image = _image!,
                Command = _command,
                Args = _containerArgs,
                Env = _containerEnv,
                ServicePort = _servicePort,
                ReadyTcpPort = _readyTcpPort,
                ImagePullPolicy = _imagePullPolicy
            }
            : null,
        IsService = _isService,
        InitContainers = _initContainers,
        Volumes = _volumes,
        VolumeMounts = _volumeMounts,
        SecurityContext = _securityContext,
        PodSecurityContext = _podSecurityContext,
        EnvFrom = _envFrom,
        NodeSelector = _nodeSelector,
        Tolerations = _tolerations,
        Affinity = _affinity,
        Dependencies = _deps.Distinct().ToList(),
        Arguments = _args,
        InputBindings = _bindings,
        FanOut = _fanOut,
        MaxRetries = _maxRetries,
        Condition = _condition
    };
}
