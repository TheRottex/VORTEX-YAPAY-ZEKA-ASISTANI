# Review Guide

## Confirm the exact export scope

1. Parse `docs/PUBLIC_EXPORT_MANIFEST.json`; reject duplicate, rooted, traversal, wildcard, forbidden, or private paths.
2. Compare its `includedPaths` with `git ls-files -z` in both directions. The hygiene test must report manifest paths missing from Git and Git-tracked paths absent from the manifest. A fresh bootstrap must fail until the owner stages every listed source file; there is no bootstrap bypass, and reviewers must not stage or otherwise mutate Git state as part of this check.
3. Under `Release`, permit only `Release/v1.0.1.md` and reject operational, artifact, checksum, private-storage, and manual-upload material. Reject Desktop, LocalAgent, Worker, Hermes, Tailscale, Docker, deploy, Admin, private Web, runtime settings, secrets, archives, databases, logs, private local paths, and build output.
4. Confirm no project reference resolves outside this repository.

## Inspect security and persistence properties

- Token creation requires a signing key of at least 32 bytes.
- Token validation rejects malformed input, unsupported `alg`/`typ`, invalid signature, expiry, wrong issuer/audience, and malformed subject.
- Password hashes use PBKDF2-SHA256 with random salts and fixed-time comparison.
- Device tokens are salted hashes with fixed-time comparison.
- Queue requests require owner device membership, allowlisted tools, bounded arguments, and required confirmation.
- A completion request contains only device credentials, success, code, message, and timeline; it cannot submit dry-run, command, output, or technical details.
- Completion status mapping is verified through the actual route: invalid credentials 401; absent/pending/non-device job 404; claimed completion 200; repeated completion returns original data without overwrite; other state guard failure 409.
- Queued `DryRun` persists as server-owned data and is not altered by completion.
- Registration, login, device registration, action queueing, job claim, and job completion each use a separate built-in fixed-window limiter keyed only by `HttpContext.Connection.RemoteIpAddress`. It must have a zero queue and generic 429 rejection; forwarded headers must not be read, trusted, or enabled.
- Rate-limit tests use fresh server hosts, prove each targeted route reaches 429 only after the limit, prove ten valid action queues are accepted before the generic empty 429 rejects the eleventh, prove policies are isolated, and prove changing `X-Forwarded-For` cannot bypass a limit.

## Verify locally

```powershell
dotnet restore VortexAI.Public.sln
dotnet build VortexAI.Public.sln -c Release --no-restore
dotnet test Vortex.Public.Tests/Vortex.Public.Tests.csproj -c Release --no-build
```

The hygiene test is expected to fail before the owner stages the manifest-listed public files, because it enforces exact `git ls-files -z` equality. It must name both directional differences and must not change the Git index. Treat all results as revision-specific evidence. This documentation is not proof of a release, deployment, desktop action, or package artifact.
