# Mesh Release Process

This runbook is the source of truth for human and agent release managers publishing Mesh.
It covers every operating system targeted by the repository, the Relay, public artifacts,
store submissions, verification, recovery, and evidence collection.

The commands assume PowerShell 7 unless a section explicitly requires macOS shell tools.

## 1. Audience and operating rules

This document is written for:

- Human release managers
- Coding agents operating with explicit release authorization
- Engineers diagnosing a partial or failed release

Release managers must follow these rules:

1. Release only the channels explicitly authorized for the current request.
2. Treat each channel independently. Do not rerun successful channels to recover one failure.
3. Never stage, delete, reset, or overwrite unrelated worktree changes.
4. Never use `git add -A` for a release. Stage only intentional files.
5. Never print, commit, or copy passwords, private keys, access tokens, service-account JSON,
   provisioning profiles, or signing certificates into logs or documentation.
6. Never reuse an Android versionCode or Apple build number.
7. Public Windows GitHub releases are ZIP-only. Never attach the raw installer EXE.
8. Do not deploy the Relay unless Relay or shared wire behavior changed.
9. Do not claim a channel is released until its channel-specific verification passes.
10. Do not use U+2014 in source, comments, UI copy, release notes, or generated text.
11. Source releases come from `main`, unless the owner explicitly authorizes another branch.
12. Every release commit created by an agent includes:

    ```text
    Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
    ```

## 2. Product and channel matrix

| Target | Framework or runtime | Artifact | Distribution channel | Current automation |
| --- | --- | --- | --- | --- |
| Windows x64 | `net10.0-windows10.0.19041.0` | Signed Inno Setup EXE inside ZIP | GitHub, Azure Blob, Microsoft Store | `_deploy\release-win.ps1` |
| Windows ARM64 | x64 compatibility mode | Same Windows x64 installer | Same Windows channels | No native ARM64 artifact |
| Android | `net10.0-android` | Signed AAB | Google Play | `_deploy\release-android.ps1` |
| iOS and iPadOS | `net10.0-ios`, `ios-arm64` | Signed IPA | TestFlight, App Store | `azure-pipelines-ios.yml` |
| macOS | `net10.0-maccatalyst` | Signed PKG | Mac App Store or notarized direct download | Build support only; no Mesh production pipeline |
| Linux desktop | Not targeted | None | None | Not supported |
| Relay on Linux | ASP.NET Core container | OCI image | GHCR, Azure Container Apps, self-hosting | `_deploy\publish-relay-release.ps1` |

The absence of a production pipeline is not permission to improvise a store release. Mac Catalyst
must remain reported as build-only until signing, notarization, store credentials, and physical
validation are configured and approved.

## 3. Fixed release identifiers

| Item | Value |
| --- | --- |
| Source repository | `C:\Users\ifain\source\repos\Mesh` |
| Source remote | `https://github.com/ilyafainberg/Mesh.git` |
| Source branch | `main` |
| Public client release repository | `MeshRelayAI/Mesh` |
| Public Relay repository | `MeshRelayAI/Relay` |
| Application and bundle ID | `net.meshrelay.mesh` |
| Azure DevOps organization | `https://dev.azure.com/Quonkel` |
| Azure DevOps project | `Mesh` |
| iOS pipeline | ID `1`, name `Mesh iOS` |
| Blob account | `meshrelaydl` |
| Blob resource group | `rg-mesh` |
| Blob container | `releases` |
| Microsoft Store product | Mesh Relay |
| Microsoft Store product ID | `cd4a1e7a-b612-419e-9503-f3c17e32bcc0` |
| Microsoft Store seller ID | `95246270` |
| Google Play package | `net.meshrelay.mesh` |
| Relay resource group | `rg-mesh` |
| Relay Container App | `mesh-relay` |
| Relay ACR | `cad4d4d4706dacr.azurecr.io` |

The private source remote and public release repositories are different. Push source commits to
`origin`; create public binaries and release notes in `MeshRelayAI/Mesh`.

## 4. Version model

The application project contains:

```xml
<ApplicationDisplayVersion>X.Y.Z</ApplicationDisplayVersion>
<ApplicationVersion>N</ApplicationVersion>
<Version>X.Y.Z</Version>
```

Located at:

```text
src\Mesh.App\Mesh.App.csproj
```

Rules:

- `ApplicationDisplayVersion` is the user-facing semantic version.
- `Version` drives assembly, file, updater, and informational versioning.
- `ApplicationVersion` is the Android versionCode and default Apple build number.
- Every Google Play upload requires a strictly higher Android versionCode.
- Every App Store Connect upload requires a strictly higher Apple build number.
- The iOS pipeline can override the Apple build number without changing Android.
- A semantic version may be shared across channels while channel build numbers differ.
- Store rejection after upload does not make a build number reusable.

Create a release record before starting:

```text
Semantic version:
Source commit:
Android versionCode:
Apple build number:
Authorized channels:
Release notes approved:
```

## 5. Tool and credential preflight

### 5.1 Common tools

```powershell
dotnet --info
git --version
gh auth status
az account show
pwsh --version
```

Expected:

- .NET 10 SDK and MAUI workloads
- PowerShell 7+
- Git and GitHub CLI
- Azure CLI
- Azure DevOps CLI extension for iOS pipeline control
- Inno Setup 6 for Windows
- Microsoft Store Developer CLI for Store submission
- JDK 21 and Android SDK for Android
- Xcode on a supported Mac for iOS and Mac Catalyst

Mesh 1.19.0 uses the approved patched .NET SDK `10.0.302`. Keep local, pipeline, workload, and Xcode versions aligned.
Do not let workload installation silently select a manifest that requires a newer unavailable Xcode.

### 5.2 Credential names

| Channel | Required credential or secure file |
| --- | --- |
| Windows signing | Active Azure login authorized for Artifact Signing |
| GitHub release | Authenticated `gh` session with public release-repository access |
| Blob upload | Active Azure login or storage-account key retrieval permission |
| Microsoft Store | `MS_STORE_TENANT_ID`, `MS_STORE_CLIENT_SECRET`; optional seller/client overrides |
| Android signing | `_deploy\android-signing\mesh-upload.keystore`, alias `mesh-upload` |
| Android signing password | `MESH_KEYSTORE_PASS` or ignored `CREDENTIALS.txt` |
| Google Play | `GOOGLE_PLAY_SA_JSON` pointing to an ignored service-account JSON file |
| iOS signing | Azure secure files: distribution P12 and App Store provisioning profile |
| TestFlight upload | App Store Connect P8 plus `ASC_APP_ID`, `ASC_KEY_ID`, `ASC_ISSUER_ID` |
| Mac Catalyst | Appropriate Apple distribution/installer certificates and provisioning profile |

The registered Google Play upload certificate SHA1 is:

```text
F2:46:B3:6A:47:84:D3:29:CA:6B:28:01:8C:AD:0C:68:60:34:42:D0
```

Abort an Android release if the AAB or keystore fingerprint differs. A JAR can have a valid
signature while still using the wrong Google Play upload key.

## 6. Freeze the release candidate

### 6.1 Inspect source state

```powershell
Set-Location 'C:\Users\ifain\source\repos\Mesh'
git fetch origin --prune
git --no-pager status --short
git --no-pager diff --check
git --no-pager log -5 --oneline --decorate
git remote -v
git rev-list --left-right --count main...origin/main
```

Required before publication:

- Correct repository and branch
- Intended source commit
- No unknown tracked changes
- No merge conflict or unfinished rebase
- Local `main` and `origin/main` synchronized after the release commit

Do not reset a dirty tree. Resolve ownership of every change first.

### 6.2 Confirm channel scope

Possible scopes:

- Source push only
- Windows local build
- GitHub Windows release
- Microsoft Store
- Android internal testing
- Google Play production
- iOS IPA only
- TestFlight
- App Store production
- Mac Catalyst validation build
- Mac App Store
- Notarized Mac direct download
- Relay public image
- Relay production deployment

Publication to one channel does not imply authorization for another.

### 6.3 Validate source

Use the pinned SDK when available:

```powershell
$dotnet = 'C:\Users\ifain\source\repos\dotnet-sdk-10.0.302-win-x64\dotnet.exe'
if (-not (Test-Path $dotnet)) { throw 'Approved .NET SDK 10.0.302 is required.' }

& $dotnet test .\tests\Mesh.App.Tests\Mesh.App.Tests.csproj -c Release --nologo
& $dotnet build .\src\Mesh.Relay\Mesh.Relay.csproj -c Release --nologo
& $dotnet build .\src\Mesh.App\Mesh.App.csproj `
  -f net10.0-windows10.0.19041.0 -c Release --nologo
& $dotnet build .\src\Mesh.App\Mesh.App.csproj `
  -f net10.0-android -c Release --nologo
```

Also:

- Run the repository U+2014 lint.
- Run JavaScript syntax validation for changed JavaScript.
- Perform physical UX tests for changed platform behavior.
- Verify responsiveness, clipping, layering, navigation, and accessibility.
- Request an independent blocker-focused code review before release.

iOS and Mac Catalyst validation must run on a Mac or protected macOS pipeline.

## 7. Commit and push the release candidate

Stage only intended files:

```powershell
git add src\Mesh.App\Mesh.App.csproj
git add <other-intended-files>
git --no-pager diff --cached --check
git --no-pager diff --cached --stat
```

Commit with the required trailer:

```text
Release Mesh X.Y.Z

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
```

Push and verify:

```powershell
git push origin main
$local = git rev-parse HEAD
$remote = git rev-parse origin/main
if ($local -ne $remote) { throw 'origin/main does not match the release commit.' }
```

Do not start a protected iOS pipeline from an unpushed commit.

## 8. Windows release

### 8.1 Supported output

Mesh publishes one Windows x64-compatible installer. Windows ARM64 runs it through x64
compatibility. No native ARM64 package is currently published.

Public format:

```text
Mesh-Setup-vX.Y.Z.zip
  Mesh-Setup-vX.Y.Z.exe
```

The ZIP must contain exactly one signed installer.

### 8.2 Dry run

```powershell
pwsh -NoProfile -File .\_deploy\release-win.ps1 `
  -Version X.Y.Z `
  -AndroidVersionCode N `
  -DryRun
```

### 8.3 Publish GitHub and Blob

```powershell
pwsh -NoProfile -File .\_deploy\release-win.ps1 `
  -Version X.Y.Z `
  -AndroidVersionCode N `
  -NotesFile .\release-notes.md
```

The wrapper:

1. Updates version fields.
2. Runs release lint.
3. Publishes the Windows self-contained app.
4. Removes non-Windows Playwright drivers.
5. Builds the Inno Setup installer.
6. Signs the installer with Azure Artifact Signing.
7. Verifies Authenticode.
8. Creates and validates the ZIP.
9. Pushes source changes unless skipped.
10. Uploads versioned and latest ZIPs to Blob.
11. Creates or updates the public GitHub release.
12. Removes raw EXE assets from the public release.
13. Publishes the version-matched public Relay release/image workflow.

Use `release-win.ps1`, not the shared `release.ps1`, for public Windows publication. The wrapper
enforces the ZIP-only rule.

### 8.4 Verify Windows

```powershell
$exe = ".\_deploy\artifacts\Mesh-Setup-vX.Y.Z.exe"
$zip = ".\_deploy\artifacts\Mesh-Setup-vX.Y.Z.zip"

Get-AuthenticodeSignature $exe |
  Select-Object Status, @{n='Signer';e={$_.SignerCertificate.Subject}}
Get-FileHash $zip -Algorithm SHA256

gh release view vX.Y.Z --repo MeshRelayAI/Mesh `
  --json url,tagName,isDraft,isPrerelease,assets

Invoke-WebRequest `
  -Method Head `
  -Uri "https://meshrelaydl.blob.core.windows.net/releases/Mesh-Setup-vX.Y.Z.zip"
```

Verify:

- Authenticode status is `Valid`.
- Signer is the approved publisher.
- ZIP contains one EXE with the expected name.
- Public GitHub release contains the ZIP and no raw installer.
- Blob versioned and latest URLs return 200.
- Installed About/version reports the requested version.
- Only the Windows Playwright runtime is present.

## 9. Microsoft Store

The Microsoft Store consumes the raw signed installer from a direct Blob URL, not the public ZIP.

### 9.1 Upload Store installer

```powershell
$version = 'X.Y.Z'
$installer = ".\_deploy\artifacts\Mesh-Setup-v$version.exe"
$key = az storage account keys list `
  --account-name meshrelaydl `
  --resource-group rg-mesh `
  --query '[0].value' -o tsv

az storage blob upload `
  --account-name meshrelaydl `
  --container-name releases `
  --name "store/Mesh-Setup-v$version.exe" `
  --file $installer `
  --account-key $key `
  --overwrite `
  --only-show-errors
```

Download the public URL and verify that its SHA-256 equals the signed local installer before
editing the Store submission.

### 9.2 Preferred Store script

```powershell
pwsh -NoProfile -File .\_deploy\publish-store.ps1 `
  -Version X.Y.Z `
  -SkipBuild
```

Use `-DraftOnly` when the package should be prepared but not submitted.

### 9.3 Reliable manual CLI flow

If the Store CLI hangs during initial polling, use the documented update flow with
`--skipInitialPolling`:

```powershell
$productId = 'cd4a1e7a-b612-419e-9503-f3c17e32bcc0'
$packageJson = @(& msstore submission get $productId) -join [Environment]::NewLine
if ($LASTEXITCODE -ne 0) { throw 'Store submission get failed.' }

$package = $packageJson | ConvertFrom-Json
$target = @($package.Packages)[0]
$target.PackageUrl =
  'https://meshrelaydl.blob.core.windows.net/releases/store/Mesh-Setup-vX.Y.Z.exe'
$target.InstallerParameters = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
$target.IsSilentInstall = $true
$payload = $package | ConvertTo-Json -Depth 20 -Compress

msstore submission update $productId $payload --skipInitialPolling --verbose
if ($LASTEXITCODE -ne 0) { throw 'Store submission update failed.' }

msstore submission publish $productId --verbose
if ($LASTEXITCODE -ne 0) { throw 'Store submission publish failed.' }

msstore submission status $productId --verbose
```

An `OngoingSubmissionId` with `IsReady=false` means certification is active. Record the ID and do
not submit a duplicate.

## 10. Android release

### 10.1 Build signed AAB

Run Windows release first when shipping the same semantic version to Windows and Android.

```powershell
pwsh -NoProfile -File .\_deploy\release-android.ps1 `
  -Version X.Y.Z `
  -AndroidVersionCode N `
  -SkipPush
```

Expected artifact:

```text
src\Mesh.App\bin\Release\net10.0-android\net.meshrelay.mesh-Signed.aab
```

### 10.2 Verify signing

```powershell
$env:JAVA_HOME = 'C:\Program Files\Android\openjdk\jdk-21.0.8'
$aab = '.\src\Mesh.App\bin\Release\net10.0-android\net.meshrelay.mesh-Signed.aab'

& "$env:JAVA_HOME\bin\jarsigner.exe" -verify $aab
& "$env:JAVA_HOME\bin\keytool.exe" -printcert -jarfile $aab |
  Select-String 'SHA1:'
Get-FileHash $aab -Algorithm SHA256
```

The SHA1 must match the registered upload certificate in section 5.2.

### 10.3 Internal testing

`release-android.ps1 -PushStores` uses the existing API implementation and targets Google Play
internal testing. It does not publish production.

```powershell
pwsh -NoProfile -File .\_deploy\release-android.ps1 `
  -Version X.Y.Z `
  -AndroidVersionCode N `
  -PushStores
```

### 10.4 Google Play production

Production requires explicit authorization.

Human Play Console flow:

1. Open Google Play Console for `net.meshrelay.mesh`.
2. Select Production.
3. Create a new release.
4. Upload the verified signed AAB.
5. Confirm the displayed versionCode is exactly `N`.
6. Enter approved release notes.
7. Review warnings and supported-device changes.
8. Start the explicitly approved rollout.

Agent Android Publisher API flow:

1. Read the ignored service account file from `GOOGLE_PLAY_SA_JSON`.
2. Mint a token for `https://www.googleapis.com/auth/androidpublisher`.
3. Create a new edit.
4. Upload the AAB to the edit.
5. Abort if the returned versionCode is not exactly `N`.
6. Put the `production` track with status `completed` for full rollout, or the explicitly approved
   staged rollout fraction.
7. Commit the edit.
8. Create a fresh read-only edit.
9. Verify production reports the expected release name, versionCode, notes, and status.
10. Delete or abandon the verification edit.

Never reuse an edit after it is committed.

## 11. iOS and iPadOS release

The protected Azure Pipeline is the preferred path. It is manual-only and restricted to `main`.

### 11.1 Pipeline prerequisites

Azure DevOps secure files:

```text
Mesh-Apple-Distribution.p12
Mesh-AppStore.mobileprovision
Mesh-AppStore-Connect.p8
```

Pipeline variables:

```text
APPLE_CERT_PASSWORD
ASC_APP_ID
ASC_KEY_ID
ASC_ISSUER_ID
```

Secure files require branch control and approval checks.

### 11.2 Run signed IPA build only

```powershell
az pipelines run `
  --id 1 `
  --branch main `
  --organization https://dev.azure.com/Quonkel `
  --project Mesh `
  --parameters version=X.Y.Z buildNumber=N pushTestFlight=false
```

### 11.3 Upload TestFlight

```powershell
az pipelines run `
  --id 1 `
  --branch main `
  --organization https://dev.azure.com/Quonkel `
  --project Mesh `
  --parameters version=X.Y.Z buildNumber=N pushTestFlight=true
```

The pipeline:

1. Checks out clean `main`.
2. Pins .NET and Xcode.
3. Installs the required MAUI workloads.
4. Installs the protected Apple certificate and provisioning profile.
5. Runs `_deploy\release-ios.ps1`.
6. Builds and signs `ios-arm64`.
7. Validates the IPA, privacy manifest, and signature.
8. Optionally uploads to App Store Connect.
9. Publishes IPA and manifest as protected pipeline artifacts.

### 11.4 Verify TestFlight

```powershell
az pipelines runs show `
  --id <run-id> `
  --organization https://dev.azure.com/Quonkel `
  --project Mesh `
  -o json
```

Required evidence:

- Pipeline source commit equals the approved release commit.
- Pipeline result is `succeeded`.
- IPA validation succeeded.
- Upload output contains an Apple delivery identifier.
- App Store Connect processing reaches `VALID`.
- Version and Apple build number match.
- TestFlight build is available to the intended tester group.

Pipeline success without successful Apple validation/upload is not sufficient.

### 11.5 App Store production

TestFlight upload does not publish the App Store.

Human App Store Connect flow:

1. Create or open the App Store version.
2. Select the processed build.
3. Complete export compliance, privacy, age-rating, and review information.
4. Verify screenshots and release notes.
5. Submit for review.
6. Select manual, phased, or automatic release according to explicit approval.
7. Record review state and release date.

Never reuse an Apple build number after any upload attempt.

## 12. macOS and Mac Catalyst

The project targets `net10.0-maccatalyst`, but Mesh does not currently have a protected production
pipeline, approved Mac signing assets, notarization automation, Mac App Store record, or completed
physical release validation. Treat this as build support, not a shipping channel.

### 12.1 Validation build

Run on macOS:

```powershell
dotnet build ./src/Mesh.App/Mesh.App.csproj `
  -f net10.0-maccatalyst `
  -c Release `
  --nologo
```

Release builds should contain both `maccatalyst-x64` and `maccatalyst-arm64` unless an approved
distribution explicitly targets one architecture.

### 12.2 Direct distribution candidate

After certificates and profiles are configured, the Microsoft-supported pattern is:

```powershell
dotnet publish ./src/Mesh.App/Mesh.App.csproj `
  -f net10.0-maccatalyst `
  -c Release `
  -p:MtouchLink=SdkOnly `
  -p:CreatePackage=true `
  -p:EnableCodeSigning=true `
  -p:EnablePackageSigning=true `
  -p:CodesignKey='Developer ID Application: <Organization> (<TeamId>)' `
  -p:CodesignProvision='<Non-App-Store profile>' `
  -p:CodesignEntitlements='Platforms/MacCatalyst/Entitlements.plist' `
  -p:PackageSigningKey='Developer ID Installer: <Organization> (<TeamId>)' `
  -p:UseHardenedRuntime=true
```

Notarize and staple:

```powershell
xcrun notarytool submit <Mesh.pkg> --wait `
  --apple-id <AppleId> `
  --password <AppSpecificPassword> `
  --team-id <TeamId>

xcrun stapler staple <Mesh.pkg>
xcrun stapler validate <Mesh.pkg>
```

Do not place credentials directly into shared logs. Prefer a stored notarytool keychain profile.

### 12.3 Mac App Store candidate

Use the Mac App Store distribution certificate, provisioning profile, entitlements, and installer
certificate. Submit through App Store Connect only after:

- Mac-specific privacy and entitlement review
- Intel and Apple Silicon physical tests
- Signing verification
- Store listing and screenshots
- Explicit production authorization

Until those gates exist, report Mac Catalyst as not released.

## 13. Linux and Relay release

There is no Linux desktop client. Linux release work applies to the Relay container and
self-hosting assets.

### 13.1 Public Relay release and GHCR

```powershell
pwsh -NoProfile -File .\_deploy\publish-relay-release.ps1 `
  -Version X.Y.Z `
  -RepoRoot 'C:\Users\ifain\source\repos\Mesh'
```

The script:

1. Waits for the public Relay mirror to contain the committed Relay/Shared source.
2. Creates or refreshes `MeshRelayAI/Relay` release `vX.Y.Z`.
3. Runs the Relay image workflow.
4. Waits for version and latest GHCR tags.

Use `-DryRun` to validate intent without publication.

### 13.2 Production Azure Relay deployment

Deploy production only if `src\Mesh.Relay` or wire-affecting `src\Mesh.Shared` changed.

```powershell
$version = 'X.Y.Z'
$tag = "v$version-$(git rev-parse --short HEAD)"

pwsh -NoProfile -File .\_deploy\sync-deploy.ps1

az acr build `
  --registry cad4d4d4706dacr `
  --image "mesh-relay:$tag" `
  .\_deploy\relay

az containerapp update `
  --resource-group rg-mesh `
  --name mesh-relay `
  --image "cad4d4d4706dacr.azurecr.io/mesh-relay:$tag"
```

Verify:

```powershell
Invoke-RestMethod https://meshrelay.net/health

az containerapp revision list `
  --resource-group rg-mesh `
  --name mesh-relay `
  -o table

az containerapp logs show `
  --resource-group rg-mesh `
  --name mesh-relay `
  --tail 200
```

Confirm:

- Expected protocol version and capabilities
- `onlineOnly=true`
- `durablePayloadStorage=false`
- Healthy active revision
- Intended traffic allocation
- No startup, Cosmos, Redis, APNs, FCM, or authentication errors

## 14. Release verification matrix

| Channel | Required evidence |
| --- | --- |
| Source | Commit hash and equality with `origin/main` |
| Tests | Exact passed, failed, and skipped counts |
| Windows | Valid Authenticode signature, ZIP contents, ZIP SHA-256 |
| GitHub | Release URL, tag, draft/prerelease state, exact asset list |
| Blob | Versioned/latest URL status and hash/content length |
| Microsoft Store | Submission ID and state |
| Android | AAB SHA-256, certificate SHA1, versionCode |
| Google Play | Track, rollout status, versionCode, release notes |
| iOS | Pipeline run, source commit, IPA manifest, build number |
| TestFlight | Apple delivery ID and processing state `VALID` |
| App Store | Version, build, review state, release mode |
| Mac direct | Signature, notarization, staple validation, artifact hash |
| Mac App Store | Build, review state, release mode |
| Relay public | Relay release URL, GHCR tags, workflow run |
| Relay production | Image tag/digest, revision, traffic, health output |

Store evidence in the release record. Never store secrets with the evidence.

## 15. Partial failure recovery

### 15.1 General rule

Do not restart a full release after a channel-specific failure.

1. Identify the last irreversible successful step.
2. Preserve valid signed artifacts.
3. Resume only the failed channel.
4. Re-verify already completed prerequisites instead of recreating them.

### 15.2 Windows installer succeeded, GitHub failed

- Keep the signed EXE and validated ZIP.
- Retry Blob or GitHub independently.
- Use `gh release upload --clobber` when the release exists.
- Remove any accidentally published raw EXE.

```powershell
gh release upload vX.Y.Z .\_deploy\artifacts\Mesh-Setup-vX.Y.Z.zip `
  --repo MeshRelayAI/Mesh `
  --clobber
```

### 15.3 Microsoft Store poll failed

- Query `msstore submission status`.
- If `OngoingSubmissionId` exists, publication succeeded and certification is active.
- Record the ID and stop retrying.

### 15.4 Google Play upload succeeded, track update failed

- If the edit is uncommitted, correct and commit the same edit.
- If committed, create a new edit for verification or correction.
- Never upload another AAB with the same versionCode.

### 15.5 TestFlight upload failed

- Read the Apple validation/upload output.
- Do not treat an Azure pipeline success as an Apple upload success.
- Increment the Apple build number before retrying an upload.
- Reuse the same source commit unless a source fix is required.

### 15.6 Relay deployment failed

- Keep the previous healthy revision serving traffic.
- Do not delete old images or revisions during recovery.
- Correct configuration or deploy a new image tag.
- If required, restore the previous known-good image/revision and verify health.

## 16. Rollback policy

Prefer forward hotfixes for client stores because version/build numbers cannot be reused.

| Channel | Rollback approach |
| --- | --- |
| GitHub/Blob Windows | Publish a higher patch version; only replace an asset when correcting the same approved binary |
| Microsoft Store | Submit a corrected higher version; use Store controls to halt release if available |
| Google Play staged rollout | Halt the rollout before full deployment |
| Google Play completed rollout | Publish a corrected higher versionCode |
| TestFlight | Disable affected tester build and upload a higher build number |
| App Store | Use phased-release controls or submit a higher build/version |
| Mac direct | Remove bad download and publish a newly signed/notarized patch |
| Relay | Restore previous known-good image/revision, then verify health |

Never rewrite source history or force-push a release branch to simulate rollback.

## 17. Final release-manager checklist

```text
[ ] Confirm repository, branch, and source commit
[ ] Confirm explicit authorized channels
[ ] Preserve unrelated worktree files
[ ] Assign semantic version, Android versionCode, Apple build number
[ ] Verify signing and store credentials exist without printing them
[ ] Run lint, tests, platform builds, UX validation, and code review
[ ] Commit only intended files with the required trailer
[ ] Push main and verify origin/main
[ ] Build and verify Windows artifact
[ ] Publish GitHub/Blob only if authorized
[ ] Submit Microsoft Store only if authorized
[ ] Build and verify Android AAB and registered upload key
[ ] Publish the correct Play track only if authorized
[ ] Run protected iOS pipeline from main
[ ] Verify Apple upload and App Store Connect processing
[ ] Submit App Store production only if authorized
[ ] Treat Mac Catalyst as build-only unless its release gates are configured
[ ] Publish/deploy Relay only when Relay or shared wire behavior changed
[ ] Verify every channel independently
[ ] Record hashes, URLs, build numbers, submission IDs, workflow runs, and health
[ ] Report every blocked or review-pending channel honestly
```

## 18. Official references

- [.NET MAUI iOS command-line publishing](https://learn.microsoft.com/dotnet/maui/ios/deployment/publish-cli?view=net-maui-10.0)
- [.NET MAUI Mac Catalyst outside-store publishing](https://learn.microsoft.com/dotnet/maui/mac-catalyst/deployment/publish-outside-app-store?view=net-maui-10.0)
- [.NET MAUI Mac Catalyst Mac App Store publishing](https://learn.microsoft.com/dotnet/maui/mac-catalyst/deployment/publish-app-store?view=net-maui-10.0)
- [Microsoft Store Developer CLI EXE commands](https://learn.microsoft.com/windows/apps/publish/msstore-dev-cli/commands-exe)
- [Azure DevOps pipeline CLI](https://learn.microsoft.com/cli/azure/pipelines?view=azure-cli-latest)
- [Google Play Android Publisher API](https://developers.google.com/android-publisher)
- [Apple App Store Connect help](https://developer.apple.com/help/app-store-connect/)
