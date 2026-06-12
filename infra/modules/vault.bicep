// Key Vault in RBAC mode. The app's managed identity is granted Key Vault Secrets User in the
// container-app module; real DB role passwords and connection strings live here only.
param prefix string
param location string

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: '${prefix}-kv'
  location: location
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    publicNetworkAccess: 'Enabled'
  }
}

output name string = vault.name
output id string = vault.id
