// Azure Container Registry (Basic). Image pull is via the app's managed identity (AcrPull),
// so admin user stays disabled.
@allowed(['dev', 'prod'])
param env string
param location string

// ACR names are global, alphanumeric, no hyphens.
var registryName = 'lb${env}acr'

resource registry 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: registryName
  location: location
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
  }
}

output name string = registry.name
output loginServer string = registry.properties.loginServer
output id string = registry.id
