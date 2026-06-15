using Klassd.Workflows.Artifacts.Gcs;
using Klassd.Workflows.Artifacts.S3;
using Klassd.Workflows.SampleJobs;
using Klassd.Workflows.Worker;
using Microsoft.Extensions.DependencyInjection;

// Library-path worker: this exe references the worker host package plus the shared job registration
// (Klassd.Workflows.SampleJobs). It registers the jobs it can run, the services they depend on, and
// the artifact backends it supports, then runs the single job the scheduler dispatched to it.
// Publishing it (`dotnet publish`) yields a self-contained worker image with these jobs baked in —
// the way you build your own worker. The DashboardHost local executor and the Kubernetes
// integration tests run this exe.
return await WorkerHost.CreateBuilder(args)
    // Dependencies jobs take through their constructors. ConfiguredGreetingJob needs GreetingOptions;
    // read the salutation from configuration (appsettings / /secrets / env, e.g. Greeting__Salutation=Hej).
    .ConfigureServices((services, config) =>
        services.AddSingleton(new GreetingOptions
        {
            Salutation = config["Greeting:Salutation"] ?? "Hello"
        }))
    .RegisterJobs(SampleJobsRegistration.Register)
    // Artifact backends this worker supports; selected by name at runtime (the built-in "file"
    // provider is always available).
    .AddArtifactProvider(new GcsArtifactStoreProvider())
    .AddArtifactProvider(new S3ArtifactStoreProvider())
    .RunAsync();
