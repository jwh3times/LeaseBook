#!/usr/bin/env bash
# LeaseBook local dev helper (POSIX). Cross-platform twin: scripts/dev.ps1
#
#   ./scripts/dev.sh up         # start Postgres, wait until healthy
#   ./scripts/dev.sh down       # stop containers (keep data)
#   ./scripts/dev.sh reset-db   # wipe the data volume and re-bootstrap from scratch
#   ./scripts/dev.sh psql       # open psql in the container as the migrator role
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

wait_healthy() {
  echo "Waiting for Postgres to become healthy..."
  for _ in $(seq 1 60); do
    status="$(docker inspect --format '{{.State.Health.Status}}' leasebook-db 2>/dev/null || true)"
    if [ "$status" = "healthy" ]; then
      echo "Postgres is healthy."
      return 0
    fi
    sleep 2
  done
  echo "Postgres did not become healthy within the timeout." >&2
  exit 1
}

case "${1:-help}" in
  up)
    docker compose up -d
    wait_healthy
    ;;
  down)
    docker compose down
    ;;
  reset-db)
    docker compose down -v
    docker compose up -d
    wait_healthy
    ;;
  psql)
    docker compose exec db psql -U leasebook_migrator -d leasebook
    ;;
  *)
    echo "Usage: ./scripts/dev.sh {up|down|reset-db|psql}"
    ;;
esac
