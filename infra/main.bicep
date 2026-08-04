// LeaseBook infrastructure — subscription-scoped entry point. Creates the resource group and wires
// the per-domain modules. Authored for dev + prod; deployment is gated on operator Azure access.
// Validate: az bicep build --file infra/main.bicep
// What-if:  az deployment sub what-if --location eastus2 --template-file infra/main.bicep \
//             --parameters infra/env/dev.bicepparam
targetScope = 'subscription'

@allowed(['dev', 'prod'])
param env string

param location string = 'eastus2'

@description('PostgreSQL administrator login (the three app roles are created separately — see infra/db/azure-bootstrap.md).')
param postgresAdminLogin string

@secure()
@description('PostgreSQL administrator password (supply at deploy time; never commit).')
param postgresAdminPassword string

@description('Deploy the VNet layer and inject Postgres + Container Apps into it. true for prod (the server has no public endpoint); false for dev, which keeps public, firewall-gated access for the CI migration job. When false, no networking resources are created and dev is byte-for-byte what it was before.')
param enablePrivateNetworking bool = false

@description('VNet address space. Only used when enablePrivateNetworking is true. PROPOSED default — confirm against any existing hub/peer/VPN ranges before the first apply (ADR-027).')
param vnetAddressPrefix string = '10.40.0.0/22'

@description('Container Apps infrastructure subnet. Minimum /27 for a workload-profiles environment; /23 here for revision-swap and scale headroom, because a subnet cannot be resized once the environment exists.')
param acaSubnetPrefix string = '10.40.0.0/23'

@description('PostgreSQL Flexible Server delegated subnet. Minimum /28; /27 here so a PITR restore can run a second HA server alongside the original before cutover.')
param postgresSubnetPrefix string = '10.40.2.0/27'

@description('Tag of the leasebook-migrator image the migration job runs. Pinned to the promoted git SHA by the deploy workflow.')
param migratorImageTag string = 'latest'

@description('Tag of the leasebook (app) image, shared by the container app and the capabilities job (ADR-028). Pinned to the promoted git SHA by the deploy workflow.')
param appImageTag string = 'latest'

@description('Key Vault secret URI for the leasebook_migrator connection string. Empty until the operator has bootstrapped the Postgres roles and stored it — see infra/db/azure-bootstrap.md.')
param migrationsSecretUri string = ''

@description('Key Vault secret URI for the leasebook_app connection string, consumed by the capabilities job (ADR-028). Empty until the operator has bootstrapped the Postgres roles and stored it — see infra/db/azure-bootstrap.md.')
param defaultSecretUri string = ''

// Naming convention: lb-<env>-<resource> (see infra/README.md).
var prefix = 'lb-${env}'

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: '${prefix}-rg'
  location: location
}

// Networking comes first: both the database and the Container Apps environment are injected into
// its subnets, and neither can be moved afterwards.
module network 'modules/network.bicep' = if (enablePrivateNetworking) {
  scope: rg
  name: 'network'
  params: {
    prefix: prefix
    location: location
    vnetAddressPrefix: vnetAddressPrefix
    acaSubnetPrefix: acaSubnetPrefix
    postgresSubnetPrefix: postgresSubnetPrefix
  }
}

module monitoring 'modules/monitoring.bicep' = {
  scope: rg
  name: 'monitoring'
  params: { prefix: prefix, location: location }
}

module registry 'modules/registry.bicep' = {
  scope: rg
  name: 'registry'
  params: { env: env, location: location }
}

module storage 'modules/storage.bicep' = {
  scope: rg
  name: 'storage'
  params: { env: env, location: location }
}

module database 'modules/database.bicep' = {
  scope: rg
  name: 'database'
  params: {
    prefix: prefix
    location: location
    env: env
    adminLogin: postgresAdminLogin
    adminPassword: postgresAdminPassword
    // ARM's if() short-circuits, so the module reference is never evaluated when the condition is
    // false — these resolve to '' for dev, which database.bicep reads as "stay on public access".
    delegatedSubnetResourceId: enablePrivateNetworking ? network!.outputs.postgresSubnetId : ''
    privateDnsZoneArmResourceId: enablePrivateNetworking ? network!.outputs.privateDnsZoneId : ''
  }
}

module vault 'modules/vault.bicep' = {
  scope: rg
  name: 'vault'
  params: { prefix: prefix, location: location }
}

module app 'modules/containerapp.bicep' = {
  scope: rg
  name: 'app'
  params: {
    prefix: prefix
    location: location
    env: env
    acrLoginServer: registry.outputs.loginServer
    acrName: registry.outputs.name
    keyVaultName: vault.outputs.name
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    logAnalyticsCustomerId: monitoring.outputs.logAnalyticsCustomerId
    logAnalyticsWorkspaceId: monitoring.outputs.logAnalyticsWorkspaceId
    infrastructureSubnetId: enablePrivateNetworking ? network!.outputs.acaSubnetId : ''
    migratorImageTag: migratorImageTag
    appImageTag: appImageTag
    migrationsSecretUri: migrationsSecretUri
    defaultSecretUri: defaultSecretUri
  }
}

output resourceGroup string = rg.name
output acrLoginServer string = registry.outputs.loginServer
output keyVaultName string = vault.outputs.name
output appFqdn string = app.outputs.fqdn
output migratorJobName string = app.outputs.migratorJobName
output capabilitiesJobName string = app.outputs.capabilitiesJobName
