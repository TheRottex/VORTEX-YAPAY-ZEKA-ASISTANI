# Contributing

## Scope first

Contributions must stay within this public subset:

- `Vortex.Server.Public`
- `Vortex.Contracts`
- public documentation

Do not add Hermes, Worker, Tailscale, deployment automation, secret material, runtime configuration, private paths, or release binaries.

## Before proposing a change

1. Read [docs/PUBLIC_SCOPE.md](docs/PUBLIC_SCOPE.md), [SECURITY.md](SECURITY.md), and [docs/REVIEW_GUIDE.md](docs/REVIEW_GUIDE.md).
2. Describe the public problem, intended behavior, and test/build evidence.
3. Keep the patch focused; do not refactor unrelated code.
4. Add or update tests when test infrastructure is available.
5. Document limitations honestly. Do not claim a capability is verified without reproducible evidence.

## Documentation rules

Use Turkish/English-safe plain UTF-8 text. Do not publish credentials, private hostnames, filesystem paths, runtime settings, or deployment details. Documentation must distinguish planned, implemented, and verified states.

## Pull requests

A pull request should state its public-scope impact, validation performed, and known limitations. No source-contained release bytes, generated runtime data, SQLite databases, or local build outputs are accepted.