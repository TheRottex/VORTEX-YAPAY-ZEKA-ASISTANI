# Security Policy

## Public-subset boundary

This repository is an under-construction, unverified public subset. It contains no deployment instructions, runtime configuration values, secrets, private paths, or release payloads.

Private Hermes execution, Worker services, Tailscale networking, deployment systems, secrets, and runtime configuration are outside this repository and must not be added to issues, documentation, examples, or commits.

## Reporting a vulnerability

Do not publish sensitive details in a public issue. Contact the project maintainer through a private channel and include:

- affected public file and revision;
- minimal reproduction steps;
- impact and suggested mitigation;
- redacted evidence only.

Do not include access tokens, passwords, private keys, database files, device tokens, production URLs, or private filesystem paths.

## Security expectations

- Keep authentication and authorization owner-scoped.
- Keep device credentials secret; store only protected or hashed forms where applicable.
- Validate input and fail closed on invalid authorization or job state.
- Do not log secrets or sensitive runtime data.
- Do not weaken cryptographic checks, token validation, or SQLite ownership constraints for convenience.

Security reports are triaged without any promise of a release timeline.