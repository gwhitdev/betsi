#!/usr/bin/env bash
#
# Deploy one image tag to a Betsi host, apply migrations, and prove the result serves
# traffic before leaving it in place (MVP-106, MVP-107).
#
# Usage:
#   deploy.sh <image reference> [<compose project directory on the host>]
#
# Environment:
#   DEPLOY_HOST        user@host of the Docker host (required)
#   DEPLOY_SSH_KEY     path to the private key (required)
#   HEALTH_URL         readiness URL to poll (default http://127.0.0.1:8080/health/ready,
#                      evaluated on the remote host)
#   HEALTH_TIMEOUT     seconds to wait for readiness (default 120)
#
# The deployment order is: migrate, then switch. Migrations in this system are expand-only
# during a release — the registry lets a tenant database be *ahead* of the build, never behind
# — so the old version keeps serving while the new schema is applied, and the switch that
# follows is the only moment of risk. If readiness does not come back, the previous tag is
# restored and the script exits non-zero, which fails the pipeline.

set -euo pipefail

IMAGE="${1:?usage: deploy.sh <image reference> [remote directory]}"
REMOTE_DIR="${2:-/opt/betsi}"
HEALTH_URL="${HEALTH_URL:-http://127.0.0.1:8080/health/ready}"
HEALTH_TIMEOUT="${HEALTH_TIMEOUT:-120}"

: "${DEPLOY_HOST:?DEPLOY_HOST is required}"
: "${DEPLOY_SSH_KEY:?DEPLOY_SSH_KEY is required}"

remote() {
    ssh -i "$DEPLOY_SSH_KEY" -o StrictHostKeyChecking=accept-new "$DEPLOY_HOST" "$@"
}

echo "==> Recording the running image so a failed deployment can be undone"
PREVIOUS=$(remote "cd '$REMOTE_DIR' && docker compose config --images | head -1" || true)
echo "    previous: ${PREVIOUS:-none}"

echo "==> Pulling $IMAGE"
remote "docker pull '$IMAGE'"

echo "==> Applying control-plane and tenant migrations with the image being deployed"
# --env-file, so the migration runs with exactly the configuration the service will run with,
# including the secret references that resolve its database credentials.
remote "cd '$REMOTE_DIR' && docker run --rm --env-file .env --network host '$IMAGE' tenants migrate"

echo "==> Switching to $IMAGE"
remote "cd '$REMOTE_DIR' && BETSI_IMAGE='$IMAGE' docker compose up -d --wait betsi" || true

echo "==> Waiting up to ${HEALTH_TIMEOUT}s for readiness"
deadline=$(( SECONDS + HEALTH_TIMEOUT ))
ready=0
while (( SECONDS < deadline )); do
    if remote "curl --fail --silent --max-time 5 '$HEALTH_URL' > /dev/null"; then
        ready=1
        break
    fi
    sleep 5
done

if (( ready == 1 )); then
    echo "==> $IMAGE is ready"
    exit 0
fi

echo "!!! $IMAGE did not become ready within ${HEALTH_TIMEOUT}s"

if [[ -n "${PREVIOUS:-}" && "$PREVIOUS" != "$IMAGE" ]]; then
    echo "==> Rolling back to $PREVIOUS"
    # The schema is not rolled back with it: the previous build tolerates a newer schema by
    # design, and undoing a migration under a live database is how data is lost. If a
    # migration itself is the fault, restore from backup — see docs/runbooks/backup-and-restore.md.
    remote "cd '$REMOTE_DIR' && BETSI_IMAGE='$PREVIOUS' docker compose up -d --wait betsi"
    echo "==> Rolled back. The schema is unchanged and remains ahead of $PREVIOUS, which it tolerates."
else
    echo "!!! No previous image recorded; the service is left as it is for inspection."
fi

exit 1
