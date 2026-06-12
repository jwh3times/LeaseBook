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

// Naming convention: lb-<env>-<resource> (see infra/README.md).
var prefix = 'lb-${env}'

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: '${prefix}-rg'
  location: location
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
  }
}

output resourceGroup string = rg.name
output acrLoginServer string = registry.outputs.loginServer
output keyVaultName string = vault.outputs.name
output appFqdn string = app.outputs.fqdn
