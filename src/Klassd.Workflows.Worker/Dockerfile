# Worker image: this is what each Kubernetes Job pod runs.
# Build from the SOLUTION ROOT so the worker + its referenced job assemblies
# are restored together:
#   docker build -f src/Klassd.Workflows.Worker/Dockerfile -t klassd-workflows-worker:latest .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Klassd.Workflows.Worker/Klassd.Workflows.Worker.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app .
# The job descriptor is supplied by the scheduler via env vars (KLASSD_*).
ENTRYPOINT ["dotnet", "Klassd.Workflows.Worker.dll"]
