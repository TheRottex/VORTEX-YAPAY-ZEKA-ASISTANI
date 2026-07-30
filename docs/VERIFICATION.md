# Verification Record

## Current evidence

- Public-source verification must be recorded with exact command output for the reviewed revision.
- The public test host exercises completion route statuses: 401 for invalid credentials, 404 for unknown/pending/other-device jobs, 200 for claimed completion and idempotent retry, and 409 for another state guard failure.
- The completion test also verifies owner isolation, unchanged original completion data on retry, and the queued `DryRun` invariant.
- Rate-limit endpoint tests use a fresh public-server host per test, verify ten permitted requests followed by generic 429 for each protected endpoint, verify ten real valid action queues are accepted before a generic empty 429 rejects the eleventh, verify login exhaustion does not affect job claim, and verify changing `X-Forwarded-For` cannot bypass the direct-peer IP limiter.
- `RepositoryHygieneTests` invokes `git ls-files -z` and enforces exact, bidirectional equality with the manifest, reporting both manifest paths missing from Git and Git-tracked paths absent from the manifest. It has no bootstrap bypass and never modifies the Git index. It permits only `Release/v1.0.1.md` and rejects operational, artifact, checksum, private-storage, and manual-upload indicators in release material. In an untracked bootstrap checkout it fails until the owner stages every intended source file.
- No claim is made here about private product source, deployment, package installation, release assets, OAuth, hardware, voice, or remote execution validation.

## Required public gate

```powershell
dotnet restore VortexAI.Public.sln
dotnet build VortexAI.Public.sln -c Release --no-restore
dotnet test Vortex.Public.Tests/Vortex.Public.Tests.csproj -c Release --no-build
```

Then inspect `git ls-files -z` against `docs/PUBLIC_EXPORT_MANIFEST.json` in both directions and scan public text/source for forbidden private paths, credentials, private-key markers, runtime configuration, archives, database files, build output, Worker/Hermes/Tailscale/deploy material, and unexplained secret-like values.

Only the repository owner may stage the intended source files. Do not mark this repository publish-ready until each command and scan result is recorded for the reviewed revision. A bootstrap hygiene failure means the owner must stage the listed source files; it is not permission to mutate Git state, add generated output, or broaden the manifest.
