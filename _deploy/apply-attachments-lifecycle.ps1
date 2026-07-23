# Applies the 14-day attachment-blob expiry lifecycle rule to the relay's storage account.
#
# Attachments live in the private "attachments" blob container. Every blob is deleted 14 days after
# creation, matching the relay's 14-day offline-inbox TTL, so an attachment never outlives its message.
# The relay uses only data-plane SAS URLs, so this management-plane policy is applied out of band, once
# per environment. Re-running is idempotent (it overwrites the account's management policy).
#
# Prerequisites: az login, and Storage Blob Data + management access to the account.
#
#   ./apply-attachments-lifecycle.ps1                      # defaults: meshrelaydl / rg-mesh
#   ./apply-attachments-lifecycle.ps1 -Account myacct -ResourceGroup my-rg

param(
    [string]$Account       = 'meshrelaydl',
    [string]$ResourceGroup = 'rg-mesh',
    [string]$PolicyPath    = (Join-Path $PSScriptRoot 'relay/attachments-lifecycle.json')
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $PolicyPath)) { throw "policy file not found: $PolicyPath" }

Write-Host "Applying attachments lifecycle policy to $Account (resource group $ResourceGroup)..."
az storage account management-policy create `
    --account-name $Account `
    --resource-group $ResourceGroup `
    --policy "@$PolicyPath"

Write-Host "Done. Blobs under the attachments container now auto-delete 14 days after creation."
