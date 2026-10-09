#!/usr/bin/env pwsh
# Dedicated, disposable payment database on the existing development Postgres container.
[CmdletBinding()]
param(
    [Parameter(Position = 0)][ValidateSet('init', 'reset')][string]$Action = 'init',
    [string]$Container = 'leasebook-db',
    [int]$Port = 5632,
    # Stripe sandbox connected accounts (acct_...), one fixture organization each. Given, the fixture is
    # the Stripe sandbox one, in its own database and manifest beside the simulation fixture.
    [string[]]$StripeAccount = @()
)
$ErrorActionPreference = 'Stop'
$fixtureRoot = Split-Path -Parent $PSScriptRoot
$sandbox = $StripeAccount.Count -gt 0
# Checked before anything is created: the bootstrap would refuse the same, with a database left behind.
foreach ($account in $StripeAccount) {
    if ($account -cnotmatch '^acct_[A-Za-z0-9]{1,95}$') { throw 'A Stripe account is acct_ followed by letters and digits.' }
}
if (@($StripeAccount | Select-Object -Unique).Count -ne $StripeAccount.Count) { throw 'Each Stripe account may be given once.' }
if ($StripeAccount.Count -gt 26) { throw 'At most 26 Stripe accounts, one fixture organization each.' }
$manifest = Join-Path $fixtureRoot ($sandbox ? 'payment-fixture-stripe.local' : 'payment-fixture.local')
$database = $sandbox ? 'leasebook_payment_fixture_stripe' : 'leasebook_payment_fixture'
$mode = $sandbox ? 'StripeSandbox' : 'Simulation'
function Sql([string]$databaseName, [string]$query) {
    $result = $query | docker exec -i $Container psql -U postgres -d $databaseName -v ON_ERROR_STOP=1 -At
    if ($LASTEXITCODE -ne 0) { throw 'Fixture database command failed.' }
    return $result
}
Push-Location $fixtureRoot
$saved = @{}
foreach ($key in @('ASPNETCORE_ENVIRONMENT', 'ConnectionStrings__Default', 'ConnectionStrings__Migrations', 'Payments__ManifestPath', 'Payments__Mode', 'Payments__Stripe__SecretKey')) {
    $saved[$key] = [Environment]::GetEnvironmentVariable($key)
}
try {
    if ($Action -eq 'reset') {
        if (!(Test-Path -LiteralPath $manifest)) { throw 'Reset requires the fixture manifest.' }
        $config = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
        # The manifest must be the one for this mode: two simulator fixtures, or one per organization
        # in the sandbox database. The count is the manifest's own, so it is compared, never trusted.
        $fixtures = @($config.Payments.Fixtures)
        if ($config.Payments.Mode -cne $mode -or $fixtures.Count -lt 1 -or (!$sandbox -and $fixtures.Count -ne 2)) { throw 'Not a fixture manifest.' }
        $orgCount = Sql $database 'SELECT count(*) FROM orgs;'
        if ($orgCount -ne "$($fixtures.Count)") { throw 'Reset refused: database contains unexpected organizations.' }
        foreach ($binding in $fixtures) {
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
    # A maintainer's exported sandbox key is refused by a host with payments disabled, which is what
    # runs below. Cleared for those processes and put back in finally; the bootstrap needs no key.
    $env:Payments__Stripe__SecretKey = $null
    dotnet ef database update --project src/LeaseBook.Web --context AppDbContext
    if ($LASTEXITCODE -ne 0) { throw 'Fixture migration failed.' }
    $accounts = @($StripeAccount | ForEach-Object { "--stripe-account=$_" })
    dotnet run --project src/LeaseBook.Web --no-launch-profile -- payment-simulation init $manifest @accounts
    if ($LASTEXITCODE -ne 0) { throw 'Fixture bootstrap failed; inspect the dedicated database before retrying.' }
    Write-Host 'Payment fixture initialized. See docs/runbooks/payment-simulation.md for host, driver and reset commands.'
} finally {
    foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
    Pop-Location
}
