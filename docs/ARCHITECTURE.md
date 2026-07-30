# Public Architecture

```text
Authenticated public owner                         Compatible device client
             |                                                |
             v                                                v
                 Vortex.Server.Public
          bearer authentication and ownership
                    SQLite persistence
             owner-scoped device-job lifecycle
```

## Server responsibilities

`Vortex.Server.Public` provides health, registration/login, authenticated profile lookup, device registration/listing, allowlisted action planning, queued device actions, guarded claims, guarded completions, and owner job status.

Authentication uses bearer tokens validated by `TokenService`. A device token authenticates only its own non-revoked device. Database access uses parameterized SQLite commands.

## Completion contract

Completion accepts only `DeviceId`, `DeviceToken`, `Success`, `Code`, `Message`, and `Timeline`. The server persists no dry-run override, command, output, or technical-details field from a completion request.

After validating device credentials, the server scopes the job to that device:

- Missing job, other-device job, or pending job: HTTP 404.
- Same-device claimed job: guarded transition to `completed`, then HTTP 200 with persisted status.
- Same-device completed job: HTTP 200 with the original persisted status; the retry does not overwrite any result field.
- Any other lifecycle guard failure: HTTP 409.
- Malformed, unknown, or revoked device credentials: HTTP 401.

The queued `DryRun` value is server-owned persistence and stays unchanged through completion.

## Local action boundary

Only named tools in `DeviceJobService` policy can be queued. Inputs are bounded. Tools requiring confirmation cannot queue without an explicit confirmation flag. There is no free-form command execution endpoint.

## Public layout references

The mandated top-level folders are navigation placeholders. `Vortex.Desktop/` alone contains an allowlisted set of four static Desktop service copies under `Services/` and six static orb assets under `Assets/Web/`. These files sit outside the projects in `VortexAI.Public.sln`; they are not loaded by `Vortex.Server.Public`, are not a Desktop or LocalAgent runtime, and do not create an execution path.

## Deliberate absence

This repository does not include Desktop or LocalAgent execution code. Hermes, HermesWorker, remote-execution queues, Tailscale, containers, deployment systems, Admin, private Web source beyond the allowlisted `Vortex.Desktop` presentation references, production endpoints, provider integrations, build artifacts, or release packages are not part of this architecture or source tree.
