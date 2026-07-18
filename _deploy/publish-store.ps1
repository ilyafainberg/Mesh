<#
  publish-store.ps1 - Build and publish the Mesh Windows installer to Microsoft Store.

  By default this runs release-win.ps1, which builds and signs the installer and uploads
  it to the public Azure Blob URL consumed by the Store. Use -SkipBuild when the matching
  release has already been built and uploaded.

  Required environment variables:
    MS_STORE_TENANT_ID
    MS_STORE_CLIENT_SECRET (the key for the Mesh Store Manager application)

  Optional environment overrides:
    MS_STORE_SELLER_ID (defaults to the Mesh seller account)
    MS_STORE_CLIENT_ID (defaults to the Mesh Store Manager application)

  Partner Center targets:
    Seller ID: 95246270
    Product: Mesh Relay
    Partner Center ID: cd4a1e7a-b612-419e-9503-f3c17e32bcc0
    Entra app: Mesh Store Manager (Manager (Windows))

  Usage:
    ./_deploy/publish-store.ps1 -Version 1.6.1
    ./_deploy/publish-store.ps1 -Version 1.6.1 -SkipBuild
    ./_deploy/publish-store.ps1 -Version 1.6.1 -SkipBuild -DraftOnly
    ./_deploy/publish-store.ps1 -Version 1.6.1 -DryRun

  This script contains no em-dash (U+2014) characters, per project rule.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [ValidatePattern('^\d+\.\d+\.\d+$')]
  [string]$Version,

  [string]$StoreProductId = "cd4a1e7a-b612-419e-9503-f3c17e32bcc0",
  [string]$StoreSellerId = "95246270",
  [string]$StoreClientId = "f119e9e3-b77a-4ba6-9fb8-ca6858a66883",
  [string]$InstallerUrl = "",
  [string]$NotesFile = "",
  [switch]$SkipBuild,
  [switch]$SkipGitHub,
  [switch]$SkipPush,
  [switch]$DraftOnly,
  [switch]$DryRun
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Deploy = $PSScriptRoot
$ReleaseWin = Join-Path $Deploy "release-win.ps1"
$Installer = Join-Path $Deploy "artifacts\Mesh-Setup-v$Version.exe"
$BlobBase = "https://meshrelaydl.blob.core.windows.net/releases"

if (-not $InstallerUrl) {
  $InstallerUrl = "$BlobBase/store/Mesh-Setup-v$Version.exe"
}

function Say($message)  { Write-Host "`n=== $message ===" -ForegroundColor Cyan }
function Ok($message)   { Write-Host "  [ok] $message" -ForegroundColor Green }
function Note($message) { Write-Host "  $message" -ForegroundColor Gray }
function Warn($message) { Write-Host "  [warn] $message" -ForegroundColor Yellow }
function Die($message)  { throw $message }

function Invoke-Native([string]$Executable, [string[]]$Arguments, [string]$Description) {
  & $Executable @Arguments
  if ($LASTEXITCODE -ne 0) {
    Die "$Description failed (exit $LASTEXITCODE)."
  }
}

function Invoke-NativeCapture([string]$Executable, [string[]]$Arguments, [string]$Description) {
  $output = @(& $Executable @Arguments)
  if ($LASTEXITCODE -ne 0) {
    Die "$Description failed (exit $LASTEXITCODE)."
  }
  return ($output -join [Environment]::NewLine)
}

function Set-JsonProperty([object]$Object, [string]$Name, [object]$Value) {
  if ($Object.PSObject.Properties[$Name]) {
    $Object.$Name = $Value
  } else {
    $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
  }
}

function Get-InstallerSignature([string]$Path) {
  for ($attempt = 1; $attempt -le 5; $attempt++) {
    try {
      return Get-AuthenticodeSignature $Path
    } catch [System.IO.IOException] {
      if ($attempt -eq 5) { throw }
      Start-Sleep -Seconds 2
    }
  }
}

Say "Mesh Microsoft Store publish v$Version"

if (-not $SkipBuild) {
  if (-not (Test-Path $ReleaseWin -PathType Leaf)) {
    Die "Windows release script not found at $ReleaseWin."
  }

  Say "Build, sign, and upload Windows release"
  $releaseArgs = @{ Version = $Version }
  if ($NotesFile) { $releaseArgs.NotesFile = $NotesFile }
  if ($SkipGitHub) { $releaseArgs.SkipGitHub = $true }
  if ($SkipPush) { $releaseArgs.SkipPush = $true }
  if ($DryRun) { $releaseArgs.DryRun = $true }

  & $ReleaseWin @releaseArgs
  if ($LASTEXITCODE -ne 0) {
    Die "Windows release failed (exit $LASTEXITCODE)."
  }
}

if (-not (Test-Path $Installer -PathType Leaf)) {
  Die "Signed installer not found at $Installer. Run without -SkipBuild first."
}

$signature = Get-InstallerSignature $Installer
if ($signature.Status -ne "Valid") {
  Die "Installer signature is not valid (status: $($signature.Status))."
}
Ok "signed installer: $Installer"
Note "Store package URL: $InstallerUrl"

try {
  $uri = [Uri]$InstallerUrl
  if (-not $uri.IsAbsoluteUri -or $uri.Scheme -ne "https") {
    Die "InstallerUrl must be an absolute HTTPS URL."
  }
} catch {
  Die "InstallerUrl is invalid: $InstallerUrl"
}

if ($DryRun) {
  Warn "DryRun: skipping URL check and Microsoft Store changes."
  exit 0
}

$remoteInstaller = Join-Path $env:TEMP "Mesh-Store-$([Guid]::NewGuid().ToString('N')).exe"
Say "Verify public installer"
try {
  Invoke-WebRequest -Uri $InstallerUrl -OutFile $remoteInstaller -UseBasicParsing
  $localHash = (Get-FileHash $Installer -Algorithm SHA256).Hash
  $remoteHash = (Get-FileHash $remoteInstaller -Algorithm SHA256).Hash
  if ($localHash -ne $remoteHash) {
    Die "Public installer does not match the signed local installer. Local SHA-256: $localHash; remote SHA-256: $remoteHash."
  }
} catch {
  Die "Installer verification failed for $InstallerUrl. $($_.Exception.Message)"
} finally {
  Remove-Item $remoteInstaller -Force -ErrorAction SilentlyContinue
}
Ok "installer URL matches local SHA-256"

if (-not (Get-Command "msstore" -ErrorAction SilentlyContinue)) {
  Die "Microsoft Store Developer CLI is missing. Install it with: winget install `"Microsoft Store Developer CLI`""
}

# Read credentials from the process first, then the user's persistent environment.
# Seller and client IDs are not secrets, so the confirmed Mesh values are safe defaults.
function Get-StoreEnvironmentValue([string]$Name) {
  $value = [Environment]::GetEnvironmentVariable($Name, "Process")
  if (-not $value) {
    $value = [Environment]::GetEnvironmentVariable($Name, "User")
  }
  return $value
}

$tenantId = Get-StoreEnvironmentValue "MS_STORE_TENANT_ID"
$clientSecret = Get-StoreEnvironmentValue "MS_STORE_CLIENT_SECRET"
$environmentSellerId = Get-StoreEnvironmentValue "MS_STORE_SELLER_ID"
$environmentClientId = Get-StoreEnvironmentValue "MS_STORE_CLIENT_ID"
if ($environmentSellerId) { $StoreSellerId = $environmentSellerId }
if ($environmentClientId) { $StoreClientId = $environmentClientId }

if (-not $tenantId) { Die "Missing Store credential: MS_STORE_TENANT_ID." }
if (-not $clientSecret) { Die "Missing Store credential: MS_STORE_CLIENT_SECRET." }

$obsoletePublisherClientId = "6890ceaa-711f-4ca1-a5d6-e8c18d5e68ca"
if ($StoreClientId -eq $obsoletePublisherClientId) {
  Die "MS_STORE_CLIENT_ID targets Mesh Store Publisher (Developer), which cannot access submissions. Set it to the Mesh Store Manager client ID f119e9e3-b77a-4ba6-9fb8-ca6858a66883, or remove the override to use the script default. The secret must belong to that Manager application."
}

Note "Store seller: $StoreSellerId"
Note "Store product: Mesh Relay ($StoreProductId)"
Note "Store application client: $StoreClientId"

Say "Configure Microsoft Store CLI"
Invoke-Native "msstore" @(
  "reconfigure",
  "--tenantId", $tenantId,
  "--sellerId", $StoreSellerId,
  "--clientId", $StoreClientId,
  "--clientSecret", $clientSecret
) "Microsoft Store authentication"
Ok "Store CLI configured"

Say "Retrieve current Store package"
$packageJson = Invoke-NativeCapture "msstore" @(
  "submission", "get", $StoreProductId
) "Retrieve Store package"

try {
  $package = $packageJson | ConvertFrom-Json
} catch {
  Die "Store CLI returned invalid package JSON. $($_.Exception.Message)"
}

if (-not $package.PSObject.Properties["Packages"]) {
  Die "Store package JSON does not contain a Packages collection."
}

$packages = @($package.Packages)
if ($packages.Count -eq 0) {
  Die "Store package JSON contains no installers."
}

$x64Packages = @($packages | Where-Object {
  $_.PSObject.Properties["Architectures"] -and
  @($_.Architectures | ForEach-Object { "$_".ToLowerInvariant() }) -contains "x64"
})

if ($packages.Count -eq 1) {
  $targetPackage = $packages[0]
} elseif ($x64Packages.Count -eq 1) {
  $targetPackage = $x64Packages[0]
} else {
  Die "Could not select one x64 Store package from $($packages.Count) installers."
}

Set-JsonProperty $targetPackage "PackageUrl" $InstallerUrl
Set-JsonProperty $targetPackage "InstallerParameters" "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"
Set-JsonProperty $targetPackage "IsSilentInstall" $true

$updatedPackageJson = $package | ConvertTo-Json -Depth 20 -Compress

Say "Update Store package"
Invoke-Native "msstore" @(
  "submission", "update", $StoreProductId, $updatedPackageJson
) "Update Store package"
Ok "Store draft updated"

if ($DraftOnly) {
  Warn "DraftOnly: package was updated but not submitted."
  exit 0
}

Say "Publish Store submission"
Invoke-Native "msstore" @(
  "submission", "publish", $StoreProductId
) "Publish Store submission"

Say "Poll Store submission"
Invoke-Native "msstore" @(
  "submission", "poll", $StoreProductId
) "Poll Store submission"

Ok "Microsoft Store submission completed"
