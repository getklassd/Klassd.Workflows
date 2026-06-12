using Klassd.Workflows.Worker;

// Library-path worker: this exe references the worker host package plus the job assemblies it should
// run (here, Klassd.Workflows.SampleJobs). Publishing it yields a self-contained worker image with
// those jobs baked in — the recommended pattern for building your own worker. The whole program is
// one line; all the work lives in WorkerHost.RunAsync.
return await WorkerHost.RunAsync(args);
