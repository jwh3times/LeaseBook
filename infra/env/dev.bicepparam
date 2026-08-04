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

// Both jobs are created in both environments so the prod paths get rehearsed here first, but dev's
// deploy workflow still migrates from the runner. These stay at their defaults: nothing pushes a
// leasebook-migrator image or stores either secret for dev yet, so the jobs exist un-armed and are
// never started.
//
// The capabilities job (ADR-028) is worth arming in dev even though dev's Postgres IS publicly
// reachable and a laptop can run the verb directly: dev is where the prod invocation gets rehearsed,
// and an operator's first use of a kill switch should not be its first execution ever. Set
// defaultSecretUri once the dev app-role connection string is in dev's Key Vault.
param migratorImageTag = 'latest'
param appImageTag = 'latest'
param migrationsSecretUri = ''
param defaultSecretUri = ''
