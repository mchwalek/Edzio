@description('Azure region for the Container Apps environment and app.')
param location string

@description('Name of the Container Apps managed environment.')
param environmentName string

@description('Name of the container app.')
param containerAppName string

@description('Container image to run. A placeholder until CI/CD pushes the real image.')
param containerImage string

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: environmentName
  location: location
  properties: {}
}

resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: containerAppName
  location: location
  properties: {
    environmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          name: containerAppName
          image: containerImage
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
      }
    }
  }
}

@description('Fully qualified domain name the container app is reachable at.')
output fqdn string = containerApp.properties.configuration.ingress.fqdn
