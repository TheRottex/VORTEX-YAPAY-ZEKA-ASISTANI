# Vortex Hermes Worker (WSL)

## Overview
- Worker mimarisi
- Server ilişkisi
- Hermes çalışma modeli
- Güvenlik sınırları

## Architecture
- Desktop
- Server
- Worker
- Docker
- CLIProxy
- Model Router

## Directory Layout
- releases/
- current/
- data/
- secrets/
- workspace/
- artifacts/

## Prerequisites
- Windows
- WSL2
- Ubuntu
- systemd
- Docker Desktop
- .NET 8

## Installation

### Windows PowerShell

...

### Ubuntu / WSL

...

### Docker

...

### .NET

...

## Worker Configuration

- worker.env
- environment variables
- Worker ID
- Worker Token
- Data paths
- CLIProxy

## Docker Mode

- docker build
- docker load
- docker run
- image inspect

## Native Mode

- publish
- dotnet
- current symlink

## systemd Service

- install
- enable
- start
- stop
- restart
- status
- logs

## Health Checks

- Worker
- Docker
- Server
- Hermes

## Diagnostics

- Docker not found
- Wrong image
- CLIProxy
- IPv4
- IPv6
- Seed
- Permission
- systemd
- current symlink

## Rollback

...

## Portable Backup

...

## Restore

...

## Security

...

## Troubleshooting Matrix

| Problem | Cause | Fix |

...

## E2E Validation

Queued
↓
Claimed
↓
Running
↓
Completed

## Related Documents

README.html
RESTORE.md
vortex-hermes-worker.env.example
publish-wsl-worker.sh
