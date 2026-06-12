# Base worker image: the generic Klassd.Workflows worker with NO jobs of its own.
# Consumers layer their job assemblies on top, e.g.:
#
#   FROM ghcr.io/getklassd/workflows-worker:0.1
#   COPY ./my-job-dlls/ /app/
#
# The worker loads every assembly in /app at startup, so the layered IJob types resolve.
# Build from the SOLUTION ROOT:
#   docker build -f src/Klassd.Workflows.Worker.App/Dockerfile -t klassd-workflows-worker:latest .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Klassd.Workflows.Worker.App/Klassd.Workflows.Worker.App.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app .
# The job descriptor is supplied by the scheduler via env vars (KLASSD_*).
ENTRYPOINT ["dotnet", "Klassd.Workflows.Worker.App.dll"]
