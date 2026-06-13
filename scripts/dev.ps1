#!/usr/bin/env pwsh
# LeaseBook local dev helper (Windows / pwsh). Cross-platform twin: scripts/dev.sh
#
#   ./scripts/dev.ps1 up         # start Postgres only (inner-loop dev: dotnet run + Vite on the host)
#   ./scripts/dev.ps1 down       # stop containers (keep data)
#   ./scripts/dev.ps1 reset-db   # wipe the data volume and re-bootstrap from scratch
#   ./scripts/dev.ps1 psql       # open psql in the container as the migrator role
#
#   ./scripts/dev.ps1 app-up     # build + run the WHOLE product in Docker (db→migrate→seed→app:8080)
#   ./scripts/dev.ps1 app-down   # stop the full stack (keep data)
#   ./scripts/dev.ps1 app-logs   # follow the app container's logs

[CmdletBinding()]
param(
  [Parameter(Position = 0)]
  [ValidateSet('up', 'down', 'reset-db', 'psql', 'app-up', 'app-down', 'app-logs', 'help')]
  [string]$Command = 'help'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Wait-Healthy {
  Write-Host 'Waiting for Postgres to become healthy...'
  for ($i = 0; $i -lt 60; $i++) {
    $status = (docker inspect --format '{{.State.Health.Status}}' leasebook-db 2>$null)
    if ($status -eq 'healthy') { Write-Host 'Postgres is healthy.' -ForegroundColor Green; return }
    Start-Sleep -Seconds 2
  }
  throw 'Postgres did not become healthy within the timeout.'
}

function Get-AppPort {
  if ($env:LEASEBOOK_APP_PORT) { return $env:LEASEBOOK_APP_PORT }
  return '8080'
}

function Wait-AppHealthy {
  $port = Get-AppPort
  $url = "http://localhost:$port/api/health"
  Write-Host "Waiting for the app to answer at $url ..."
  for ($i = 0; $i -lt 90; $i++) {
    try {
      $resp = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 3
      if ($resp.StatusCode -eq 200) { Write-Host 'App is up.' -ForegroundColor Green; return }
    }
    catch { }
    Start-Sleep -Seconds 2
  }
  throw "App did not answer within the timeout. Check logs: ./scripts/dev.ps1 app-logs"
}

Push-Location $repoRoot
try {
  switch ($Command) {
    'up' {
      docker compose up -d
      Wait-Healthy
    }
    'down' {
      docker compose --profile full down
    }
    'reset-db' {
      docker compose --profile full down -v
      docker compose up -d
      Wait-Healthy
    }
    'psql' {
      docker compose exec db psql -U leasebook_migrator -d leasebook
    }
    'app-up' {
      # Build images, then bring up db → migrate → seed → app in dependency order.
      docker compose --profile full up -d --build
      Wait-AppHealthy
      $port = Get-AppPort
      Write-Host ''
      Write-Host "LeaseBook is running: http://localhost:$port" -ForegroundColor Green
      Write-Host 'Sign in (DEV ONLY): renee.calloway@tarheelpg.test / Tarheel-Trust-2026!'
      Write-Host 'Logs: ./scripts/dev.ps1 app-logs   ·   Stop: ./scripts/dev.ps1 app-down'
    }
    'app-down' {
      docker compose --profile full down
    }
    'app-logs' {
      docker compose logs -f app
    }
    default {
      Write-Host 'Usage: ./scripts/dev.ps1 {up|down|reset-db|psql|app-up|app-down|app-logs}'
    }
  }
}
finally {
  Pop-Location
}
