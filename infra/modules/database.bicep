// PostgreSQL Flexible Server 18 (P3). dev: Burstable B1ms + public access (firewall-gated); prod:
// General Purpose + zone-redundant HA + longer PITR + VNet-injected private access.
// The three app roles are NOT created here (Bicep can't) — see azure-bootstrap.md.
@allowed(['dev', 'prod'])
param env string
param prefix string
param location string
param adminLogin string

@secure()
param adminPassword string

@description('Resource id of the subnet delegated to Microsoft.DBforPostgreSQL/flexibleServers. Empty means public access (dev).')
param delegatedSubnetResourceId string = ''

@description('Resource id of the private DNS zone ending in .postgres.database.azure.com. Required whenever delegatedSubnetResourceId is set.')
param privateDnsZoneArmResourceId string = ''

var isProd = env == 'prod'
var usePrivateNetworking = !empty(delegatedSubnetResourceId)

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
    // VNet injection and publicNetworkAccess are mutually exclusive: per the ARM reference,
    // publicNetworkAccess "is only supported for servers that are not integrated into a virtual
    // network which is owned and provided by customer". A VNet-injected server has no public
    // endpoint at all, so setting the property alongside delegatedSubnetResourceId is rejected —
    // hence the whole object is switched rather than just adding fields.
    network: usePrivateNetworking
      ? {
          delegatedSubnetResourceId: delegatedSubnetResourceId
          privateDnsZoneArmResourceId: privateDnsZoneArmResourceId
        }
      : {
          publicNetworkAccess: isProd ? 'Disabled' : 'Enabled'
        }
  }
}

resource leasebookDb 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgres
  name: 'leasebook'
}

// Dev only: let Azure services (the CI migration job) reach the server. Prod uses private access.
// Firewall rules are meaningless on (and rejected for) a VNet-injected server, hence the extra guard.
resource allowAzureServices 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = if (!isProd && !usePrivateNetworking) {
  parent: postgres
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

output serverName string = postgres.name
output fqdn string = postgres.properties.fullyQualifiedDomainName
