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
          // All three probe types, declared explicitly. Two endpoints, three jobs.
          //
          // /api/health/ready is 503 until the capability seam has been proven reachable (ADR-028).
          // /api/health is a static response that touches no dependency. Which endpoint each probe
          // uses is the load-bearing decision: liveness RESTARTS the container, so pointing it at
          // anything that reads the database would turn one database blip into a simultaneous restart
          // storm across every replica. A dependency belongs behind readiness; a wedged process
          // belongs behind liveness.
          //
          // Startup — /api/health, and it must exist rather than be inherited. Program.cs does real
          // work before app.Run(): the capability registry validation and, in production, Hangfire
          // storage initialization. Kestrel is not bound during any of it, so a slow database can push
          // the first successful bind well past liveness's own 10 + 3x10 = 40s budget, and liveness
          // would kill a container that was merely still booting. A startup probe suspends liveness and
          // readiness until it passes, which is exactly the guard that gap needs. Azure does document
          // a default startup probe, but the sentence attributes it to the PORTAL adding defaults "if
          // you don't define each type" — too thin a guarantee for a Bicep deployment to lean on when
          // the failure mode is a crash loop. 5 + 10x15 = 155s.
          //
          // Readiness — patient, because the dependency is its whole job and it has no other. The
          // in-process CapabilityReadinessProbe backs off to a 15s ceiling, and against an unreachable
          // server each attempt first burns Npgsql's own 15s connect timeout, so its attempts land at
          // roughly 0s, 16s, 33s, 52s. Anything tighter than that gives up before the retry it is
          // waiting on has even been made. The budget here is 10 + 10x20 = 210s, chosen to clear a
          // Postgres Flexible Server zone-redundant HA failover (documented at 60-120s) with several
          // in-process retries left over.
          //
          // Liveness — fast, because its target is unambiguous. IsPopulated is never cleared once set,
          // so after startup readiness cannot fail for a dependency reason; the only thing left is a
          // hung process or an unresponsive Kestrel, and there is no reason to be patient about that.
          // 10 + 3x10 = 40s to a restart.
          //
          // Every failureThreshold is <= 10 deliberately. The Microsoft.App/containerApps ARM
          // reference states: "failureThreshold ... Minimum value is 1. Maximum value is 10." The
          // resource provider rejects anything larger at deploy time, and `az bicep build` does NOT
          // catch it — the property is typed `int`, so a template carrying 3000 compiles clean and
          // fails only against a live subscription. Tolerance is therefore bought with periodSeconds
          // (max 240), never by raising the threshold. Other ceilings, same source, all respected
          // here: initialDelaySeconds max 60, timeoutSeconds max 240, successThreshold max 10.
          probes: [
            {
              type: 'Startup'
              httpGet: {
                path: '/api/health'
                port: 8080
              }
              initialDelaySeconds: 5
              periodSeconds: 15
              timeoutSeconds: 3
              failureThreshold: 10
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/api/health/ready'
                port: 8080
              }
              initialDelaySeconds: 10
              periodSeconds: 20
              timeoutSeconds: 5
              failureThreshold: 10
              successThreshold: 1
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/api/health'
                port: 8080
              }
              initialDelaySeconds: 10
              periodSeconds: 10
              timeoutSeconds: 3
              failureThreshold: 3
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
