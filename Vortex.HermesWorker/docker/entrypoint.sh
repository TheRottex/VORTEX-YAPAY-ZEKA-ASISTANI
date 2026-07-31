#!/bin/sh
set -e

# Bind-mounted per-workspace directories (workspace root, HERMES_HOME) are created
# by the host worker process and may be owned by a host UID that does not match
# the container's non-root "hermes" user. Fix ownership here (running as root)
# before dropping privileges, so Hermes can read/write its home and workspace
# regardless of the host UID that created the mount.
chown -R hermes:hermes "$HERMES_HOME" /workspace 2>/dev/null || true

exec gosu hermes hermes "$@"
