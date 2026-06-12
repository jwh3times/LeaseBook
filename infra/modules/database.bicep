// PostgreSQL Flexible Server 18 (P3). dev: Burstable B1ms + public access (firewall-gated); prod:
// General Purpose + zone-redundant HA + longer PITR + public access disabled (requires vnet — see
// infra/README.md). The three app roles are NOT created here (Bicep can't) — see azure-bootstrap.md.
@allowed(['dev', 'prod'])
param env string
param prefix string
param location string
param adminLogin string

@secure()
param adminPassword string

var isProd = env == 'prod'

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: '${prefix}-pg'
  location: location
  sku: {
    name: isProd ? 'Standard_D2ds_v5' : 'Standard_B1ms'
    tier: isProd ? 'GeneralPurpose' : 'Burstable'
  }
  properties: {
    version: '18'
    administratorLogin: adminLogin
    administratorLoginPassword: adminPassword
    storage: {
      storageSizeGB: 32
    }
    backup: {
      backupRetentionDays: isProd ? 35 : 7
      geoRedundantBackup: isProd ? 'Enabled' : 'Disabled'
    }
    highAvailability: {
      mode: isProd ? 'ZoneRedundant' : 'Disabled'
    }
    network: {
      // Prod must not be publicly reachable; production deploys wire delegatedSubnetResourceId +
      // a private DNS zone (documented in infra/README.md). Dev stays public + firewall-gated.
      publicNetworkAccess: isProd ? 'Disabled' : 'Enabled'
    }
  }
}

resource leasebookDb 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgres
  name: 'leasebook'
}

// Dev only: let Azure services (the CI migration job) reach the server. Prod uses private access.
resource allowAzureServices 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = if (!isProd) {
  parent: postgres
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

output serverName string = postgres.name
output fqdn string = postgres.properties.fullyQualifiedDomainName
