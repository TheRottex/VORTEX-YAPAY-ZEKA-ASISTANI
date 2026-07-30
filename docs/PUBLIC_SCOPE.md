# Public Scope

## Included

- .NET 8 public contracts, minimal ASP.NET Core API, SQLite schema, and parameterized persistence helpers.
- Password hashing, signed bearer-token validation, and AES-GCM helper code.
- Owner-scoped device registration, allowlisted device-action planning/queueing, guarded claim/completion, and owner job-status retrieval.
- Public tests, governance documents, and the exact positive file list in [PUBLIC_EXPORT_MANIFEST.json](PUBLIC_EXPORT_MANIFEST.json).
- Mandated top-level layout README files. `Vortex.Desktop` contains four explicitly copied Desktop service examples and six explicitly copied static orb UI assets, all outside the public solution compilation paths.

## Supported capabilities

- Password registration/login and authenticated profile lookup.
- Registration, login, device registration, action queueing, job claim, and job completion each have a fixed-window direct-peer IP limit. No forwarded-header value is read or trusted, rejected requests queue nothing, and rejection is generic HTTP 429.
- A compatible device client can claim its own queued job and submit persisted completion fields only: device credentials, success, code, message, and timeline.
- Completion semantics are explicit: malformed, unknown, or revoked device credentials return 401; a valid device with no job for itself or a pending job returns 404; a same-device claimed job completes once and returns 200; a repeat after completion returns the original stored data and cannot overwrite it; another state guard failure returns 409.
- A queued job's `DryRun` value remains server-owned and is returned in owner-visible status; completion cannot change it.

## Excluded and unsupported

- Desktop and LocalAgent source/runtime. The isolated `Vortex.Desktop/` references are not a runtime, are not compiled by the public solution, and cannot execute a device action.
- `Vortex.HermesWorker`, Hermes profiles/workspaces, Worker contracts/tokens/queues/tests, Tailscale, Docker, deployment, Admin, and private Web source.
- Runtime configuration, `.env` files, secrets, OAuth values, JWT keys, device tokens, database files, logs, user data, build output, binaries, archives, checksums, package assets, private storage, and operational history.
- The separate private source tree and its Git history.
- No public route starts Hermes, contacts a Worker, configures Tailscale, or claims execution success.

## Rules

This public edition uses positive inclusion. A file is included only when its exact path appears in `docs/PUBLIC_EXPORT_MANIFEST.json`; wildcards are not permitted. The manifest must equal `git ls-files` in both directions. `.gitignore` is not a publication boundary by itself.
