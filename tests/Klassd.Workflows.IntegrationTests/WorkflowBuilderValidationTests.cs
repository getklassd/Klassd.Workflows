using Klassd.Workflows.Core.Model;
using Klassd.Workflows.Core.Workflows;

namespace Klassd.Workflows.IntegrationTests;

public class WorkflowBuilderValidationTests
{
    [Test]
    public async Task Service_node_cannot_fan_out()
    {
        var builder = new WorkflowBuilder("bad")
            .Add("src", "Src")
            .Add("svc", "Svc", n => n.AsService().FanOutOver("src", "items", "item"));
        await Assert.That(() => builder.Build()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Unknown_dependency_throws()
    {
        var builder = new WorkflowBuilder("bad").Add("a", "A", n => n.DependsOn("ghost"));
        await Assert.That(() => builder.Build()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Cycle_throws()
    {
        var builder = new WorkflowBuilder("bad")
            .Add("a", "A", n => n.DependsOn("b"))
            .Add("b", "B", n => n.DependsOn("a"));
        await Assert.That(() => builder.Build()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Container_node_carries_image_ports_and_service_flag()
    {
        var def = new WorkflowBuilder("ok")
            .AddContainer("proxy", "cloud-sql-proxy:2.11", c => c
                .WithArgs("--port=5432")
                .ServicePort(5432)
                .ReadyOnTcp(5432)
                .AsService())
            .Build();

        var node = def.Node("proxy")!;
        await Assert.That(node.IsService).IsTrue();
        await Assert.That(node.Container).IsNotNull();
        await Assert.That(node.Container!.Image).IsEqualTo("cloud-sql-proxy:2.11");
        await Assert.That(node.Container.ServicePort).IsEqualTo(5432);
        await Assert.That(node.Container.ReadyTcpPort).IsEqualTo(5432);
        await Assert.That(node.JobTypeName).IsEqualTo("");
    }

    [Test]
    public async Task Init_containers_attach_to_both_job_and_container_nodes()
    {
        var def = new WorkflowBuilder("ok")
            .Add("cleanup", "Cleanup", n => n
                .WithInitContainer("migrate", "olympus/migrate:1", "--db", "primary"))
            .AddContainer("proxy", "cloud-sql-proxy:2.11", c => c
                .ServicePort(5432)
                .WithInitContainer(new InitContainerSpec { Name = "warm", Image = "busybox:1.36" }))
            .Build();

        var job = def.Node("cleanup")!;
        await Assert.That(job.InitContainers.Count).IsEqualTo(1);
        await Assert.That(job.InitContainers[0].Name).IsEqualTo("migrate");
        await Assert.That(job.InitContainers[0].Image).IsEqualTo("olympus/migrate:1");
        await Assert.That(job.InitContainers[0].Args).IsEquivalentTo(new[] { "--db", "primary" });

        var proxy = def.Node("proxy")!;
        await Assert.That(proxy.InitContainers.Single().Name).IsEqualTo("warm");
    }

    [Test]
    public async Task Volumes_and_mounts_attach_to_the_node()
    {
        var def = new WorkflowBuilder("ok")
            .Add("cleanup", "Cleanup", n => n
                .WithEmptyDir("scratch")
                .WithVolume(new VolumeSpec { Name = "secrets", Kind = VolumeKind.Secret, Source = "db-creds" })
                .WithVolumeMount("scratch", "/scratch")
                .WithVolumeMount(new VolumeMountSpec { Name = "secrets", MountPath = "/secrets", ReadOnly = true }))
            .Build();

        var node = def.Node("cleanup")!;
        await Assert.That(node.Volumes.Count).IsEqualTo(2);
        await Assert.That(node.Volumes[0].Name).IsEqualTo("scratch");
        await Assert.That(node.Volumes[0].Kind).IsEqualTo(VolumeKind.EmptyDir);
        await Assert.That(node.Volumes[1].Kind).IsEqualTo(VolumeKind.Secret);
        await Assert.That(node.Volumes[1].Source).IsEqualTo("db-creds");

        await Assert.That(node.VolumeMounts.Count).IsEqualTo(2);
        await Assert.That(node.VolumeMounts[0].MountPath).IsEqualTo("/scratch");
        await Assert.That(node.VolumeMounts[1].Name).IsEqualTo("secrets");
        await Assert.That(node.VolumeMounts[1].ReadOnly).IsTrue();
    }

    [Test]
    public async Task Security_contexts_attach_to_the_node()
    {
        var def = new WorkflowBuilder("ok")
            .Add("cleanup", "Cleanup", n => n
                .WithPodSecurityContext(new PodSecurityContextSpec { FsGroup = 2000, RunAsNonRoot = true })
                .WithSecurityContext(new SecurityContextSpec
                {
                    RunAsUser = 1000,
                    ReadOnlyRootFilesystem = true,
                    DropCapabilities = new[] { "ALL" },
                }))
            .Build();

        var node = def.Node("cleanup")!;
        await Assert.That(node.PodSecurityContext!.FsGroup).IsEqualTo(2000L);
        await Assert.That(node.PodSecurityContext.RunAsNonRoot).IsEqualTo(true);
        await Assert.That(node.SecurityContext!.RunAsUser).IsEqualTo(1000L);
        await Assert.That(node.SecurityContext.ReadOnlyRootFilesystem).IsEqualTo(true);
        await Assert.That(node.SecurityContext.DropCapabilities).IsEquivalentTo(new[] { "ALL" });
    }

    [Test]
    public async Task Resources_envfrom_and_scheduling_attach_to_the_node()
    {
        var def = new WorkflowBuilder("ok")
            .Add("cleanup", "Cleanup", n => n
                .WithEnvFromConfigMap("app-config")
                .WithEnvFromSecret("db-creds", optional: true)
                .WithNodeSelector("pool", "batch")
                .WithToleration(new TolerationSpec { Key = "dedicated", Operator = "Equal", Value = "batch", Effect = "NoSchedule" })
                .WithAffinity(new AffinitySpec
                {
                    NodeAffinity = new NodeAffinitySpec
                    {
                        Required = new[]
                        {
                            new NodeSelectorTermSpec
                            {
                                MatchExpressions = new[] { new NodeSelectorRequirementSpec { Key = "disktype", Operator = "In", Values = new[] { "ssd" } } },
                            },
                        },
                    },
                }))
            .Build();

        var node = def.Node("cleanup")!;
        await Assert.That(node.EnvFrom.Count).IsEqualTo(2);
        await Assert.That(node.EnvFrom[0].Kind).IsEqualTo(EnvFromKind.ConfigMap);
        await Assert.That(node.EnvFrom[1].Optional).IsTrue();
        await Assert.That(node.NodeSelector["pool"]).IsEqualTo("batch");
        await Assert.That(node.Tolerations.Single().Effect).IsEqualTo("NoSchedule");
        await Assert.That(node.Affinity!.NodeAffinity!.Required[0].MatchExpressions[0].Values).IsEquivalentTo(new[] { "ssd" });
    }

    [Test]
    public async Task BindServiceAddress_binds_the_address_output_and_adds_the_dependency()
    {
        var def = new WorkflowBuilder("ok")
            .AddContainer("proxy", "cloud-sql-proxy:2.11", c => c.AsService().ServicePort(5432))
            .Add("cleanup", "Cleanup", n => n.BindServiceAddress("db_host", "proxy"))
            .Build();

        var cleanup = def.Node("cleanup")!;
        // The dependency was added automatically (no separate DependsOn needed).
        await Assert.That(cleanup.Dependencies).Contains("proxy");
        // Binding resolves to the well-known "address" output without naming the string.
        await Assert.That(cleanup.InputBindings["db_host"]).IsEqualTo("proxy.address");
    }

    [Test]
    public async Task File_outputs_and_fanout_parallelism_attach_to_the_node()
    {
        var def = new WorkflowBuilder("ok")
            .Add("markets", "Markets", n => n
                .WithFileOutput("market_ids", "/mnt/out/market_ids.json", @default: "[\"en-dk_DKK\"]"))
            .Add("work", "Work", n => n
                .FanOutOver("markets", "market_ids", "market", maxParallelism: 5))
            .Build();

        var markets = def.Node("markets")!;
        await Assert.That(markets.FileOutputs.Single().Name).IsEqualTo("market_ids");
        await Assert.That(markets.FileOutputs[0].Path).IsEqualTo("/mnt/out/market_ids.json");
        await Assert.That(markets.FileOutputs[0].Default).IsEqualTo("[\"en-dk_DKK\"]");

        var work = def.Node("work")!;
        await Assert.That(work.FanOut!.MaxParallelism).IsEqualTo(5);
        await Assert.That(work.Dependencies).Contains("markets");
    }

    [Test]
    public async Task Container_methods_rejected_on_job_node()
    {
        // ServicePort on a non-container node throws while configuring (inside Add).
        await Assert.That(() => new WorkflowBuilder("bad").Add("j", "Job", n => n.ServicePort(80)))
            .Throws<InvalidOperationException>();
    }
}
