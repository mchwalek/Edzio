targetScope = 'subscription'

@description('Azure region for all resources.')
param location string = 'westeurope'

@description('Name of the resource group to create.')
param resourceGroupName string = 'rg-edzio-signaling'

@description('Name of the Container Apps managed environment.')
param environmentName string = 'cae-edzio-signaling'

@description('Name of the container app.')
param containerAppName string = 'edzio-signaling'

@description('Container image to deploy initially. CI/CD overwrites this after the first successful build.')
param containerImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
}

module containerApps 'container-apps.bicep' = {
  name: 'container-apps-deployment'
  scope: rg
  params: {
    location: location
    environmentName: environmentName
    containerAppName: containerAppName
    containerImage: containerImage
  }
}

@description('Name of the created resource group.')
output resourceGroupName string = resourceGroupName

@description('Fully qualified domain name the container app is reachable at.')
output containerAppFqdn string = containerApps.outputs.fqdn
