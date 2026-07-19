<#
  release.ps1  -  one-command Mesh release pipeline (Phase 1)

  Runs the full chain that used to be done by hand:
    version bump -> em-dash lint -> Windows publish -> stage -> Inno installer ->
    Azure Trusted Signing -> signed Android AAB -> git commit+push ->
    Azure Blob upload -> GitHub release -> (optional) Microsoft Store + Google Play push.

  USAGE
    ./_deploy/release.ps1 -Version 1.4.1
    ./_deploy/release.ps1 -Version 1.4.1 -NotesFile notes.md
    ./_deploy/release.ps1 -Version 1.4.1 -SkipAndroid -SkipStores
    ./_deploy/release.ps1 -Version 1.4.1 -DryRun          # build everything, publish nothing
    ./_deploy/release.ps1 -Version 1.4.1 -PushStores      # also submit to MS Store + Play (needs creds)

  AUTH / PREREQUISITES
    - az login  (used for Azure Trusted Signing and the Blob key lookup).
    - JAVA_HOME set, or the default JDK path below exists (for the Android AAB).
    - gh auth login  (GitHub CLI) with access to the release repo.
    - Signing toolchain under _deploy/signing (auto-restored from the bundled nuget zips).
    - Android keystore under _deploy/android-signing (password read from env
      MESH_KEYSTORE_PASS, else from CREDENTIALS.txt).
    - Store push (-PushStores) additionally needs the Store credentials documented in
      publish-store.ps1. Missing credentials fail the requested Store submission.

  This script contains no em-dash (U+2014) characters, per project rule.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$Version,
  [int]$AndroidVersionCode = 0,
  [string]$NotesFile = "",
  [switch]$SkipWindows,
  [switch]$SkipAndroid,
  [switch]$SkipBlob,
  [switch]$SkipGitHub,
  [switch]$SkipPush,
  [switch]$PushStores,
  [switch]$DryRun
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# ------------------------------------------------------------------ config ----
$RepoRoot   = Split-Path -Parent $PSScriptRoot            # _deploy/.. = repo root
$Deploy     = $PSScriptRoot
$Csproj     = Join-Path $RepoRoot "src\Mesh.App\Mesh.App.csproj"
$SrcDir     = Join-Path $RepoRoot "src"
$Iss        = Join-Path $Deploy "mesh-client.iss"
$PubDir     = Join-Path $Deploy "client-release\Mesh-win-x64"
$Artifacts  = Join-Path $Deploy "artifacts"
$BrandIcon  = Join-Path $Deploy "brand\meshicon.ico"
$LicenseSrc = Join-Path $Deploy "LICENSE-polyform.txt"
$NoticesSrc = Join-Path $Deploy "THIRD-PARTY-NOTICES.txt"

$WinTfm     = "net10.0-windows10.0.19041.0"
$AndTfm     = "net10.0-android"
$ISCC       = "C:\Users\ifain\AppData\Local\Programs\Inno Setup 6\ISCC.exe"
$DefaultJdk = "C:\Program Files\Android\openjdk\jdk-21.0.8"

# Signing (Azure Trusted Signing via signtool + dlib; auth from az login).
$SignSdkZip = Join-Path $Deploy "signing\sdk.zip"
$SignTscZip = Join-Path $Deploy "signing\tsc.zip"
$SignTool   = Join-Path $Deploy "signing\sdk\bin\10.0.28000.0\x64\signtool.exe"
$SignDlib   = Join-Path $Deploy "signing\tsc\bin\x64\Azure.CodeSigning.Dlib.dll"
$SignMeta   = Join-Path $Deploy "signing\metadata.json"
$TimeStamp  = "http://timestamp.acs.microsoft.com"

# Android keystore.
$Keystore   = Join-Path $Deploy "android-signing\mesh-upload.keystore"
$KeyAlias   = "mesh-upload"
$CredFile   = Join-Path $Deploy "android-signing\CREDENTIALS.txt"

# Publish targets.
$ReleaseRepo = "MeshRelayAI/Mesh"                          # GitHub release lives here
$BlobAccount = "meshrelaydl"
$BlobRg      = "rg-mesh"
$BlobCtr     = "releases"
$BlobBase    = "https://$BlobAccount.blob.core.windows.net/$BlobCtr"

# Store identifiers (used only with -PushStores).
$PlayPackage = "net.meshrelay.mesh"

# --------------------------------------------------------------- utilities ----
function Say($m)  { Write-Host "`n=== $m ===" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "  [ok] $m"   -ForegroundColor Green }
function Note($m) { Write-Host "  $m"        -ForegroundColor Gray }
function Warn($m) { Write-Host "  [warn] $m" -ForegroundColor Yellow }
function Die($m)  { Write-Host "  [fail] $m" -ForegroundColor Red; exit 1 }

function Invoke-Native([string]$exe, [string[]]$argv, [string]$what) {
  & $exe @argv
  if ($LASTEXITCODE -ne 0) { Die "$what failed (exit $LASTEXITCODE)." }
}

function Get-KeystorePassword {
  if ($env:MESH_KEYSTORE_PASS) { return $env:MESH_KEYSTORE_PASS }
  if (Test-Path $CredFile) {
    $line = (Select-String -Path $CredFile -Pattern '^Store password:\s*(.+)$').Matches
    if ($line.Count -gt 0) { return $line[0].Groups[1].Value.Trim() }
  }
  Die "Keystore password not found. Set env MESH_KEYSTORE_PASS or add it to CREDENTIALS.txt."
}

function Resolve-Jdk {
  if ($env:JAVA_HOME -and (Test-Path $env:JAVA_HOME)) { return $env:JAVA_HOME }
  if (Test-Path $DefaultJdk) { return $DefaultJdk }
  Die "JDK not found. Set JAVA_HOME (needed for the Android build)."
}

# --------------------------------------------------------------- preflight ----
function Test-Preflight {
  Say "Preflight"
  if ($Version -notmatch '^\d+\.\d+\.\d+$') { Die "Version '$Version' must look like 1.4.1." }

  foreach ($t in "dotnet","git","gh","az") {
    if (-not (Get-Command $t -ErrorAction SilentlyContinue)) { Die "Required tool '$t' not on PATH." }
  }
  if (-not (Test-Path $Csproj)) { Die "csproj not found at $Csproj" }
  if (-not $SkipWindows -and -not (Test-Path $ISCC)) { Die "Inno Setup ISCC.exe not found at $ISCC" }

  # az login is needed for signing (Windows) and the Blob key lookup.
  if (-not $SkipWindows -or (-not $SkipBlob)) {
    $who = (& az account show --query "user.name" -o tsv 2>$null)
    if (-not $who) { Die "az not logged in. Run 'az login' (needed for signing + Blob)." }
    Ok "az login: $who"
  }

  # gh auth for the release.
  if (-not $SkipGitHub) {
    & gh auth status *> $null
    if ($LASTEXITCODE -ne 0) { Die "gh not authenticated. Run 'gh auth login'." }
    Ok "gh authenticated"
  }
  Ok "tools present"
}

function Restore-SigningToolchain {
  if ((Test-Path $SignTool) -and (Test-Path $SignDlib)) { Ok "signing toolchain present"; return }
  Say "Restoring signing toolchain from bundled nuget zips"
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  function Extract-Bin($zip, $destParent, $mustHave) {
    if (Test-Path $mustHave) { return }
    if (-not (Test-Path $zip)) { Die "Signing zip missing: $zip (cannot restore signtool/dlib)." }
    $tmp = Join-Path $env:TEMP ("mx_" + [guid]::NewGuid().ToString("N"))
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $tmp)
    Copy-Item (Join-Path $tmp "bin") (Join-Path $destParent "bin") -Recurse -Force
    Remove-Item $tmp -Recurse -Force
  }
  Extract-Bin $SignSdkZip (Join-Path $Deploy "signing\sdk") $SignTool
  Extract-Bin $SignTscZip (Join-Path $Deploy "signing\tsc") $SignDlib
  if (-not (Test-Path $SignTool)) { Die "signtool.exe still missing after restore." }
  if (-not (Test-Path $SignDlib)) { Die "signing dlib still missing after restore." }
  Ok "signing toolchain restored"
}

# ----------------------------------------------------------- version + lint ---
function Set-ProjectVersion {
  Say "Version -> $Version (android versionCode $script:AndroidVersionCode)"
  $text = Get-Content $Csproj -Raw
  $text = [regex]::Replace($text, '<ApplicationDisplayVersion>[^<]*</ApplicationDisplayVersion>', "<ApplicationDisplayVersion>$Version</ApplicationDisplayVersion>")
  $text = [regex]::Replace($text, '<Version>[^<]*</Version>', "<Version>$Version</Version>")
  $text = [regex]::Replace($text, '<ApplicationVersion>[^<]*</ApplicationVersion>', "<ApplicationVersion>$script:AndroidVersionCode</ApplicationVersion>")
  Set-Content $Csproj $text -NoNewline
  Ok "csproj patched"
}

function Resolve-AndroidVersionCode {
  if ($AndroidVersionCode -gt 0) { $script:AndroidVersionCode = $AndroidVersionCode; return }
  $cur = [int]([regex]::Match((Get-Content $Csproj -Raw), '<ApplicationVersion>(\d+)</ApplicationVersion>').Groups[1].Value)
  $script:AndroidVersionCode = $cur + 1
}

function Invoke-EmDashLint {
  Say "Em-dash lint (U+2014 forbidden)"
  $em = [char]0x2014
  # Only lint code we author: skip vendored libraries and build output.
  $skip = '\\wwwroot\\lib\\|\\bin\\|\\obj\\|\\node_modules\\|\.min\.(js|css)$'
  $hits = Get-ChildItem $SrcDir -Recurse -File -Include *.cs,*.razor,*.js,*.css,*.html,*.xaml -ErrorAction SilentlyContinue |
          Where-Object { $_.FullName -notmatch $skip } |
          Select-String -Pattern $em -SimpleMatch -ErrorAction SilentlyContinue
  if ($hits) {
    $hits | ForEach-Object { Warn "$($_.Path):$($_.LineNumber)" }
    Die "Em-dash found. Replace with a hyphen/colon before releasing."
  }
  Ok "no em-dash in src"
}

# ------------------------------------------------------------- windows build --
function Build-Windows {
  Say "Windows: publish self-contained"
  if (Test-Path $PubDir) { Remove-Item $PubDir -Recurse -Force }
  Invoke-Native "dotnet" @(
    "publish", $Csproj, "-f", $WinTfm, "-c", "Release",
    "-p:WindowsPackageType=None", "-p:RuntimeIdentifierOverride=win10-x64",
    "--self-contained", "true", "-o", $PubDir, "--nologo"
  ) "dotnet publish (windows)"
  if (-not (Test-Path (Join-Path $PubDir "Mesh.App.exe"))) { Die "publish produced no Mesh.App.exe" }
  Ok "published"

  Note "staging license/notices/icon"
  Copy-Item $LicenseSrc (Join-Path $PubDir "LICENSE.txt") -Force
  if (Test-Path $NoticesSrc) { Copy-Item $NoticesSrc (Join-Path $PubDir "THIRD-PARTY-NOTICES.txt") -Force }
  Copy-Item $BrandIcon (Join-Path $PubDir "meshicon.ico") -Force

  Say "Windows: build installer (Inno Setup)"
  if (-not (Test-Path $Artifacts)) { New-Item -ItemType Directory -Path $Artifacts | Out-Null }
  Invoke-Native $ISCC @(
    "/DAppVersion=$Version",
    "/DSourceDir=$PubDir",
    "/DOutputDir=$Artifacts",
    $Iss
  ) "Inno Setup compile"
  $exe = Join-Path $Artifacts "Mesh-Setup-v$Version.exe"
  if (-not (Test-Path $exe)) { Die "installer not produced at $exe" }
  Ok "installer built"

  Say "Windows: sign installer"
  Invoke-Native $SignTool @(
    "sign", "/q", "/fd", "SHA256", "/tr", $TimeStamp, "/td", "SHA256",
    "/dlib", $SignDlib, "/dmdf", $SignMeta, $exe
  ) "signtool sign"
  $sig = Get-AuthenticodeSignature $exe
  if ($sig.Status -ne "Valid") { Die "signature not valid (status: $($sig.Status))." }
  Ok "installer signature valid"

  # Use script scope (not a return value): external tool stdout from Invoke-Native would otherwise
  # pollute the function's output stream and turn the returned object into an array.
  $script:WinExe = $exe
}

# ------------------------------------------------------------- android build --
function Build-Android {
  Say "Android: build signed AAB (versionCode $script:AndroidVersionCode)"
  $env:JAVA_HOME = Resolve-Jdk
  $pw = Get-KeystorePassword
  Invoke-Native "dotnet" @(
    "publish", $Csproj, "-f", $AndTfm, "-c", "Release",
    "-p:AndroidPackageFormat=aab", "-p:AndroidKeyStore=true",
    "-p:AndroidSigningKeyStore=$Keystore", "-p:AndroidSigningKeyAlias=$KeyAlias",
    "-p:AndroidSigningStorePass=$pw", "-p:AndroidSigningKeyPass=$pw", "--nologo"
  ) "dotnet publish (android aab)"
  $aab = Get-ChildItem (Join-Path $RepoRoot "src\Mesh.App\bin\Release\$AndTfm") -Recurse -Filter "*-Signed.aab" -ErrorAction SilentlyContinue |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if (-not $aab) { Die "signed AAB not found after publish." }
  & "$($env:JAVA_HOME)\bin\jarsigner.exe" -verify $aab.FullName *> $null
  if ($LASTEXITCODE -ne 0) { Die "AAB signature verification failed." }
  Ok "AAB signed + verified: $([math]::Round($aab.Length/1MB,1)) MB"
  $script:AndroidAab = $aab.FullName
}

# ------------------------------------------------------------------ publish ---
function Invoke-GitCommitPush {
  Say "Git: commit version bump + push"
  Push-Location $RepoRoot
  try {
    & git -c core.longpaths=true add "src/Mesh.App/Mesh.App.csproj"
    & git -c core.longpaths=true -c user.name="Mesh Dev" -c user.email="mesh@localhost" commit -m "Release v$Version" | Out-Null
    if ($DryRun) { Warn "DryRun: skipping git push"; return }
    Invoke-Native "git" @("-c","core.longpaths=true","push","origin","HEAD") "git push"
    Ok "pushed"
  } finally { Pop-Location }
}

function Publish-Blob([string]$exe) {
  Say "Azure Blob: upload signed installer"
  if ($DryRun) { Warn "DryRun: skipping blob upload"; return }
  $key = (& az storage account keys list --account-name $BlobAccount --resource-group $BlobRg --query "[0].value" -o tsv)
  if (-not $key) { Die "could not fetch Blob account key (check az permissions)." }
  Invoke-Native "az" @(
    "storage","blob","upload","--account-name",$BlobAccount,"--container-name",$BlobCtr,
    "--name","Mesh-Setup-v$Version.exe","--file",$exe,"--account-key",$key,"--overwrite","--only-show-errors"
  ) "blob upload"
  $url = "$BlobBase/Mesh-Setup-v$Version.exe"
  try {
    $r = Invoke-WebRequest -Uri $url -Method Head -UseBasicParsing
    if ($r.StatusCode -eq 200) { Ok "live: $url" } else { Warn "unexpected status $($r.StatusCode) for $url" }
  } catch { Warn "could not HEAD $url : $($_.Exception.Message)" }
}

function Get-ReleaseNotes {
  if ($NotesFile -and (Test-Path $NotesFile)) { return (Resolve-Path $NotesFile).Path }
  $tmp = Join-Path $env:TEMP "mesh-notes-$Version.md"
  Push-Location $RepoRoot
  try {
    $prev = (& git -c core.longpaths=true tag --list "v*" --sort=-v:refname | Select-Object -First 1)
    $range = if ($prev) { "$prev..HEAD" } else { "HEAD" }
    $log = & git -c core.longpaths=true log --pretty=format:"- %s" -n 30 $range
  } finally { Pop-Location }
  "## Mesh v$Version`n`n### Changes`n$($log -join "`n")`n`n### Install`nDownload and run Mesh-Setup-v$Version.exe." |
    Set-Content $tmp -NoNewline
  return $tmp
}

function Publish-GitHubRelease([string]$exe) {
  Say "GitHub: create release v$Version on $ReleaseRepo"
  $notes = Get-ReleaseNotes
  if ($DryRun) { Warn "DryRun: skipping gh release create (notes at $notes)"; return }
  Invoke-Native "gh" @(
    "release","create","v$Version","--repo",$ReleaseRepo,
    "--title","Mesh v$Version","--notes-file",$notes,$exe
  ) "gh release create"
  Ok "released: https://github.com/$ReleaseRepo/releases/tag/v$Version"
}

# -------------------------------------------------------------- store pushes --
# Phase 2 wiring. Both are gated on -PushStores AND the creds below. If creds are
# absent the step is skipped with a clear message; the installer/AAB are still
# built + uploaded so you can finish in the portal.
function Push-GooglePlay([string]$aab) {
  Say "Google Play: upload to internal testing"
  $saJson = $env:GOOGLE_PLAY_SA_JSON
  if (-not $saJson) { $saJson = [Environment]::GetEnvironmentVariable("GOOGLE_PLAY_SA_JSON", "User") }
  if (-not $saJson -or -not (Test-Path $saJson)) {
    Warn "skipped: set GOOGLE_PLAY_SA_JSON to a service-account key file to enable."
    return
  }
  if ($PSVersionTable.PSVersion.Major -lt 7) { Warn "skipped: Play push needs PowerShell 7+ (pwsh)."; return }
  if ($DryRun) { Warn "DryRun: skipping Play upload"; return }

  $sa = Get-Content $saJson -Raw | ConvertFrom-Json
  # Build + sign a JWT for the service account, exchange for an OAuth token.
  $now = [int][double]::Parse((Get-Date -UFormat %s))
  $header  = @{ alg="RS256"; typ="JWT" } | ConvertTo-Json -Compress
  $claim   = @{ iss=$sa.client_email; scope="https://www.googleapis.com/auth/androidpublisher"; aud=$sa.token_uri; iat=$now; exp=($now+3600) } | ConvertTo-Json -Compress
  function B64Url([byte[]]$b) { [Convert]::ToBase64String($b).TrimEnd('=').Replace('+','-').Replace('/','_') }
  $signingInput = (B64Url([Text.Encoding]::UTF8.GetBytes($header))) + "." + (B64Url([Text.Encoding]::UTF8.GetBytes($claim)))
  $rsa = [System.Security.Cryptography.RSA]::Create(); $rsa.ImportFromPem($sa.private_key)
  $sig = $rsa.SignData([Text.Encoding]::UTF8.GetBytes($signingInput), [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
  $jwt = "$signingInput." + (B64Url($sig))
  $token = (Invoke-RestMethod -Method Post -Uri $sa.token_uri -Body @{ grant_type="urn:ietf:params:oauth:grant-type:jwt-bearer"; assertion=$jwt }).access_token
  $H = @{ Authorization = "Bearer $token" }
  $api = "https://androidpublisher.googleapis.com/androidpublisher/v3/applications/$PlayPackage"

  $edit = Invoke-RestMethod -Method Post -Uri "$api/edits" -Headers $H
  $up   = Invoke-RestMethod -Method Post -Uri "https://androidpublisher.googleapis.com/upload/androidpublisher/v3/applications/$PlayPackage/edits/$($edit.id)/bundles?uploadType=media" -Headers $H -InFile $aab -ContentType "application/octet-stream"
  $body = @{ track="internal"; releases=@(@{ status="completed"; versionCodes=@("$($up.versionCode)") }) } | ConvertTo-Json -Depth 6
  Invoke-RestMethod -Method Put -Uri "$api/edits/$($edit.id)/tracks/internal" -Headers $H -Body $body -ContentType "application/json" | Out-Null
  Invoke-RestMethod -Method Post -Uri "$api/edits/$($edit.id):commit" -Headers $H | Out-Null
  Ok "Play internal testing updated to versionCode $($up.versionCode)"
}

function Push-MicrosoftStore([string]$exe) {
  Say "Microsoft Store: update package"
  $publisher = Join-Path $Deploy "publish-store.ps1"
  if (-not (Test-Path $publisher)) { Die "Store publisher not found at $publisher." }
  $key = (& az storage account keys list --account-name $BlobAccount --resource-group $BlobRg --query "[0].value" -o tsv)
  if (-not $key) { Die "could not fetch Blob account key for Store installer." }
  $storeBlobName = "store/Mesh-Setup-v$Version.exe"
  Invoke-Native "az" @(
    "storage","blob","upload","--account-name",$BlobAccount,"--container-name",$BlobCtr,
    "--name",$storeBlobName,"--file",$exe,"--account-key",$key,"--overwrite","--only-show-errors"
  ) "Store installer upload"
  $blobUrl = "$BlobBase/$storeBlobName"
  $args = @{
    Version = $Version
    InstallerUrl = $blobUrl
    SkipBuild = $true
  }
  if ($DryRun) { $args.DryRun = $true }
  & $publisher @args
  if ($LASTEXITCODE -ne 0) { Die "Microsoft Store publish failed (exit $LASTEXITCODE)." }
}

# --------------------------------------------------------------------- main ---
$sw = [System.Diagnostics.Stopwatch]::StartNew()
Say "Mesh release v$Version  (repo: $RepoRoot)"
if ($DryRun) { Warn "DRY RUN: builds + signs locally, publishes nothing irreversible." }

Test-Preflight
Resolve-AndroidVersionCode
if (-not $SkipWindows) { Restore-SigningToolchain }
Set-ProjectVersion
Invoke-EmDashLint

$win = $null; $aab = $null
$script:WinExe = $null; $script:AndroidAab = $null
if (-not $SkipWindows) { Build-Windows } else { Warn "skipping Windows build" }
if (-not $SkipAndroid) { Build-Android } else { Warn "skipping Android build" }

if (-not $SkipPush)   { Invoke-GitCommitPush }
if ($script:WinExe -and -not $SkipBlob)   { Publish-Blob   $script:WinExe }
if ($script:WinExe -and -not $SkipGitHub) {
  $sig = Get-AuthenticodeSignature $script:WinExe
  if ($sig.Status -ne "Valid") {
    Die "refusing GitHub upload: installer signature is not valid."
  }
  Publish-GitHubRelease $script:WinExe
}

if ($PushStores) {
  if ($script:WinExe)     { Push-MicrosoftStore $script:WinExe }
  if ($script:AndroidAab) { Push-GooglePlay     $script:AndroidAab }
} else {
  Note "stores: use -PushStores to submit to Microsoft Store + Google Play (Phase 2 creds required)."
}

$sw.Stop()
Say "Done in $([math]::Round($sw.Elapsed.TotalMinutes,1)) min"
if ($script:WinExe) { Ok "installer: $script:WinExe" }
if ($script:AndroidAab) { Ok "aab: $script:AndroidAab" }
