// ADR-027 addendum: manual database administration inside the existing ACA network.
param prefix string
param location string
param environmentId string
param acrName string
param acrLoginServer string
param imageTag string
param adminLogin string
// The vault is empty on first apply. Arm only after all four secrets exist.
param secretsReady bool = false

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${prefix}-dbadmin-id'
  location: location
}

// Separate from the application vault: its existing vault-wide app grant must not expose admin.
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${prefix}-dbadmin-kv'
  location: location
  properties: {
    sku: { family: 'A', name: 'standard' }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
  }
}

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: acrName
}

var acrPullRole = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
var secretsUserRole = '4633458b-17de-408a-b874-0445c86b69e6'
resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: acr
  name: guid(acr.id, identity.id, acrPullRole)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRole)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}
resource secretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, identity.id, secretsUserRole)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', secretsUserRole)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

var credentials = [
  { name: 'postgres-admin-password', variable: 'PGPASSWORD' }
  { name: 'postgres-migrator-password', variable: 'LEASEBOOK_MIGRATOR_PASSWORD' }
  { name: 'postgres-app-password', variable: 'LEASEBOOK_APP_PASSWORD' }
  { name: 'postgres-ops-password', variable: 'LEASEBOOK_OPS_PASSWORD' }
]
var jobSecrets = [for credential in credentials: {
  name: credential.name
  keyVaultUrl: 'https://${vault.name}${environment().suffixes.keyvaultDns}/secrets/${credential.name}'
  identity: identity.id
}]
var credentialEnv = [for credential in credentials: {
  name: credential.variable
  secretRef: credential.name
}]

resource job 'Microsoft.App/jobs@2024-03-01' = {
  name: '${prefix}-dbadmin'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  properties: {
    environmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      replicaTimeout: 600
      replicaRetryLimit: 0
      registries: [{ server: acrLoginServer, identity: identity.id }]
      secrets: secretsReady ? jobSecrets : []
    }
    template: {
      containers: [{
        name: 'dbadmin'
        image: '${acrLoginServer}/leasebook-dbadmin:${imageTag}'
        resources: { cpu: json('0.5'), memory: '1Gi' }
        // Bare start refuses: the target and accountable operator must be selected per execution.
        args: ['refuse']
        env: concat([
          { name: 'PGUSER', value: adminLogin }
        ], secretsReady ? credentialEnv : [])
      }]
    }
  }
  dependsOn: [acrPull, secretsUser]
}

output jobName string = job.name
output vaultName string = vault.name
