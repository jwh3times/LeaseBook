using '../main.bicep'

param env = 'prod'
param location = 'eastus2'
param postgresAdminLogin = 'lbadmin'
// Supplied at deploy time via the LEASEBOOK_PG_ADMIN_PASSWORD env var; never committed.
param postgresAdminPassword = readEnvironmentVariable('LEASEBOOK_PG_ADMIN_PASSWORD', '')

// --- Private networking -----------------------------------------------------------------------
// Prod's PostgreSQL server has no public endpoint, so it is injected into a VNet and reached over a
// private DNS zone. Turning this off would leave prod either unreachable or publicly exposed.
param enablePrivateNetworking = true

// PROPOSED address plan — NOT yet confirmed by the operator. Edit these three values before the
// first `az deployment sub create`; after the first apply they are effectively frozen (Azure will
// not resize a subnet that has resources in it, and a Flexible Server cannot move subnets).
//
// Why this block:
//   * 10.40.0.0/22 is a small, deliberately unusual slice of RFC 1918 space. It avoids 10.0.0.0/16
//     and 10.1.0.0/16 (the Azure portal/CLI defaults, and therefore the ranges most likely to
//     already exist in a hub or a partner VPN), 192.168.0.0/16 (home and small-office LANs), and
//     172.16.0.0/12 (Docker's default bridge sits at 172.17.0.0/16).
//   * It also stays clear of every range Azure itself reserves for a Container Apps workload-profiles
//     environment: 169.254.0.0/16, 172.30.0.0/16, 172.31.0.0/16, 192.0.2.0/24, 100.100.0.0/17,
//     100.100.128.0/19, 100.100.160.0/19, 100.100.192.0/19 — plus 100.64.0.0/10 (CGNAT).
//   * A /22 is 1024 addresses: enough for the two subnets below plus room for private endpoints
//     (Key Vault, ACR, Storage) if those ever move off public endpoints, without reserving a /16 the
//     project will never use.
//   * If dev is ever given private networking too, use 10.41.0.0/22 so the two can be peered.
param vnetAddressPrefix = '10.40.0.0/22'

// Container Apps infrastructure subnet, delegated to Microsoft.App/environments.
// Verified minimum for a Workload profiles (v2) environment is /27 (18 usable addresses after the
// 14 the platform takes). /23 is chosen anyway: a single-revision-mode revision swap temporarily
// DOUBLES the required address space, the subnet cannot be grown later, and the extra space costs
// nothing. 10.40.0.0/23 covers 10.40.0.0 - 10.40.1.255.
param acaSubnetPrefix = '10.40.0.0/23'

// PostgreSQL delegated subnet, delegated to Microsoft.DBforPostgreSQL/flexibleServers and usable by
// nothing else. Verified minimum is /28 (16 addresses, 5 reserved by Azure, 11 usable; one
// HA-enabled server consumes 4). /27 is chosen so a PITR drill can run the restored server
// alongside the original before cutover with room to spare. 10.40.2.0/27 covers
// 10.40.2.0 - 10.40.2.31; 10.40.2.32 - 10.40.3.255 is left free for future private endpoints.
param postgresSubnetPrefix = '10.40.2.0/27'

// --- Jobs -------------------------------------------------------------------------------------
// The deploy workflow overrides both tags with the promoted git SHA. 'latest' is only a placeholder
// for a manual template deploy. appImageTag is shared by the container app and the capabilities job
// (ADR-028) on purpose: the capability registry is source code, so an operator tool built from a
// different commit knows a different set of capabilities than the app it is being used to control.
param migratorImageTag = 'latest'
param appImageTag = 'latest'

// Both empty on the first deployment: Key Vault is created empty by this template and the Postgres
// roles do not exist until the operator runs infra/db/azure-bootstrap.md. Once each connection string
// is stored, set these to their full secret URIs (e.g.
// 'https://lb-prod-kv.vault.azure.net/secrets/connectionstrings-migrations') and redeploy; the jobs
// pick them up with no code change.
//
// They are DIFFERENT credentials and must stay so: migrations run as leasebook_migrator (DDL on
// public), the capabilities job as leasebook_app (DML on the four platform tables, under RLS's
// platform escape). Pointing both at one secret would hand an operator tool schema-owner rights.
param migrationsSecretUri = ''
param defaultSecretUri = ''
