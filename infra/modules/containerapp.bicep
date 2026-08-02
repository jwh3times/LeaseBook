// User-assigned managed identity + Container Apps environment + the app + the one-shot migrator job.
// The identity pulls from ACR (AcrPull) and reads secrets from Key Vault (Key Vault Secrets User).
// Dev scales to zero.
param prefix string
param location string
@allowed(['dev', 'prod'])
param env string
param acrLoginServer string
param acrName string
param keyVaultName string
@secure()
param appInsightsConnectionString string
param logAnalyticsCustomerId string
param logAnalyticsWorkspaceId string

@description('Resource id of the subnet delegated to Microsoft.App/environments. Empty means the platform-managed network (dev).')
param infrastructureSubnetId string = ''

@description('Tag of the leasebook-migrator image the migration job runs. The deploy workflow pins this to the git SHA it promoted.')
param migratorImageTag string = 'latest'

@description('Full Key Vault secret URI holding the leasebook_migrator connection string, e.g. https://lb-prod-kv.vault.azure.net/secrets/connectionstrings-migrations. Empty until the operator has bootstrapped the Postgres roles and stored the secret — see infra/db/azure-bootstrap.md.')
param migrationsSecretUri string = ''

var isProd = env == 'prod'
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
var usePrivateNetworking = !empty(infrastructureSubnetId)
var haveMigrationsSecret = !empty(migrationsSecretUri)

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${prefix}-id'
  location: location
}

resource acr 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' existing = {
  name: acrName
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: last(split(logAnalyticsWorkspaceId, '/'))
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: acr
  name: guid(acr.id, identity.id, acrPullRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource keyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, identity.id, keyVaultSecretsUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${prefix}-cae'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalyticsCustomerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
    // `workloadProfiles` is declared explicitly rather than relying on the API default, because the
    // environment TYPE decides whether the subnet delegation in network.bicep is legal: a Workload
    // profiles (v2) environment REQUIRES delegation to Microsoft.App/environments, while the legacy
    // Consumption-only (v1) type forbids any delegation. Naming the profile pins v2.
    //
    // Declared in BOTH environments, deliberately. Prod needs it for the delegation to be legal;
    // dev takes it so the two environments are the same type. Environment type cannot be changed in
    // place — closing this gap later would mean recreating dev's environment — and nothing is
    // provisioned yet, so parity is free today and expensive at any later date.
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    // The VNet leg stays prod-only: dev keeps the platform-managed network, no subnet, no DNS zone.
    // `internal: false` keeps ingress external (inbound routes via the platform's public IP, not the
    // subnet) — the app is a public SaaS front end; only the DB leg is private.
    ...(usePrivateNetworking
      ? {
          vnetConfiguration: {
            infrastructureSubnetId: infrastructureSubnetId
            internal: false
          }
        }
      : {})
  }
}

resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-app'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: environment.id
    // Only valid on a workload-profiles (v2) environment, so it is omitted when there is no VNet.
    ...(usePrivateNetworking ? { workloadProfileName: 'Consumption' } : {})
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      registries: [
        {
          server: acrLoginServer
          identity: identity.id
        }
      ]
      // ConnectionStrings__Default is added as a Key Vault secret reference at deploy time
      // (keyVaultUrl + the identity). ConnectionStrings__Migrations deliberately never appears here:
      // it belongs to the migrator job below, so the running app cannot reach the DDL-capable role.
      secrets: [
        {
          name: 'appinsights-connection-string'
          value: appInsightsConnectionString
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'web'
          image: '${acrLoginServer}/leasebook:latest'
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              secretRef: 'appinsights-connection-string'
            }
          ]
        }
      ]
      scale: {
        minReplicas: isProd ? 1 : 0
        maxReplicas: isProd ? 5 : 2
      }
    }
  }
}

// One-shot migration job. This is how schema changes reach a VNet-injected database: a
// GitHub-hosted runner has no route to a server with no public endpoint, but a job running inside
// the Container Apps environment shares the VNet with it. It runs the `migrator` Dockerfile target
// (an EF bundle, ENTRYPOINT ./efbundle) as leasebook_migrator — never the app image, never the app
// role, and never at app startup.
//
// Trigger is Manual: the deploy workflow starts it and waits for the execution to succeed BEFORE
// swapping the app revision (`az containerapp job start` + poll). It is never started automatically.
resource migratorJob 'Microsoft.App/jobs@2024-03-01' = {
  name: '${prefix}-migrate'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    environmentId: environment.id
    ...(usePrivateNetworking ? { workloadProfileName: 'Consumption' } : {})
    configuration: {
      triggerType: 'Manual'
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      // 30 minutes: long enough for a large migration on a cold GeneralPurpose server, short enough
      // that a hung migration holding schema locks is surfaced rather than blocking a deploy forever.
      replicaTimeout: 1800
      // No automatic retry. A failed migration may have applied part of a batch; re-running blindly
      // is how you get a half-migrated schema. A human reads the log and decides.
      replicaRetryLimit: 0
      registries: [
        {
          server: acrLoginServer
          identity: identity.id
        }
      ]
      // The migrator connection string is a Key Vault reference resolved by the same user-assigned
      // identity that the app uses (Key Vault Secrets User, granted above). It is deliberately
      // OPTIONAL: on the very first deployment Key Vault is created empty and the Postgres roles do
      // not exist yet (they are bootstrapped by the operator after provisioning), so a hard-wired
      // reference would fail the initial `az deployment sub create`. Once the operator stores the
      // secret and passes its URI, the next deployment wires it with no code change; until then the
      // job exists but fails loudly at run time on a missing connection string.
      ...(haveMigrationsSecret
        ? {
            secrets: [
              {
                name: 'connectionstrings-migrations'
                keyVaultUrl: migrationsSecretUri
                identity: identity.id
              }
            ]
          }
        : {})
    }
    template: {
      containers: [
        {
          name: 'migrate'
          image: '${acrLoginServer}/leasebook-migrator:${migratorImageTag}'
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: haveMigrationsSecret
            ? [
                {
                  name: 'ConnectionStrings__Migrations'
                  secretRef: 'connectionstrings-migrations'
                }
              ]
            : []
        }
      ]
    }
  }
}

output fqdn string = containerApp.properties.configuration.ingress.fqdn
output identityClientId string = identity.properties.clientId
output migratorJobName string = migratorJob.name
