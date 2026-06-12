#!/usr/bin/env pwsh
# LeaseBook local dev helper (Windows / pwsh). Cross-platform twin: scripts/dev.sh
#
#   ./scripts/dev.ps1 up         # start Postgres, wait until healthy
#   ./scripts/dev.ps1 down       # stop containers (keep data)
#   ./scripts/dev.ps1 reset-db   # wipe the data volume and re-bootstrap from scratch
#   ./scripts/dev.ps1 psql       # open psql in the container as the migrator role

[CmdletBinding()]
param(
  [Parameter(Position = 0)]
  [ValidateSet('up', 'down', 'reset-db', 'psql', 'help')]
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

Push-Location $repoRoot
try {
  switch ($Command) {
    'up' {
      docker compose up -d
      Wait-Healthy
    }
    'down' {
      docker compose down
    }
    'reset-db' {
      docker compose down -v
      docker compose up -d
      Wait-Healthy
    }
    'psql' {
      docker compose exec db psql -U leasebook_migrator -d leasebook
    }
    default {
      Write-Host 'Usage: ./scripts/dev.ps1 {up|down|reset-db|psql}'
    }
  }
}
finally {
  Pop-Location
}
