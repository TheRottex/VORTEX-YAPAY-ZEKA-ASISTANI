# Vortex Hermes Worker on WSL

> **Canonical operations guide:** Open [`README.html`](README.html) for the maintained Turkish command cookbook: correct shell selection, WSL/Docker/.NET preflight, systemd lifecycle, Worker publish, diagnostics, rollback, and portable-backup boundaries.

## Scope

This runbook describes the private WSL-hosted `Vortex.HermesWorker`. The public `Vortex.Server` queues and receives jobs; it never starts Hermes. The Worker connects outbound to the Server. Do not open an inbound laptop port for the Worker.

The canonical HTML guide is the only command catalog. This Markdown file intentionally stays short so lifecycle commands do not diverge between two documents.

## Non-negotiable boundary

- Keep real environment files, Hermes seed files, provider/model keys, user workspaces, logs, databases, Docker volumes, container state, and credentials out of source and portable archives.
- Keep `VORTEX_REQUIRE_PRIVATE_SERVER_ENDPOINT=false` while the Worker intentionally uses a public HTTPS Server origin. Only change it after a verified private transport is available.
- Docker mode is one-shot and does not automatically fall back to process mode.
- Do not infer the local CLIProxy endpoint from an old example. Confirm the configured listener and use the endpoint configured for the active Worker.

## Reference files

- [Secret-free Worker environment template](vortex-hermes-worker.env.example)
- [Canonical private WSL operations guide](README.html)
- [Worker versioned publish helper](../../Vortex.HermesWorker/publish-wsl-worker.sh)
- [Worker user-service template](../../Vortex.HermesWorker/vortex-hermes-worker.service.example)
- [Public Server operations guide](../../Vortex.Server/README.html)

## Acceptance boundary

A successful Docker image smoke test or `/health/worker` response is not a complete E2E result. Complete validation requires an owner-authenticated controlled job to transition:

```text
Queued → Claimed → Running → Completed
```

and must preserve owner isolation (`404` for a different owner; `401` for an anonymous request).
