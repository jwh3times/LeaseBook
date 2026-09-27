#!/usr/bin/env pwsh
# Dedicated, disposable payment database on the existing development Postgres container.
[CmdletBinding()]
param(
    [Parameter(Position = 0)][ValidateSet('init', 'reset')][string]$Action = 'init',
    [string]$Container = 'leasebook-db',
    [int]$Port = 5632
)
$ErrorActionPreference = 'Stop'
$fixtureRoot = Split-Path -Parent $PSScriptRoot
$manifest = Join-Path $fixtureRoot 'payment-fixture.local'
$database = 'leasebook_payment_fixture'
function Sql([string]$databaseName, [string]$query) {
    $result = $query | docker exec -i $Container psql -U postgres -d $databaseName -v ON_ERROR_STOP=1 -At
    if ($LASTEXITCODE -ne 0) { throw 'Fixture database command failed.' }
    return $result
}
Push-Location $fixtureRoot
$saved = @{}
foreach ($key in @('ASPNETCORE_ENVIRONMENT', 'ConnectionStrings__Default', 'ConnectionStrings__Migrations', 'Payments__ManifestPath', 'Payments__Mode')) {
    $saved[$key] = [Environment]::GetEnvironmentVariable($key)
}
try {
    if ($Action -eq 'reset') {
        if (!(Test-Path -LiteralPath $manifest)) { throw 'Reset requires the fixture manifest.' }
        $config = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
        if ($config.Payments.Mode -ne 'Simulation' -or $config.Payments.Fixtures.Count -ne 2) { throw 'Not a fixture manifest.' }
        $orgCount = Sql $database 'SELECT count(*) FROM orgs;'
        if ($orgCount -ne '2') { throw 'Reset refused: database contains unexpected organizations.' }
        foreach ($binding in $config.Payments.Fixtures) {
            # Parse before interpolation; no manifest text becomes SQL identifiers or syntax.
            $orgId = [guid]$binding.OrgId
            $generation = [guid]$binding.Generation
            $bankId = [guid]$binding.BankId
            $match = Sql $database "SELECT count(*) FROM payment_fixtures WHERE org_id = '$orgId' AND id = '$generation' AND bank_id = '$bankId';"
            if ($match -ne '1') { throw 'Reset refused: fixture generation does not match the database.' }
        }
        # Fixed database name, verified markers above. Active hosts must be stopped first.
        Sql 'postgres' "DROP DATABASE $database;" | Out-Null
        Remove-Item -LiteralPath $manifest
    }
    if (Test-Path -LiteralPath $manifest) { throw 'Manifest exists. Restart with it; use reset only to discard the fixture.' }
    Sql 'postgres' "CREATE DATABASE $database OWNER leasebook_migrator;" | Out-Null
    # Reuse canonical role grants/schema defaults, without recreating cluster-wide roles or demo DB.
    $bootstrap = Get-Content (Join-Path $fixtureRoot 'infra/db/bootstrap.sql') -Raw
    $schema = ($bootstrap -split '\\connect leasebook', 2)[1]
    if ([string]::IsNullOrWhiteSpace($schema)) { throw 'Canonical bootstrap schema section not found.' }
    Sql $database $schema | Out-Null
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ConnectionStrings__Default = "Host=localhost;Port=$Port;Database=$database;Username=leasebook_app;Password=dev_app_pw"
    $env:ConnectionStrings__Migrations = "Host=localhost;Port=$Port;Database=$database;Username=leasebook_migrator;Password=dev_migrator_pw"
    $env:Payments__ManifestPath = $null
    $env:Payments__Mode = 'Disabled'
    dotnet ef database update --project src/LeaseBook.Web --context AppDbContext
    if ($LASTEXITCODE -ne 0) { throw 'Fixture migration failed.' }
    dotnet run --project src/LeaseBook.Web --no-launch-profile -- payment-simulation init $manifest
    if ($LASTEXITCODE -ne 0) { throw 'Fixture bootstrap failed; inspect the dedicated database before retrying.' }
    Write-Host 'Payment fixture initialized. See docs/runbooks/payment-simulation.md for host, driver and reset commands.'
} finally {
    foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
    Pop-Location
}
