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
    public async Task Container_methods_rejected_on_job_node()
    {
        // ServicePort on a non-container node throws while configuring (inside Add).
        await Assert.That(() => new WorkflowBuilder("bad").Add("j", "Job", n => n.ServicePort(80)))
            .Throws<InvalidOperationException>();
    }
}
