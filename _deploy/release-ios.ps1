<#
  release-ios.ps1  -  signed iOS IPA release pipeline for Mesh

  Builds on macOS (preferred) or on Windows through a paired Mac build host:
    preflight -> version/build bump -> em-dash lint -> signed IPA publish ->
    package/privacy/signature verification -> manifest -> git commit+push ->
    optional App Store Connect upload (the uploaded build appears in TestFlight).

  OFFICIAL .NET MAUI CLI REFERENCE
    https://learn.microsoft.com/dotnet/maui/ios/deployment/publish-cli?view=net-maui-10.0

  LOCAL MAC USAGE (PowerShell 7 / pwsh)
    ./_deploy/release-ios.ps1 -Version 1.6.1 `
      -CodesignKey "Apple Distribution: Your Organization (TEAMID)" `
      -CodesignProvision "Mesh App Store"

  WINDOWS + PAIRED MAC USAGE
    ./_deploy/release-ios.ps1 -Version 1.6.1 `
      -CodesignKey "Apple Distribution: Your Organization (TEAMID)" `
      -CodesignProvision "Mesh App Store" `
      -MacAddress 192.168.1.20 -MacUser ilya

    Pair Visual Studio to the Mac once first so SSH keys and the XMA build host are
    configured. This script requires the saved SSH keys and never puts a Mac
    password on a process command line.

  OPTIONAL TESTFLIGHT UPLOAD (must run locally on macOS)
    ./_deploy/release-ios.ps1 -Version 1.6.1 `
      -CodesignKey "Apple Distribution: Your Organization (TEAMID)" `
      -CodesignProvision "Mesh App Store" `
      -PushTestFlight `
      -AppStoreAppId 1234567890 `
      -AppStoreKeyId ABC123DEFG `
      -AppStoreIssuerId 00000000-0000-0000-0000-000000000000 `
      -AppStoreKeyPath ~/.appstoreconnect/private_keys/AuthKey_ABC123DEFG.p8

  ENVIRONMENT ALIASES
    MESH_IOS_CODESIGN_KEY       Apple distribution identity name
    MESH_IOS_PROVISION         App Store provisioning profile name or UUID
    MESH_MAC_ADDRESS            Paired Mac IP/host (Windows remote mode)
    MESH_MAC_USER               Paired Mac username
    MESH_ASC_APP_ID             Numeric App Store Connect app Apple ID
    MESH_ASC_KEY_ID             App Store Connect API key ID
    MESH_ASC_ISSUER_ID          App Store Connect API issuer ID
    MESH_ASC_KEY_PATH           AuthKey_<key-id>.p8 path

  APPLE PREREQUISITES
    - Active Apple Developer Program organization enrollment.
    - Explicit App ID net.meshrelay.mesh.
    - Apple Distribution certificate + private key in the Mac login keychain.
    - App Store provisioning profile for net.meshrelay.mesh installed on the Mac.
    - App record created in App Store Connect before -PushTestFlight is used.
    - Xcode 16+ selected with xcode-select.
    - The repo already contains Platforms/iOS/Resources/PrivacyInfo.xcprivacy.

  NOTES
    - BuildNumber maps to CFBundleVersion and must increase for every App Store upload.
      If omitted, the current ApplicationVersion in the csproj is incremented.
    - Version maps to CFBundleShortVersionString / ApplicationDisplayVersion.
    - The IPA is NOT attached to a public GitHub release. App Store binaries go to
      App Store Connect/TestFlight, while a local copy is retained in _deploy/artifacts.
    - DryRun builds and verifies the IPA but restores the csproj and skips git/upload.
    - This script contains no em-dash (U+2014) characters, per project rule.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$Version,
  [int]$BuildNumber = 0,

  [string]$CodesignKey = $env:MESH_IOS_CODESIGN_KEY,
  [string]$CodesignProvision = $env:MESH_IOS_PROVISION,
  [string]$CodesignEntitlements = "",
  [switch]$UseInterpreter,

  [string]$MacAddress = $env:MESH_MAC_ADDRESS,
  [string]$MacUser = $env:MESH_MAC_USER,
  [int]$TcpPort = 58181,
  [string]$RemoteDotNetRoot = "",

  [switch]$PushTestFlight,
  [string]$AppStoreAppId = $env:MESH_ASC_APP_ID,
  [string]$AppStoreKeyId = $env:MESH_ASC_KEY_ID,
  [string]$AppStoreIssuerId = $env:MESH_ASC_ISSUER_ID,
  [string]$AppStoreKeyPath = $env:MESH_ASC_KEY_PATH,
  [switch]$SkipPush,
  [switch]$DryRun
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# ------------------------------------------------------------------ config ----
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Deploy = $PSScriptRoot
$Csproj = Join-Path $RepoRoot "src/Mesh.App/Mesh.App.csproj"
$SrcDir = Join-Path $RepoRoot "src"
$Artifacts = Join-Path $Deploy "artifacts"
$IosTfm = "net10.0-ios"
$RuntimeId = "ios-arm64"
$PrivacyManifest = Join-Path $RepoRoot "src/Mesh.App/Platforms/iOS/Resources/PrivacyInfo.xcprivacy"
$script:IsMacHost = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
  [System.Runtime.InteropServices.OSPlatform]::OSX)
$script:IsWindowsHost = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
  [System.Runtime.InteropServices.OSPlatform]::Windows)
$script:OriginalCsprojBytes = $null
$script:OriginalCsprojHash = $null
$script:PatchedCsprojHash = $null
$script:PatchedCsprojBlob = $null
$script:IpaPath = $null
$script:ManifestPath = $null
$script:RunArtifacts = $Artifacts
$script:VersionCommitted = $false
$script:Failed = $false
$script:TestFlightUploaded = $false
$script:ExitCode = 0

# --------------------------------------------------------------- utilities ----
function Say($message) { Write-Host "`n=== $message ===" -ForegroundColor Cyan }
function Ok($message) { Write-Host "  [ok] $message" -ForegroundColor Green }
function Note($message) { Write-Host "  $message" -ForegroundColor Gray }
function Warn($message) { Write-Host "  [warn] $message" -ForegroundColor Yellow }
function Fail($message) { throw $message }

function Invoke-Native([string]$exe, [string[]]$arguments, [string]$what) {
  & $exe @arguments
  if ($LASTEXITCODE -ne 0) { Fail "$what failed (exit $LASTEXITCODE)." }
}

function Invoke-NativeOutput([string]$exe, [string[]]$arguments, [string]$what) {
  $output = (& $exe @arguments 2>&1 | Out-String).Trim()
  if ($LASTEXITCODE -ne 0) { Fail "$what failed (exit $LASTEXITCODE): $output" }
  return $output
}

function Invoke-Altool([string[]]$arguments, [string]$what) {
  $captured = @()
  & xcrun @arguments 2>&1 | Tee-Object -Variable captured
  $exitCode = $LASTEXITCODE
  $text = ($captured | Out-String)
  if ($exitCode -ne 0 -or
      $text -match '(?im)^\s*(UPLOAD|VERIFY) FAILED\b' -or
      $text -match '(?im)"product-errors"\s*:') {
    Fail "$what failed (exit $exitCode). Review the Apple validation output above."
  }
}

function Get-BytesHash([byte[]]$bytes) {
  $sha = [System.Security.Cryptography.SHA256]::Create()
  try {
    return [Convert]::ToHexString($sha.ComputeHash($bytes)).ToLowerInvariant()
  } finally {
    $sha.Dispose()
  }
}

function Get-FileSha([string]$path) {
  return (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-ProjectValue([string]$name) {
  $text = Get-Content $Csproj -Raw
  $match = [regex]::Match($text, "<$name>([^<]+)</$name>")
  if (-not $match.Success) { Fail "$name not found in $Csproj." }
  return $match.Groups[1].Value.Trim()
}

function Test-TcpPort([string]$hostName, [int]$port, [int]$timeoutMs = 5000) {
  $client = [System.Net.Sockets.TcpClient]::new()
  try {
    $task = $client.ConnectAsync($hostName, $port)
    return $task.Wait($timeoutMs) -and $client.Connected
  } catch {
    return $false
  } finally {
    $client.Dispose()
  }
}

function Get-ApiKeyFile {
  if (-not $PushTestFlight) { return $null }
  $expected = "AuthKey_$AppStoreKeyId.p8"

  if ($AppStoreKeyPath) {
    $resolvedItem = Resolve-Path $AppStoreKeyPath -ErrorAction SilentlyContinue
    if (-not $resolvedItem) { Fail "App Store Connect key not found: $AppStoreKeyPath" }
    $resolved = $resolvedItem.Path
    if ([System.IO.Path]::GetFileName($resolved) -ne $expected) {
      Fail "API key file must be named $expected (altool requirement)."
    }
    return $resolved
  }

  $homeDir = [Environment]::GetFolderPath("UserProfile")
  $candidates = @(
    (Join-Path (Get-Location) "private_keys/$expected"),
    (Join-Path $homeDir "private_keys/$expected"),
    (Join-Path $homeDir ".private_keys/$expected"),
    (Join-Path $homeDir ".appstoreconnect/private_keys/$expected")
  )
  foreach ($candidate in $candidates) {
    if (Test-Path $candidate) { return (Resolve-Path $candidate).Path }
  }
  Fail "AuthKey file not found. Pass -AppStoreKeyPath or place $expected under ~/.appstoreconnect/private_keys."
}

# --------------------------------------------------------------- preflight ----
function Test-Preflight {
  Say "Preflight"

  if ($PSVersionTable.PSVersion.Major -lt 7) {
    Fail "PowerShell 7+ is required. Run this script with pwsh."
  }
  if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    Fail "Version '$Version' must look like 1.6.1."
  }
  if (-not $script:IsMacHost -and -not $script:IsWindowsHost) {
    Fail "iOS publish supports macOS locally or Windows through a paired Mac."
  }

  foreach ($tool in "dotnet", "git") {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
      Fail "Required tool '$tool' is not on PATH."
    }
  }
  if (-not (Test-Path $Csproj)) { Fail "csproj not found at $Csproj" }
  if (-not (Test-Path $PrivacyManifest)) {
    Fail "Apple privacy manifest is missing: $PrivacyManifest"
  }
  if ((Get-ProjectValue "ApplicationId") -ne "net.meshrelay.mesh") {
    Fail "ApplicationId must be net.meshrelay.mesh for the registered Apple App ID."
  }
  if (-not ((Get-Content $Csproj -Raw) -match [regex]::Escape($IosTfm))) {
    Fail "$IosTfm is not present in Mesh.App.csproj."
  }
  if (-not $CodesignKey) {
    Fail "Codesign key is required. Pass -CodesignKey or set MESH_IOS_CODESIGN_KEY."
  }
  if (-not $CodesignProvision) {
    Fail "Provisioning profile is required. Pass -CodesignProvision or set MESH_IOS_PROVISION."
  }
  if ($CodesignEntitlements -and -not (Test-Path $CodesignEntitlements)) {
    Fail "Entitlements file not found: $CodesignEntitlements"
  }

  Push-Location $RepoRoot
  try {
    # A release must be reproducible from the pushed commit. Include untracked files in this
    # check because SDK default globs can compile/package them even when git does not track them.
    $dirty = @(& git status --porcelain --untracked-files=all)
    if ($dirty.Count -gt 0) {
      $preview = ($dirty | Select-Object -First 8) -join "`n"
      Fail "The worktree is not clean. Commit/stash/remove every change before release:`n$preview"
    }
    $remote = (& git remote get-url origin 2>$null)
    if (-not $remote) { Fail "git origin remote is not configured." }
  } finally {
    Pop-Location
  }

  $workloads = (& dotnet workload list 2>&1 | Out-String)
  if ($workloads -notmatch '(?im)\bmaui-ios\b|\bios\b') {
    Warn "No iOS workload was detected locally. dotnet publish will report the exact workload needed."
  }

  if ($script:IsMacHost) {
    foreach ($tool in "xcodebuild", "xcrun", "security", "codesign", "plutil") {
      if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        Fail "Required Apple tool '$tool' is not on PATH."
      }
    }
    $xcodeText = (& xcodebuild -version 2>&1 | Out-String)
    if ($xcodeText -notmatch 'Xcode\s+(\d+)') { Fail "Could not determine the selected Xcode version." }
    if ([int]$Matches[1] -lt 16) { Fail "Xcode 16+ is required for App Store uploads." }

    $identities = (& security find-identity -v -p codesigning 2>&1 | Out-String)
    if ($identities -notmatch [regex]::Escape($CodesignKey)) {
      Fail "Signing identity '$CodesignKey' was not found in the Mac keychain."
    }
    Ok "local Mac build host; $($xcodeText.Trim() -replace "`n", " / ")"
  } else {
    if (-not $MacAddress -or -not $MacUser) {
      Fail "Windows builds require -MacAddress and -MacUser (or MESH_MAC_ADDRESS/MESH_MAC_USER)."
    }
    foreach ($tool in "ssh", "scp") {
      if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        Fail "Windows remote verification requires OpenSSH tool '$tool' on PATH."
      }
    }
    if (-not (Test-TcpPort $MacAddress 22)) {
      Fail "Paired Mac '$MacAddress' is not reachable over SSH (port 22)."
    }
    if (-not $RemoteDotNetRoot) {
      $script:RemoteDotNetRoot = "/Users/$MacUser/Library/Caches/Xamarin/XMA/SDKs/dotnet/"
    }
    if ($PushTestFlight) {
      Fail "-PushTestFlight requires running release-ios.ps1 locally on macOS (xcrun/altool)."
    }
    Ok "Windows remote build through paired Mac $MacUser@$MacAddress"
  }

  if ($PushTestFlight) {
    if (-not $AppStoreAppId) { Fail "App Store app Apple ID is required for upload." }
    if (-not $AppStoreKeyId) { Fail "App Store Connect API key ID is required for upload." }
    if (-not $AppStoreIssuerId) { Fail "App Store Connect issuer ID is required for upload." }
    $keyFile = Get-ApiKeyFile
    $env:API_PRIVATE_KEYS_DIR = Split-Path -Parent $keyFile
    Ok "App Store Connect JWT key found (key id $AppStoreKeyId)"
  }

  Ok "bundle id net.meshrelay.mesh; privacy manifest present; signing inputs present"
}

# ----------------------------------------------------------- version + lint ---
function Resolve-BuildNumber {
  if ($BuildNumber -gt 0) {
    $script:BuildNumber = $BuildNumber
    return
  }
  $current = [int](Get-ProjectValue "ApplicationVersion")
  $script:BuildNumber = $current + 1
}

function Set-ProjectVersion {
  Say "Version -> $Version (iOS build $script:BuildNumber)"
  # Keep original bytes, not decoded text, so DryRun/failure restoration is byte-for-byte
  # (including any BOM/encoding choice).
  $script:OriginalCsprojBytes = [System.IO.File]::ReadAllBytes($Csproj)
  $script:OriginalCsprojHash = Get-BytesHash $script:OriginalCsprojBytes
  $text = Get-Content $Csproj -Raw
  $text = [regex]::Replace($text,
    '<ApplicationDisplayVersion>[^<]*</ApplicationDisplayVersion>',
    "<ApplicationDisplayVersion>$Version</ApplicationDisplayVersion>")
  $text = [regex]::Replace($text, '<Version>[^<]*</Version>', "<Version>$Version</Version>")
  $text = [regex]::Replace($text,
    '<ApplicationVersion>[^<]*</ApplicationVersion>',
    "<ApplicationVersion>$script:BuildNumber</ApplicationVersion>")
  [System.IO.File]::WriteAllText($Csproj, $text, [System.Text.UTF8Encoding]::new($false))
  $script:PatchedCsprojHash = Get-FileSha $Csproj
  $script:PatchedCsprojBlob = Invoke-NativeOutput "git" @(
    "-C", $RepoRoot, "hash-object", "--", $Csproj
  ) "git hash-object"
  Ok "csproj patched"
}

function Invoke-EmDashLint {
  Say "Em-dash lint (U+2014 forbidden)"
  $em = [char]0x2014
  $skip = '[\\/](wwwroot[\\/]lib|bin|obj|node_modules)[\\/]|\.min\.(js|css)$'
  $hits = Get-ChildItem $SrcDir -Recurse -File -Include *.cs,*.razor,*.js,*.css,*.html,*.xaml -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch $skip } |
    Select-String -Pattern $em -SimpleMatch -ErrorAction SilentlyContinue
  if ($hits) {
    $hits | ForEach-Object { Warn "$($_.Path):$($_.LineNumber)" }
    Fail "Em-dash found. Replace it before releasing."
  }
  Ok "no em-dash in src"
}

# -------------------------------------------------------------------- build ---
function Build-Ios {
  Say "iOS: publish signed IPA"

  # Microsoft documents this exact output directory. Delete only old IPA files from it
  # before publishing so a failed build can never select and release a stale archive.
  $publishDir = Join-Path $RepoRoot "src/Mesh.App/bin/Release/$IosTfm/$RuntimeId/publish"
  if (Test-Path $publishDir) {
    Get-ChildItem $publishDir -File -Filter *.ipa -ErrorAction SilentlyContinue |
      Remove-Item -Force
  }

  $arguments = @(
    "publish", $Csproj,
    "-f", $IosTfm,
    "-c", "Release",
    "-p:ArchiveOnBuild=true",
    "-p:RuntimeIdentifier=$RuntimeId",
    "-p:ApplicationId=net.meshrelay.mesh",
    "-p:ApplicationDisplayVersion=$Version",
    "-p:ApplicationVersion=$script:BuildNumber",
    "-p:CodesignKey=$CodesignKey",
    "-p:CodesignProvision=$CodesignProvision",
    "--nologo"
  )
  if ($CodesignEntitlements) {
    $arguments += "-p:CodesignEntitlements=$((Resolve-Path $CodesignEntitlements).Path)"
  }
  if ($UseInterpreter) {
    # Hosted Azure jobs have a 60-minute free-tier cap. Interpret framework/dependency
    # assemblies to reduce AOT time, while keeping Mesh.App and Mesh.Shared native.
    $arguments += "-p:MtouchInterpreter=all%2c-Mesh.App%2c-Mesh.Shared"
  }
  if (-not $script:IsMacHost) {
    $arguments += @(
      "-p:ServerAddress=$MacAddress",
      "-p:ServerUser=$MacUser",
      "-p:TcpPort=$TcpPort",
      "-p:_DotNetRootRemoteDirectory=$script:RemoteDotNetRoot"
    )
  }

  Invoke-Native "dotnet" $arguments "dotnet publish (signed iOS IPA)"

  $ipas = @(Get-ChildItem $publishDir -File -Filter *.ipa -ErrorAction SilentlyContinue)
  if ($ipas.Count -ne 1) {
    Fail "Expected exactly one new IPA under $publishDir; found $($ipas.Count)."
  }
  $ipa = $ipas[0]

  New-Item -ItemType Directory -Force -Path $script:RunArtifacts | Out-Null
  $artifactName = "Mesh-iOS-v$Version-build$script:BuildNumber.ipa"
  $artifactPath = Join-Path $script:RunArtifacts $artifactName
  if (Test-Path $artifactPath) {
    Fail "Release artifact already exists (refusing to overwrite): $artifactPath"
  }
  Copy-Item $ipa.FullName $artifactPath
  $script:IpaPath = $artifactPath
  Ok "signed IPA staged: $artifactPath ($([math]::Round($ipa.Length / 1MB, 1)) MB)"
}

function Test-Ipa {
  Say "iOS: verify IPA"
  Add-Type -AssemblyName System.IO.Compression.FileSystem

  $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("mesh-ios-" + [guid]::NewGuid().ToString("N"))
  try {
    [System.IO.Compression.ZipFile]::ExtractToDirectory($script:IpaPath, $temp)
    $apps = @(Get-ChildItem (Join-Path $temp "Payload") -Directory -Filter *.app)
    if ($apps.Count -ne 1) { Fail "Expected exactly one .app bundle in the IPA; found $($apps.Count)." }
    $app = $apps[0]

    $infoPlist = Join-Path $app.FullName "Info.plist"
    $profile = Join-Path $app.FullName "embedded.mobileprovision"
    $privacyFiles = @(Get-ChildItem $app.FullName -Recurse -File -Filter PrivacyInfo.xcprivacy)
    if (-not (Test-Path $infoPlist)) { Fail "IPA app bundle has no Info.plist." }
    if (-not (Test-Path $profile)) { Fail "IPA app bundle has no embedded.mobileprovision." }
    if ($privacyFiles.Count -lt 1) { Fail "IPA app bundle has no PrivacyInfo.xcprivacy." }
    if (Test-Path (Join-Path $app.FullName ".playwright")) {
      Fail "IPA contains the desktop-only .playwright runtime payload."
    }
    $rootDylibs = @(Get-ChildItem $app.FullName -File -Filter *.dylib)
    if ($rootDylibs.Count -gt 0) {
      Fail "IPA contains standalone root dylib(s), which App Store Connect rejects: $($rootDylibs.Name -join ', ')"
    }

    if ($script:IsMacHost) {
      function Read-PlistValue([string]$key) {
        $value = (& plutil -extract $key raw -o - $infoPlist 2>&1 | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) { Fail "Could not read $key from the IPA Info.plist." }
        return $value
      }
      $actualBundle = Read-PlistValue "CFBundleIdentifier"
      $actualVersion = Read-PlistValue "CFBundleShortVersionString"
      $actualBuild = Read-PlistValue "CFBundleVersion"
      if ($actualBundle -ne "net.meshrelay.mesh") {
        Fail "IPA bundle id is '$actualBundle', expected net.meshrelay.mesh."
      }
      if ($actualVersion -ne $Version) {
        Fail "IPA version is '$actualVersion', expected $Version."
      }
      if ($actualBuild -ne "$script:BuildNumber") {
        Fail "IPA build is '$actualBuild', expected $script:BuildNumber."
      }

      $decodedProfile = Join-Path $temp "embedded-profile.plist"
      Invoke-Native "security" @("cms", "-D", "-i", $profile, "-o", $decodedProfile) `
        "provisioning profile decode"
      $profileText = Get-Content $decodedProfile -Raw
      Invoke-Native "codesign" @("--verify", "--deep", "--strict", "--verbose=2", $app.FullName) `
        "codesign verification"
      Ok "Apple code signature verified"

      $signedEntitlements = Invoke-NativeOutput "codesign" @(
        "-d", "--entitlements", ":-", $app.FullName
      ) "signed app entitlement inspection"
      $keychainGroupPattern =
        '(?s)<key>keychain-access-groups</key>\s*<array>(?:(?!</array>).)*<string>[^<]*com\.microsoft\.adalcache</string>(?:(?!</array>).)*</array>'
      if ($signedEntitlements -notmatch $keychainGroupPattern) {
        Fail "Signed IPA is missing com.microsoft.adalcache from keychain-access-groups. Refusing upload."
      }
      Ok "signed MSAL keychain entitlement verified"
    } else {
      # Parse the exact keys with plutil on the paired Mac. This avoids weak byte/string
      # searches against a binary plist while still validating the IPA copied back to Windows.
      $remoteId = [guid]::NewGuid().ToString("N")
      $remoteInfo = "/tmp/mesh-$remoteId-Info.plist"
      $remoteProfile = "/tmp/mesh-$remoteId.mobileprovision"
      $remoteApp = "/tmp/mesh-$remoteId.app"
      $remoteHost = "$MacUser@$MacAddress"
      try {
        Invoke-Native "scp" @("-q", $infoPlist, "${remoteHost}:$remoteInfo") `
          "copy Info.plist to paired Mac"
        Invoke-Native "scp" @("-q", $profile, "${remoteHost}:$remoteProfile") `
          "copy provisioning profile to paired Mac"
        Invoke-Native "scp" @("-q", "-r", $app.FullName, "${remoteHost}:$remoteApp") `
          "copy app bundle to paired Mac"

        $actualBundle = Invoke-NativeOutput "ssh" @(
          $remoteHost, "plutil", "-extract", "CFBundleIdentifier", "raw", "-o", "-", $remoteInfo
        ) "remote Info.plist bundle-id read"
        $actualVersion = Invoke-NativeOutput "ssh" @(
          $remoteHost, "plutil", "-extract", "CFBundleShortVersionString", "raw", "-o", "-", $remoteInfo
        ) "remote Info.plist version read"
        $actualBuild = Invoke-NativeOutput "ssh" @(
          $remoteHost, "plutil", "-extract", "CFBundleVersion", "raw", "-o", "-", $remoteInfo
        ) "remote Info.plist build read"
        if ($actualBundle -ne "net.meshrelay.mesh") {
          Fail "IPA bundle id is '$actualBundle', expected net.meshrelay.mesh."
        }
        if ($actualVersion -ne $Version) {
          Fail "IPA version is '$actualVersion', expected $Version."
        }
        if ($actualBuild -ne "$script:BuildNumber") {
          Fail "IPA build is '$actualBuild', expected $script:BuildNumber."
        }
        $profileText = Invoke-NativeOutput "ssh" @(
          $remoteHost, "security", "cms", "-D", "-i", $remoteProfile
        ) "remote provisioning profile decode"
        Invoke-Native "ssh" @(
          $remoteHost, "codesign", "--verify", "--deep", "--strict", "--verbose=2", $remoteApp
        ) "remote codesign verification"
        $signedEntitlements = Invoke-NativeOutput "ssh" @(
          $remoteHost, "codesign", "-d", "--entitlements", ":-", $remoteApp
        ) "remote signed app entitlement inspection"
        $keychainGroupPattern =
          '(?s)<key>keychain-access-groups</key>\s*<array>(?:(?!</array>).)*<string>[^<]*com\.microsoft\.adalcache</string>(?:(?!</array>).)*</array>'
        if ($signedEntitlements -notmatch $keychainGroupPattern) {
          Fail "Signed IPA is missing com.microsoft.adalcache from keychain-access-groups. Refusing upload."
        }
        Ok "remote Apple code signature and MSAL keychain entitlement verified"
      } finally {
        & ssh $remoteHost "rm" "-rf" $remoteInfo $remoteProfile $remoteApp *> $null
      }
    }

    if ($profileText -notmatch [regex]::Escape("net.meshrelay.mesh")) {
      Fail "Provisioning profile is not for net.meshrelay.mesh."
    }
    if ($profileText -match '<key>ProvisionedDevices</key>' -or
        $profileText -match '<key>ProvisionsAllDevices</key>') {
      Fail "Provisioning profile is ad-hoc/development/in-house, not App Store distribution."
    }
    if ($profileText -notmatch '(?s)<key>get-task-allow</key>\s*<false\s*/>') {
      Fail "Provisioning profile allows debugging; expected App Store distribution."
    }
    if ($CodesignKey -match '\(([A-Z0-9]{10})\)\s*$') {
      $teamId = $Matches[1]
      if ($profileText -notmatch [regex]::Escape($teamId)) {
        Fail "Provisioning profile does not contain signing team $teamId."
      }
    }

    Ok "IPA identity, version, build, App Store profile, and privacy manifest verified"
  } finally {
    if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
  }
}

function Write-ReleaseManifest {
  $hash = (Get-FileHash $script:IpaPath -Algorithm SHA256).Hash.ToLowerInvariant()
  $item = Get-Item $script:IpaPath
  $manifest = [ordered]@{
    product = "Mesh"
    platform = "iOS"
    bundleId = "net.meshrelay.mesh"
    version = $Version
    buildNumber = $script:BuildNumber
    runtimeIdentifier = $RuntimeId
    artifact = $item.Name
    sizeBytes = $item.Length
    sha256 = $hash
    createdUtc = [DateTimeOffset]::UtcNow.ToString("O")
    testFlightUploadRequested = [bool]$PushTestFlight
    testFlightUploaded = $script:TestFlightUploaded
  }
  $script:ManifestPath = Join-Path $script:RunArtifacts "Mesh-iOS-v$Version-build$script:BuildNumber.json"
  $manifest | ConvertTo-Json | Set-Content $script:ManifestPath -Encoding utf8
  Ok "release manifest: $script:ManifestPath"
}

# ------------------------------------------------------------------ publish ---
function Invoke-GitCommitPush {
  if ($SkipPush) { Warn "skipping git commit + push"; return }
  if ($DryRun) { Warn "DryRun: skipping git commit + push"; return }

  Say "Git: commit iOS version/build + push"
  Push-Location $RepoRoot
  try {
    if ((Get-FileSha $Csproj) -ne $script:PatchedCsprojHash) {
      Fail "Mesh.App.csproj changed concurrently after the release patch; refusing to commit."
    }
    $changes = @(& git status --porcelain --untracked-files=all)
    $unexpected = @($changes | Where-Object {
      $_ -notmatch '^\sM\s+src/Mesh\.App/Mesh\.App\.csproj$'
    })
    if ($unexpected.Count -gt 0) {
      Fail "Unexpected worktree changes appeared during the build; refusing to commit."
    }

    Invoke-Native "git" @("add", "src/Mesh.App/Mesh.App.csproj") "git add"
    $stagedBlob = Invoke-NativeOutput "git" @(
      "rev-parse", ":src/Mesh.App/Mesh.App.csproj"
    ) "read staged csproj blob"
    if ($stagedBlob -ne $script:PatchedCsprojBlob) {
      Fail "The staged csproj does not match the release patch; refusing to commit."
    }
    $staged = @(& git diff --cached --name-only)
    $unexpectedStaged = @($staged | Where-Object { $_ -ne "src/Mesh.App/Mesh.App.csproj" })
    if ($unexpectedStaged.Count -gt 0) {
      Fail "Unrelated files are staged; refusing to create the release commit."
    }

    & git diff --cached --quiet
    $diffExit = $LASTEXITCODE
    if ($diffExit -eq 1) {
      Invoke-Native "git" @(
        "-c", "user.name=Mesh Dev",
        "-c", "user.email=mesh@localhost",
        "commit", "-m", "Release iOS v$Version (build $script:BuildNumber)"
      ) "git commit"
      $script:VersionCommitted = $true
    } elseif ($diffExit -eq 0) {
      Note "no csproj version/build change to commit"
    } else {
      Fail "git diff --cached failed (exit $diffExit)."
    }
    Invoke-Native "git" @("push", "origin", "HEAD") "git push"
    Ok "pushed"
  } finally {
    Pop-Location
  }
}

function Publish-TestFlight {
  if (-not $PushTestFlight) {
    Note "TestFlight: pass -PushTestFlight to validate + upload through App Store Connect."
    return
  }
  if ($DryRun) {
    Warn "DryRun: skipping App Store Connect validation/upload"
    return
  }

  Say "App Store Connect: validate signed IPA"
  $auth = @(
    "--platform", "ios",
    "--apple-id", $AppStoreAppId,
    "--bundle-id", "net.meshrelay.mesh",
    "--bundle-version", "$script:BuildNumber",
    "--bundle-short-version-string", $Version,
    "--api-key", $AppStoreKeyId,
    "--api-issuer", $AppStoreIssuerId,
    "--output-format", "json"
  )
  Invoke-Altool (@("altool", "--validate-app", $script:IpaPath) + $auth) `
    "App Store Connect validation"
  Ok "App Store Connect validation passed"

  Say "App Store Connect: upload build (TestFlight)"
  Invoke-Altool (@(
    "altool", "--upload-package", $script:IpaPath
  ) + $auth + @("--wait", "--show-progress")) "App Store Connect upload"
  $script:TestFlightUploaded = $true
  Write-ReleaseManifest
  Ok "uploaded; Apple will process the build before it appears in TestFlight"
}

# --------------------------------------------------------------------- main ---
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
try {
  Say "Mesh iOS release v$Version (repo: $RepoRoot)"
  if ($DryRun) {
    Warn "DRY RUN: builds + signs in a temporary folder, publishes nothing irreversible."
    $script:RunArtifacts = Join-Path ([System.IO.Path]::GetTempPath()) (
      "mesh-ios-dryrun-" + [guid]::NewGuid().ToString("N"))
  }

  Test-Preflight
  Resolve-BuildNumber
  Set-ProjectVersion
  Invoke-EmDashLint
  Build-Ios
  Test-Ipa
  Write-ReleaseManifest
  Invoke-GitCommitPush
  Publish-TestFlight

  Say "Done in $([math]::Round($stopwatch.Elapsed.TotalMinutes, 1)) min"
  Ok "IPA: $script:IpaPath"
  Ok "manifest: $script:ManifestPath"
  if (-not $PushTestFlight) {
    Note "Next: upload the IPA with Transporter, or rerun locally on macOS with -PushTestFlight."
  }
} catch {
  $script:Failed = $true
  $script:ExitCode = 1
  Write-Host "  [fail] $($_.Exception.Message)" -ForegroundColor Red
} finally {
  if (($DryRun -or $script:Failed) -and
      -not $script:VersionCommitted -and
      $script:OriginalCsprojBytes -ne $null) {
    # If a failed commit staged our exact patched blob, remove it from the index first.
    Push-Location $RepoRoot
    try {
      $stagedBlob = (& git rev-parse ":src/Mesh.App/Mesh.App.csproj" 2>$null | Out-String).Trim()
      if ($stagedBlob -and $stagedBlob -eq $script:PatchedCsprojBlob) {
        & git restore --staged -- "src/Mesh.App/Mesh.App.csproj" *> $null
      }
    } finally {
      Pop-Location
    }

    # Restore only when the file is still ours. If another process/user changed it after
    # patching, preserve their bytes and fail loudly instead of overwriting their work.
    $currentHash = Get-FileSha $Csproj
    if ($currentHash -eq $script:PatchedCsprojHash) {
      [System.IO.File]::WriteAllBytes($Csproj, $script:OriginalCsprojBytes)
      Warn "restored Mesh.App.csproj"
    } elseif ($currentHash -ne $script:OriginalCsprojHash) {
      Write-Host "  [fail] Mesh.App.csproj changed concurrently; it was NOT overwritten." -ForegroundColor Red
      $script:ExitCode = 1
    }
  }
  if ($DryRun -and (Test-Path $script:RunArtifacts)) {
    Remove-Item $script:RunArtifacts -Recurse -Force
    Warn "DryRun: removed temporary release artifacts"
  }
}
if ($script:ExitCode -ne 0) { exit $script:ExitCode }
