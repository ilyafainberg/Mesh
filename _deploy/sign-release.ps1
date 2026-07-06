<#
  Signs the Mesh release binaries + installer with Azure Trusted Signing.

  Prerequisites (one-time, see _deploy/CODE-SIGNING.md):
    1. Identity validation approved in the Azure portal (Organization).
    2. A certificate profile created under the mesh-signing account.
    3. A service principal (app registration) granted the
       "Trusted Signing Certificate Profile Signer" role on the account, and the
       env vars below set so the tool can authenticate.

  Required environment variables:
    AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET  (the signing service principal)

  Usage:
    ./sign-release.ps1 -ClientDir _deploy\client-release\Mesh-win-x64 `
                       -Installer _deploy\artifacts\Mesh-Setup-vX.Y.Z.exe `
                       -CertProfile mesh-cert
#>
param(
  [Parameter(Mandatory = $true)][string]$ClientDir,
  [string]$Installer,
  [string]$Account = "mesh-signing",
  [string]$CertProfile = "mesh-cert",
  [string]$Endpoint = "https://neu.codesigning.azure.net/"
)
$ErrorActionPreference = "Stop"

foreach ($v in "AZURE_TENANT_ID","AZURE_CLIENT_ID","AZURE_CLIENT_SECRET") {
  if (-not (Get-Item "env:$v" -ErrorAction SilentlyContinue)) { throw "Missing env var $v (signing service principal)." }
}

# Sign the app's own executables inside the published client. Signing the exe (and the bundled
# TotalControl.exe) is what removes the SmartScreen "unknown publisher" warning; the giant runtime
# DLLs do not need signing, so we target just the Mesh + TotalControl binaries for speed.
$targets = @(
  (Join-Path $ClientDir "Mesh.App.exe"),
  (Join-Path $ClientDir "mcp\totalcontrol\TotalControl.exe")
) | Where-Object { Test-Path $_ }

Write-Host "Signing $($targets.Count) client binaries..."
foreach ($t in $targets) {
  sign code trusted-signing $t `
    --trusted-signing-account $Account `
    --trusted-signing-certificate-profile $CertProfile `
    --trusted-signing-endpoint $Endpoint `
    --description "Mesh" `
    --description-url "https://meshrelay.net"
}

# Sign the installer (the file most users actually download and run).
if ($Installer -and (Test-Path $Installer)) {
  Write-Host "Signing installer: $Installer"
  sign code trusted-signing $Installer `
    --trusted-signing-account $Account `
    --trusted-signing-certificate-profile $CertProfile `
    --trusted-signing-endpoint $Endpoint `
    --description "Mesh Setup" `
    --description-url "https://meshrelay.net"
}

Write-Host "Signing complete. Re-zip the client + installer AFTER signing so the signatures ship."
