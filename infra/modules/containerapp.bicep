// User-assigned managed identity + Container Apps environment + the app + two manual-trigger jobs:
// the one-shot migrator (ADR-027) and the operator capabilities job (ADR-028).
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

@description('Tag of the leasebook (app) image. Used by BOTH the container app and the capabilities job, deliberately: the capability registry is source code, so a job built from a different commit knows a different set of capabilities than the app it is being used to control. The deploy workflow pins both to the promoted git SHA.')
param appImageTag string = 'latest'

@description('Full Key Vault secret URI holding the leasebook_migrator connection string, e.g. https://lb-prod-kv.vault.azure.net/secrets/connectionstrings-migrations. Empty until the operator has bootstrapped the Postgres roles and stored the secret — see infra/db/azure-bootstrap.md.')
param migrationsSecretUri string = ''

@description('Full Key Vault secret URI holding the leasebook_app connection string, e.g. https://lb-prod-kv.vault.azure.net/secrets/connectionstrings-default. Consumed by the capabilities job (ADR-028), which runs the app image as the app role. Empty until the operator has bootstrapped the Postgres roles and stored the secret — see infra/db/azure-bootstrap.md.')
param defaultSecretUri string = ''

var isProd = env == 'prod'
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
var usePrivateNetworking = !empty(infrastructureSubnetId)
var haveMigrationsSecret = !empty(migrationsSecretUri)
var haveDefaultSecret = !empty(defaultSecretUri)

// The capabilities job is the one container in this template whose STDOUT is read by a human rather
// than scraped by a machine, so EF's two loudest logging categories are turned off for it alone.
//
//   Database.Command — logs every statement at Information ("Executed DbCommand ...") and the failing
//     statement at Error. `capabilities list` would print a wall of SQL above its own table.
//   Update — logs the whole DbUpdateException stack at Error from SaveChanges, which lands ABOVE the
//     verb's own friendly explanation of the same failure and buries it.
//
// This suppresses ILogger output only. An exception the verb does not recognize still propagates out
// of Main and is printed by the runtime with its full stack (CapabilitiesCommand.Describe returns null
// for anything unrecognized, deliberately), so nothing unexpected is hidden by this — only the
// duplicate narration of failures the verb has already explained better.
var operatorFacingLogging = [
  {
    name: 'Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command'
    value: 'None'
  }
  {
    name: 'Logging__LogLevel__Microsoft.EntityFrameworkCore.Update'
    value: 'None'
  }
]

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
          image: '${acrLoginServer}/leasebook:${appImageTag}'
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
  // Same ordering argument as the capabilities job below, and it bites hardest here: deploy-prod
  // starts this job immediately after the deployment returns, so it is the tightest race in the
  // template. A job execution that cannot pull or cannot resolve its secret FAILS — unlike a container
  // app revision, whose image pull the platform retries until it goes healthy.
  dependsOn: [
    acrPull
    keyVaultSecretsUser
  ]
}

// Manual-trigger job for platform capability operations (ADR-028). Flags, entitlements and cohorts
// are controlled by a CLI verb that lives inside the APP image, and prod's database is VNet-injected
// with no public endpoint (ADR-027) — there is no shell into that network and nothing for a runner to
// connect to. So, exactly like migrations, the only route to the database is from inside the
// Container Apps environment. Without this job "roll out and roll back without a deploy" is not
// delivered, and a kill switch that cannot be reached in production is not a kill switch.
//
// Three things differ from migratorJob, and each is deliberate:
//
//  1. It runs the APP image, not the migrator image. The verb is `LeaseBook.Web`'s, and the capability
//     registry is source code — a job built from a different commit would know a different set of
//     capabilities than the app it is being used to control, which is how you disable a flag that the
//     running replicas have never heard of.
//  2. It runs as leasebook_app (ConnectionStrings__Default), not leasebook_migrator. The verb needs
//     DML on four tables, not DDL, and the app role is what the RLS platform-escape policies are
//     written against. Handing this job the migrator credential would over-privilege an operator tool
//     AND change the role the writes land under.
//  3. It carries no schedule. A capability flip is an operator decision, taken during an incident or a
//     rollout; nothing about it should ever happen on a timer.
resource capabilitiesJob 'Microsoft.App/jobs@2024-03-01' = {
  name: '${prefix}-capabilities'
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
      // Manual only. There is no scheduleTriggerConfig and no eventTriggerConfig here, and there must
      // not be: this job's whole purpose is to apply a decision a human has already made.
      triggerType: 'Manual'
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      // Ten minutes, and the budget is mostly NOT the write. The work itself is a single-row
      // transaction — a capability operation that takes minutes is wrong — but replicaTimeout is a
      // wall-clock kill deadline over image pull + host boot + the transaction. This program's own
      // documented cold-start budget is 155s (see the startup probe above), and a first pull of the
      // app image on a cold node is tens of seconds more, so the 300s that "single-row write" suggests
      // would leave under a minute of margin and could kill an operator's kill switch mid-boot.
      //
      // A timeout is therefore NOT evidence that the write did not happen: it can land after commit
      // and before the process exits. `capabilities list` is the only way to find out, and the runbook
      // says so — entitlements are append-only, so a blind re-run appends a second event rather than
      // being idempotent.
      replicaTimeout: 600
      // No automatic retry, same reasoning as the migrator and then some: `grant`/`revoke` APPEND
      // events, so a silent retry writes a second one into an append-only table that can never be
      // corrected. An operator reads the output and decides.
      replicaRetryLimit: 0
      registries: [
        {
          server: acrLoginServer
          identity: identity.id
        }
      ]
      // The app connection string is a Key Vault reference resolved by the same user-assigned identity
      // the app itself uses (Key Vault Secrets User, granted above), so no credential enters this
      // template or any workflow. OPTIONAL for the same reason as the migrator's: Key Vault is created
      // empty by this deployment and the Postgres roles do not exist until the operator bootstraps
      // them, so a hard-wired reference would fail the very first `az deployment sub create`. Until the
      // URI is supplied the job exists un-armed and fails loudly at run time on a missing connection
      // string; supplying it later is a redeploy, not a code change.
      ...(haveDefaultSecret
        ? {
            secrets: [
              {
                name: 'connectionstrings-default'
                keyVaultUrl: defaultSecretUri
                identity: identity.id
              }
            ]
          }
        : {})
    }
    template: {
      containers: [
        {
          name: 'capabilities'
          image: '${acrLoginServer}/leasebook:${appImageTag}'
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          // No `args` here, deliberately: `az containerapp job start --args` supplies them per
          // execution, so ONE job definition serves every subcommand — list, flag enable/disable,
          // grant, revoke, cohort add/remove. A job per verb would multiply the resource, the RBAC
          // surface and the runbook by seven for no gain. The image sets ENTRYPOINT and no CMD, so
          // per-execution args become the CMD and are appended to `dotnet LeaseBook.Web.dll`.
          //
          // LEASEBOOK_OPERATOR is likewise per-execution and is NOT defaulted here. It names the human
          // or system accountable for the change, and every row this verb writes is append-only — a
          // baked-in value would be a permanent, plausible-looking lie in the platform audit trail,
          // which is worse than none. Its absence is not silent either: outside Development the verb
          // REFUSES every mutating subcommand when it is unset (CapabilitiesCommand), because process
          // identity in this container is an ephemeral pod name that attributes a change to nobody.
          env: concat(
            operatorFacingLogging,
            haveDefaultSecret
              ? [
                  {
                    name: 'ConnectionStrings__Default'
                    secretRef: 'connectionstrings-default'
                  }
                ]
              : []
          )
        }
      ]
    }
  }
  // Neither role assignment is reachable through a property reference, so ARM would otherwise be free
  // to create this job before either exists. Its first execution needs BOTH: AcrPull to pull the app
  // image, Key Vault Secrets User to resolve the connection-string reference. Ordering them is not a
  // guarantee — ARM having created a role assignment is not the same as Entra having propagated it,
  // which can lag by minutes — but it removes the half of the race that is ours to remove. A pull or
  // secret failure on a first execution shortly after deployment is the other half; wait and re-run.
  dependsOn: [
    acrPull
    keyVaultSecretsUser
  ]
}

output fqdn string = containerApp.properties.configuration.ingress.fqdn
output identityClientId string = identity.properties.clientId
output migratorJobName string = migratorJob.name
output capabilitiesJobName string = capabilitiesJob.name
