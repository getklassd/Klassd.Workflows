using Klassd.Workflows.Artifacts.Gcs;
using Klassd.Workflows.Artifacts.S3;
using Klassd.Workflows.SampleJobs;
using Klassd.Workflows.Worker;

// Library-path worker: this exe references the worker host package plus the shared job registration
// (Klassd.Workflows.SampleJobs). It registers the jobs it can run and the artifact backends it
// supports, then runs the single job the scheduler dispatched to it. Each job declares its own
// dependencies on its static IJob.Configure (e.g. ConfiguredGreetingJob registers GreetingOptions),
// so there's nothing per-job to wire here. Publishing it (`dotnet publish`) yields a self-contained
// worker image with these jobs baked in — the way you build your own worker. The DashboardHost local
// executor and the Kubernetes integration tests run this exe.
return await WorkerHost.CreateBuilder(args)
    .RegisterJobs(SampleJobsRegistration.Register)
    // Artifact backends this worker supports; selected by name at runtime (the built-in "file"
    // provider is always available).
    .AddArtifactProvider(new GcsArtifactStoreProvider())
    .AddArtifactProvider(new S3ArtifactStoreProvider())
    .RunAsync();
