#!/usr/bin/env bash
# LeaseBook local dev helper (POSIX). Cross-platform twin: scripts/dev.ps1
#
#   ./scripts/dev.sh up         # start Postgres only (inner-loop dev: dotnet run + Vite on the host)
#   ./scripts/dev.sh down       # stop containers (keep data)
#   ./scripts/dev.sh reset-db   # wipe the data volume and re-bootstrap from scratch
#   ./scripts/dev.sh psql       # open psql in the container as the migrator role
#
#   ./scripts/dev.sh app-up     # build + run the WHOLE product in Docker (db→migrate→seed→app:8080)
#   ./scripts/dev.sh app-down   # stop the full stack (keep data)
#   ./scripts/dev.sh app-logs   # follow the app container's logs
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

app_port() { echo "${LEASEBOOK_APP_PORT:-8080}"; }

wait_app_healthy() {
  local port url
  port="$(app_port)"
  url="http://localhost:${port}/api/health"
  echo "Waiting for the app to answer at ${url} ..."
  for _ in $(seq 1 90); do
    if curl -fsS -o /dev/null --max-time 3 "$url" 2>/dev/null; then
      echo "App is up."
      return 0
    fi
    sleep 2
  done
  echo "App did not answer within the timeout. Check logs: ./scripts/dev.sh app-logs" >&2
  exit 1
}

case "${1:-help}" in
  up)
    docker compose up -d
    wait_healthy
    ;;
  down)
    docker compose --profile full down
    ;;
  reset-db)
    docker compose --profile full down -v
    docker compose up -d
    wait_healthy
    ;;
  psql)
    docker compose exec db psql -U leasebook_migrator -d leasebook
    ;;
  app-up)
    docker compose --profile full up -d --build
    wait_app_healthy
    port="$(app_port)"
    echo ""
    echo "LeaseBook is running: http://localhost:${port}"
    echo "Sign in (DEV ONLY): renee.calloway@tarheelpg.test / Tarheel-Trust-2026!"
    echo "Logs: ./scripts/dev.sh app-logs   ·   Stop: ./scripts/dev.sh app-down"
    ;;
  app-down)
    docker compose --profile full down
    ;;
  app-logs)
    docker compose logs -f app
    ;;
  *)
    echo "Usage: ./scripts/dev.sh {up|down|reset-db|psql|app-up|app-down|app-logs}"
    ;;
esac
