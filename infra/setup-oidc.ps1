<#
.SYNOPSIS
  One-time setup: creates an Entra ID app registration with a federated
  credential trusting GitHub Actions OIDC tokens for this repo's main
  branch, and grants it Contributor on the signaling server resource group.

.DESCRIPTION
  Run this once, manually, after infra/main.bicep has created the resource
  group. It is safe to re-run: it looks up existing resources by name
  instead of creating duplicates. Prints the three values to add as GitHub
  repository Variables (Settings > Secrets and variables > Actions >
  Variables) — AZURE_CLIENT_ID, AZURE_TENANT_ID, AZURE_SUBSCRIPTION_ID.
  These are identifiers, not secrets: OIDC means no password/secret ever
  leaves Azure.
#>
param(
    [string]$AppDisplayName = "edzio-signaling-deploy",
    [string]$ResourceGroupName = "rg-edzio-signaling",
    [string]$GitHubRepository = "mchwalek/Edzio",
    [string]$GitHubBranch = "main"
)

$ErrorActionPreference = "Stop"

Write-Host "Looking up subscription and tenant..."
$account = az account show | ConvertFrom-Json
$subscriptionId = $account.id
$tenantId = $account.tenantId

Write-Host "Looking up existing app registration '$AppDisplayName'..."
$existingApps = az ad app list --display-name $AppDisplayName | ConvertFrom-Json
if ($existingApps.Count -gt 0) {
    $appId = $existingApps[0].appId
    Write-Host "Found existing app registration: $appId"
} else {
    Write-Host "Creating app registration '$AppDisplayName'..."
    $app = az ad app create --display-name $AppDisplayName | ConvertFrom-Json
    $appId = $app.appId
    Write-Host "Created app registration: $appId"
}

Write-Host "Ensuring a service principal exists for the app..."
$existingSp = az ad sp list --filter "appId eq '$appId'" | ConvertFrom-Json
if ($existingSp.Count -eq 0) {
    az ad sp create --id $appId | Out-Null
    Write-Host "Created service principal for app $appId"
} else {
    Write-Host "Service principal already exists for app $appId"
}

$federatedCredentialName = "github-actions-$GitHubBranch"
Write-Host "Ensuring federated credential '$federatedCredentialName' exists..."
$existingCreds = az ad app federated-credential list --id $appId | ConvertFrom-Json
if ($existingCreds | Where-Object { $_.name -eq $federatedCredentialName }) {
    Write-Host "Federated credential '$federatedCredentialName' already exists"
} else {
    $subject = "repo:${GitHubRepository}:ref:refs/heads/${GitHubBranch}"
    $params = @{
        name = $federatedCredentialName
        issuer = "https://token.actions.githubusercontent.com"
        subject = $subject
        audiences = @("api://AzureADTokenExchange")
    } | ConvertTo-Json -Compress

    $tempFile = New-TemporaryFile
    Set-Content -LiteralPath $tempFile -Value $params
    az ad app federated-credential create --id $appId --parameters "@$tempFile"
    Remove-Item -LiteralPath $tempFile
    Write-Host "Created federated credential trusting $subject"
}

Write-Host "Ensuring 'Contributor' role assignment on resource group '$ResourceGroupName'..."
$scope = "/subscriptions/$subscriptionId/resourceGroups/$ResourceGroupName"
$existingAssignment = az role assignment list --assignee $appId --scope $scope --role Contributor | ConvertFrom-Json
if ($existingAssignment.Count -eq 0) {
    az role assignment create --assignee $appId --role Contributor --scope $scope | Out-Null
    Write-Host "Granted Contributor on $scope"
} else {
    Write-Host "Contributor role already assigned on $scope"
}

Write-Host ""
Write-Host "=== Add these as GitHub repository Variables (not Secrets) ==="
Write-Host "Settings > Secrets and variables > Actions > Variables tab, in $GitHubRepository"
Write-Host "AZURE_CLIENT_ID       = $appId"
Write-Host "AZURE_TENANT_ID       = $tenantId"
Write-Host "AZURE_SUBSCRIPTION_ID = $subscriptionId"
