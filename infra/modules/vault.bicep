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

// F8 / ADR-041: the key that wraps the Data Protection keyring. The keyring itself lives in Postgres
// so it survives a container recreate; this key is what keeps its material out of the same database
// as the ciphertext it protects. RSA rather than a secret because Data Protection wraps with an
// asymmetric key operation, and the private half never leaves the vault.
resource dataProtectionKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = {
  parent: vault
  name: 'dataprotection'
  properties: {
    kty: 'RSA'
    keySize: 2048
    keyOps: ['wrapKey', 'unwrapKey']
    attributes: {
      enabled: true
    }
  }
}

output name string = vault.name
output id string = vault.id

// Consumed by the container app as DataProtection__KeyVaultKeyUri. Versionless on purpose: pinning a
// version would make key rotation a redeploy, and Data Protection resolves the current version.
output dataProtectionKeyUri string = '${vault.properties.vaultUri}keys/${dataProtectionKey.name}'
