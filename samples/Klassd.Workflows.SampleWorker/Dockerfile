# Sample worker image (library path): a thin exe referencing the worker host + the sample job
# assemblies, published into a self-contained worker image. This is the recommended pattern for
# building your own worker, and is what the Kubernetes integration tests run.
# Build from the SOLUTION ROOT so the worker + its referenced job assemblies restore together:
#   docker build -f samples/Klassd.Workflows.SampleWorker/Dockerfile -t klassd-workflows-sampleworker:latest .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish samples/Klassd.Workflows.SampleWorker/Klassd.Workflows.SampleWorker.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app .
# The job descriptor is supplied by the scheduler via env vars (KLASSD_*).
ENTRYPOINT ["dotnet", "Klassd.Workflows.SampleWorker.dll"]
