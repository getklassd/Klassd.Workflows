# Security Policy

## Reporting a vulnerability

Please report security vulnerabilities **privately** — do not open a public issue.

Use GitHub's [**private vulnerability reporting**](https://github.com/getklassd/Klassd.Workflows/security/advisories/new)
(Security → Report a vulnerability). We aim to acknowledge reports within a few business days and
will keep you updated on remediation progress. Please give us a reasonable window to release a fix
before any public disclosure.

When reporting, include where possible:

- Affected package(s) and version(s)
- A description of the issue and its impact
- Steps to reproduce or a proof of concept
- Any suggested remediation

## Supported versions

Klassd.Workflows is pre-1.0. Security fixes are applied to the latest released version. Until 1.0,
please upgrade to the newest version to receive fixes.

## Things to be aware of when deploying

Klassd.Workflows runs your code as jobs and exposes an operational dashboard. Some behavior is by
design and is your responsibility to secure for your environment:

- **Jobs run your code.** The worker loads an `IJob` by type name and executes it. Only deploy job
  assemblies you trust, and treat job arguments/outputs as you would any other input.
- **The dashboard ships with no built-in AuthN/AuthZ.** It can start, stop and inspect jobs. Put it
  behind your own authentication/network boundary — do not expose it to untrusted networks.
- **Artifact and store credentials** come from the platform's default credential chain (workload
  identity / IRSA / env). Scope those credentials to the buckets/databases the workflows need.

If you believe any `Klassd.Workflows.*` package can be abused beyond its documented intent (e.g. a
worker escaping its job context, injection, SSRF), that **is** a security issue — please report it.

## Scope

In scope: all `Klassd.Workflows.*` packages in this repository. Out of scope: third-party
storage/object/cluster providers you wire up (PostgreSQL, MongoDB, S3, GCS, Kubernetes) — report
those to their respective maintainers.
