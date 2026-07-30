# Configuration

Only [appsettings.example.json](../Vortex.Server.Public/appsettings.example.json) is versioned. It is intentionally inert.

Create an ignored local `appsettings.json` or use environment variables for local development:

| Key | Requirement |
| --- | --- |
| `Jwt:Issuer` | Local issuer identifier. |
| `Jwt:Audience` | Client audience identifier. |
| `Jwt:SigningKey` | Unique secret with at least 32 UTF-8 bytes. Never commit or log it. |
| `Vortex:DataDirectory` | Optional local writable directory for `vortex-public.db`. Never commit its contents. |

Environment variable equivalents replace `:` with `__`, for example `Jwt__SigningKey`.

Example URLs use `example.invalid`; they are not service endpoints. This public subset does not configure OAuth, remote providers, Worker connectivity, Hermes, Tailscale, Docker, or deployment.
