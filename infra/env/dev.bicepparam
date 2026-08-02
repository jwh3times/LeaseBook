using '../main.bicep'

param env = 'dev'
param location = 'eastus2'
param postgresAdminLogin = 'lbadmin'
// Supplied at deploy time via the LEASEBOOK_PG_ADMIN_PASSWORD env var; never committed.
param postgresAdminPassword = readEnvironmentVariable('LEASEBOOK_PG_ADMIN_PASSWORD', '')

// Dev deliberately stays on public, firewall-gated Postgres (AllowAzureServices) so the CI
// migration job on a GitHub-hosted runner can reach it. No VNet, no private DNS zone, no subnet
// delegation, and the Container Apps environment stays on the platform-managed network — dev is
// unchanged by the prod networking work. The address-plan parameters are not set here because
// network.bicep is not deployed at all when this is false.
param enablePrivateNetworking = false

// The migrator job is created in both environments so the prod migration path gets rehearsed here
// first, but dev's deploy workflow still migrates from the runner. These stay at their defaults:
// nothing pushes a leasebook-migrator image or stores a migrator secret for dev yet, so the job
// exists un-armed and is never started.
param migratorImageTag = 'latest'
param migrationsSecretUri = ''
