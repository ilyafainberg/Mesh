<#
  release-win.ps1  -  Windows-only Mesh release (fast path)

  Runs the full release pipeline but SKIPS the Android AAB build, which is the slow
  part (AOT compile of every assembly takes the bulk of the wall-clock time). Use this
  for the common case where only client/relay code changed and you want a quick Windows
  drop: version bump -> em-dash lint -> Windows publish + sign + zipped installer ->
  git commit+push -> Azure Blob upload -> GitHub release.

  USAGE
    ./_deploy/release-win.ps1 -Version 1.5.8
    ./_deploy/release-win.ps1 -Version 1.5.8 -DryRun
    ./_deploy/release-win.ps1 -Version 1.5.8 -PushStores      # also submit to Microsoft Store

  The shared release script builds and signs the installer. This Windows entry point
  then creates and validates the ZIP, uploads ZIP-only public artifacts, and creates
  the GitHub release. Run release-android.ps1 separately for Android.

  This script contains no em-dash (U+2014) characters, per project rule.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$Version,
  [int]$AndroidVersionCode = 0,
  [string]$NotesFile = "",
  [switch]$SkipBlob,
  [switch]$SkipGitHub,
  [switch]$SkipPush,
  [switch]$PushStores,
  [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $PSScriptRoot "artifacts"
$ReleaseRepo = "MeshRelayAI/Mesh"
$BlobAccount = "meshrelaydl"
$BlobRg = "rg-mesh"
$BlobCtr = "releases"
$BlobBase = "https://$BlobAccount.blob.core.windows.net/$BlobCtr"

$release = Join-Path $PSScriptRoot "release.ps1"
if (-not (Test-Path $release)) { Write-Host "  [fail] release.ps1 not found next to this wrapper" -ForegroundColor Red; exit 1 }
foreach ($tool in @("az", "gh")) {
  if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
    Write-Host "  [fail] required tool '$tool' is not on PATH." -ForegroundColor Red
    exit 1
  }
}
if (-not $SkipGitHub) {
  & gh auth status *> $null
  if ($LASTEXITCODE -ne 0) { Write-Host "  [fail] gh is not authenticated." -ForegroundColor Red; exit 1 }
}

# Build and sign in the shared pipeline, but keep public Windows distribution here.
$forward = @{ Version = $Version; SkipAndroid = $true; SkipBlob = $true; SkipGitHub = $true }
if ($AndroidVersionCode -gt 0) { $forward.AndroidVersionCode = $AndroidVersionCode }
foreach ($p in 'SkipPush','PushStores','DryRun') {
  if ($PSBoundParameters.ContainsKey($p)) { $forward[$p] = $PSBoundParameters[$p] }
}

& $release @forward
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Join-Path $Artifacts "Mesh-Setup-v$Version.exe"
$zip = Join-Path $Artifacts "Mesh-Setup-v$Version.zip"
if (-not (Test-Path $exe -PathType Leaf)) {
  Write-Host "  [fail] signed installer not found at $exe" -ForegroundColor Red
  exit 1
}

$signature = Get-AuthenticodeSignature $exe
if ($signature.Status -ne "Valid") {
  Write-Host "  [fail] installer signature is not valid (status: $($signature.Status))." -ForegroundColor Red
  exit 1
}

Write-Host "`n=== Windows: package signed installer ===" -ForegroundColor Cyan
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -LiteralPath $exe -DestinationPath $zip -CompressionLevel Optimal

Add-Type -AssemblyName System.IO.Compression
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
  $expected = "Mesh-Setup-v$Version.exe"
  $entries = @($archive.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) })
  if ($entries.Count -ne 1 -or $entries[0].FullName -ne $expected) {
    Write-Host "  [fail] archive must contain exactly $expected." -ForegroundColor Red
    exit 1
  }
} finally {
  $archive.Dispose()
}
Write-Host "  [ok] ZIP validated: $zip" -ForegroundColor Green

if ($DryRun) {
  Write-Host "  [warn] DryRun: skipping Blob and GitHub publishing." -ForegroundColor Yellow
  exit 0
}

if (-not $SkipBlob) {
  Write-Host "`n=== Azure Blob: upload Windows ZIP ===" -ForegroundColor Cyan
  $key = (& az storage account keys list --account-name $BlobAccount --resource-group $BlobRg --query "[0].value" -o tsv)
  if (-not $key) { Write-Host "  [fail] could not fetch Blob account key." -ForegroundColor Red; exit 1 }
  foreach ($name in @("Mesh-Setup-v$Version.zip", "Mesh-Setup-latest.zip")) {
    & az storage blob upload --account-name $BlobAccount --container-name $BlobCtr `
      --name $name --file $zip --account-key $key --overwrite --only-show-errors
    if ($LASTEXITCODE -ne 0) { Write-Host "  [fail] Blob upload failed for $name." -ForegroundColor Red; exit 1 }
  }
  foreach ($legacyName in @("Mesh-Setup-v$Version.exe", "Mesh-Setup-latest.exe")) {
    & az storage blob delete --account-name $BlobAccount --container-name $BlobCtr `
      --name $legacyName --account-key $key --only-show-errors *> $null
  }
  $url = "$BlobBase/Mesh-Setup-v$Version.zip"
  $response = Invoke-WebRequest -Uri $url -Method Head -UseBasicParsing
  if ($response.StatusCode -ne 200) { Write-Host "  [fail] ZIP URL returned $($response.StatusCode)." -ForegroundColor Red; exit 1 }
  Write-Host "  [ok] live: $url" -ForegroundColor Green
}

if (-not $SkipGitHub) {
  Write-Host "`n=== GitHub: create ZIP-only release v$Version ===" -ForegroundColor Cyan
  if ($NotesFile -and (Test-Path $NotesFile)) {
    $notes = (Resolve-Path $NotesFile).Path
  } else {
    $notes = Join-Path $env:TEMP "mesh-notes-$Version.md"
    Push-Location $RepoRoot
    try {
      $previous = (& git -c core.longpaths=true tag --list "v*" --sort=-v:refname |
        Where-Object { $_ -ne "v$Version" } | Select-Object -First 1)
      $range = if ($previous) { "$previous..HEAD" } else { "HEAD" }
      $changes = & git -c core.longpaths=true log --pretty=format:"- %s" -n 30 $range
    } finally {
      Pop-Location
    }
    "## Mesh v$Version`n`n### Changes`n$($changes -join "`n")`n`n### Install`nDownload Mesh-Setup-v$Version.zip, extract it, then run Mesh-Setup-v$Version.exe." |
      Set-Content $notes -NoNewline
  }
  & gh release view "v$Version" --repo $ReleaseRepo *> $null
  if ($LASTEXITCODE -eq 0) {
    & gh release edit "v$Version" --repo $ReleaseRepo --title "Mesh v$Version" --notes-file $notes
    if ($LASTEXITCODE -ne 0) { Write-Host "  [fail] GitHub release update failed." -ForegroundColor Red; exit 1 }
    & gh release upload "v$Version" $zip --repo $ReleaseRepo --clobber
    if ($LASTEXITCODE -ne 0) { Write-Host "  [fail] GitHub ZIP upload failed." -ForegroundColor Red; exit 1 }
  } else {
    & gh release create "v$Version" --repo $ReleaseRepo --title "Mesh v$Version" --notes-file $notes $zip
    if ($LASTEXITCODE -ne 0) { Write-Host "  [fail] GitHub release creation failed." -ForegroundColor Red; exit 1 }
  }
  $legacyAssets = (& gh release view "v$Version" --repo $ReleaseRepo --json assets --jq '.assets[].name') |
    Where-Object { $_ -match '^Mesh-Setup-v.+\.exe$' }
  foreach ($asset in $legacyAssets) {
    & gh release delete-asset "v$Version" $asset --repo $ReleaseRepo --yes
    if ($LASTEXITCODE -ne 0) { Write-Host "  [fail] could not remove legacy asset $asset." -ForegroundColor Red; exit 1 }
  }
  Write-Host "  [ok] released: https://github.com/$ReleaseRepo/releases/tag/v$Version" -ForegroundColor Green
}

Write-Host "`n=== Windows release complete ===" -ForegroundColor Cyan
Write-Host "  [ok] archive: $zip" -ForegroundColor Green
exit 0
