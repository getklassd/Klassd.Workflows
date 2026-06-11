# Klassd.Workflows.IntegrationTests

End-to-end tests for the **Kubernetes executor** against a real, throwaway single-node cluster
([K3s](https://k3s.io)) spun up entirely through **Testcontainers** — no `kind`/`docker`/`kubectl`
CLI needed, just a reachable Docker daemon.

`KubernetesCluster` starts a K3s container, builds the worker image from
`src/Klassd.Workflows.Worker/Dockerfile` (via Testcontainers' `ImageFromDockerfileBuilder`), and
side-loads it into the node's containerd (`docker save` over the Docker API → `ctr images import`).
A registry-qualified image name (`klassd.test/worker:it`) plus `imagePullPolicy=Never` makes the
kubelet use the imported image verbatim and never try to pull it.

`TestHost` then wires a full Klassd.Workflows host (core + the Kubernetes executor + the running
orchestrator) once for the assembly, with a **MinIO** container (`MinioStore`) as the S3 artifact
store so the DAG can pass payloads between pods. The tests cover:

- **`Job_runs_to_completion`** — `HelloWorldJob` → `Succeeded`, progress 100, inline `##BAR##` reaches total.
- **`Failing_job_reports_failure`** — `FailingJob` → `Failed` with the error surfaced from the pod.
- **`Stopping_a_running_job_marks_it_stopped`** — `StopAsync` deletes the K8s Job → `Stopped`.
- **`Workflow_dag_runs_all_nodes`** — the full `catalog-integration` DAG (fan-out, retries, `when`
  gates, **cross-pod artifacts via S3/MinIO**) → `Succeeded`, `rollback` omitted, `publish` succeeds
  via retry, and the finalizer reads the dataset artifact `data-proxy` wrote.

### MinIO / S3 notes

- Pods reach MinIO at its **bridge IP** (`http://<ip>:9000`); egress NATs through the K3s node.
  Docker-network *aliases* don't resolve inside pods (they use CoreDNS, not Docker DNS), so it's an IP.
- AWS SDK **v4** sends integrity checksums by default, which MinIO rejects
  (`The provided 'x-amz-content-sha256' header does not match what was computed`). The `s3` provider
  sets `RequestChecksumCalculation`/`ResponseChecksumValidation = WHEN_REQUIRED` whenever a
  `serviceUrl` is configured — required for MinIO/Ceph/etc., harmless for real AWS.
- The DAG test failing fast (seconds) almost always means `data-proxy`'s artifact write failed — the
  test dumps each node's status + exec error/logs on failure (`DiagnoseAsync`).

## Running

Opt-in — they build an image and spin up a cluster (minutes). A normal run skips them. Enable with
the env var and a running Docker daemon:

```pwsh
$env:KLASSD_K8S_IT = "1"
dotnet run --project tests/Klassd.Workflows.IntegrationTests   # TUnit on Microsoft.Testing.Platform
```

Without `KLASSD_K8S_IT=1` (or with no Docker) every test reports **skipped**. First run pulls the
`rancher/k3s` and .NET SDK/runtime images and `dotnet publish`es the worker; later runs reuse caches.
Testcontainers' Ryuk reaper removes the cluster afterwards even if a run is killed mid-way.
