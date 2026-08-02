// Private networking for the environment: one VNet with two dedicated subnets (Container Apps
// infrastructure + a subnet delegated to PostgreSQL Flexible Server), plus the private DNS zone that
// resolves the server's FQDN inside the VNet.
//
// Only deployed when main.bicep's `enablePrivateNetworking` is true (prod). Dev keeps public,
// firewall-gated Postgres access and no VNet at all.
//
// Sizing and delegation constraints below are taken from current Azure documentation (checked
// 2026-08-02) — see infra/README.md / ADR-027 for the citations:
//   * Container Apps, Workload profiles (v2) environment: minimum subnet size /27, and the subnet
//     MUST be delegated to `Microsoft.App/environments`. (The /23 minimum applies only to the legacy
//     Consumption-only (v1) environment type, whose subnet must NOT be delegated.)
//   * PostgreSQL Flexible Server VNet injection: smallest permitted subnet is /28 (16 addresses, of
//     which Azure reserves 5); a single HA-enabled server consumes 4 addresses. The subnet must be
//     delegated to `Microsoft.DBforPostgreSQL/flexibleServers` and may host nothing else.
//   * Neither subnet can be resized once resources exist in it, and a server cannot be moved to a
//     different VNet/subnet afterwards — so both are sized with headroom up front.
param prefix string
param location string

@description('VNet address space. Must not overlap peered networks or the Azure-reserved ranges listed in infra/env/prod.bicepparam.')
param vnetAddressPrefix string

@description('Container Apps infrastructure subnet (delegated to Microsoft.App/environments). Minimum /27 for a workload-profiles environment; sized larger for revision-swap headroom.')
param acaSubnetPrefix string

@description('PostgreSQL Flexible Server delegated subnet. Minimum /28; sized larger so a PITR restore can stand up a second HA server alongside the original before cutover.')
param postgresSubnetPrefix string

// The zone name must end in `.postgres.database.azure.com` and must not equal the server name.
// Created here rather than letting Azure auto-create one: auto-creation only happens via the portal
// or CLI — an ARM/Bicep deployment must supply an existing zone.
var privateDnsZoneName = 'privatelink.postgres.database.azure.com'

// Subnets are declared inline on the VNet rather than as standalone child resources: separate
// `subnets` resources race with (and get reverted by) concurrent writes to the parent VNet.
resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: '${prefix}-vnet'
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: [vnetAddressPrefix]
    }
    subnets: [
      {
        name: '${prefix}-snet-aca'
        properties: {
          addressPrefix: acaSubnetPrefix
          delegations: [
            {
              name: 'aca-delegation'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
      {
        name: '${prefix}-snet-pg'
        properties: {
          addressPrefix: postgresSubnetPrefix
          delegations: [
            {
              name: 'postgres-delegation'
              properties: {
                serviceName: 'Microsoft.DBforPostgreSQL/flexibleServers'
              }
            }
          ]
        }
      }
    ]
  }
}

// Private DNS zones are global resources; `location` is always 'global'.
resource privateDnsZone 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: privateDnsZoneName
  location: 'global'
}

// Without this link, nothing inside the VNet — including the Container App and the migrator job —
// can resolve <server>.postgres.database.azure.com. registrationEnabled stays false: the Postgres
// resource provider writes the server's A record itself; VMs must not auto-register here.
resource dnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: privateDnsZone
  name: '${prefix}-pgdns-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnet.id
    }
  }
}

output vnetId string = vnet.id
output acaSubnetId string = vnet.properties.subnets[0].id
output postgresSubnetId string = vnet.properties.subnets[1].id
output privateDnsZoneId string = privateDnsZone.id
output privateDnsZoneName string = privateDnsZone.name
