using Klassd.Workflows.Worker;

// Generic base worker image. It ships no jobs of its own — layer your job DLLs into /app and they
// are loaded from the worker's directory at startup (see the Dockerfile). To build a self-contained
// worker instead, reference the Klassd.Workflows.Worker package from your own exe alongside your job
// assemblies and call the same entry point.
return await WorkerHost.RunAsync(args);
