# Mesh Release Handover

This document is for an agent taking over Mesh release engineering. It describes the release
process that was actually used for Mesh 1.8.0, including channel-specific commands, verification
requirements, partial-failure recovery, and known script behavior.

## 1. Source of truth

| Item | Value |
| --- | --- |
| Working repository | `C:\Users\ifain\source\repos\Mesh` |
| Source remote | `https://github.com/ilyafainberg/Mesh.git` |
| Source branch | `main` |
| Public GitHub release repository | `MeshRelayAI/Mesh` |
| Application ID / bundle ID | `net.meshrelay.mesh` |
| Windows public format | ZIP containing the signed EXE installer |
| Azure DevOps organization | `https://dev.azure.com/Quonkel` |
| Azure DevOps project | `Mesh` |
| iOS pipeline | ID `1`, name `Mesh iOS` |
| Blob account | `meshrelaydl` |
| Blob resource group | `rg-mesh` |
| Blob container | `releases` |
| Microsoft Store product ID | `cd4a1e7a-b612-419e-9503-f3c17e32bcc0` |
| Microsoft Store seller ID | `95246270` |
| Google Play package | `net.meshrelay.mesh` |
| Relay Container App | `mesh-relay` in `rg-mesh` |
| Relay ACR | `cad4d4d4706dacr.azurecr.io` |

Do not confuse the private source remote with the public release repository. Push source commits
to `origin`, but create public releases in `MeshRelayAI/Mesh`.

## 2. Current baseline

As of 2026-07-22:

| Channel | State |
| --- | --- |
| Source `main` | `35de05c` |
| Public GitHub release | `v1.8.0`, ZIP-only |
| Google Play | Production, versionCode `49` |
| Microsoft Store | Submission `1152921505701468324` in review |
| TestFlight | Mesh `1.8.0` build `20`, App Store Connect status `VALID` |
| Relay | `mesh-relay:v1.7.0`, revision `mesh-relay--v170`, Running |

The public Windows/Play/Store 1.8.0 build came from `9616bc4`. TestFlight build 20 includes the
iPhone hotfix commit `35de05c`. That hotfix was intentionally published to TestFlight only.

The repository currently has an untracked `Feedback\` folder containing user screenshots. Treat it
as user-owned content. Do not stage, delete, or publish it unless the owner explicitly asks.

## 3. Non-negotiable release rules

1. Never use U+2014 in source, comments, UI, release notes, or generated release text.
2. Public Windows releases are ZIP-only. Never attach the raw EXE to GitHub Releases.
3. Commit all feature and fix changes before running release automation.
4. Include this commit trailer:

   ```text
   Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
   ```

5. Increment Android `ApplicationVersion` for every Play upload.
6. Increment the iOS build number for every App Store Connect upload.
7. Do not deploy Relay unless Relay or shared wire behavior changed.
8. Treat each publication channel independently. Do not rerun every channel after one channel
   fails.
9. Verify the exact requested scope before publishing. A TestFlight-only hotfix must not touch
   GitHub Releases, Blob, Play, Microsoft Store, or Relay.
10. Never print, commit, or copy credential values into logs or documentation.

## 4. Release tooling map

| File | Purpose | Important behavior |
| --- | --- | --- |
| `_deploy\release.ps1` | Shared Windows/Android build and publish implementation | Its direct GitHub/Blob path publishes an EXE, so do not use it directly for the public Windows release |
| `_deploy\release-win.ps1` | Windows ZIP-only release wrapper | Correct public Windows path |
| `_deploy\release-android.ps1` | Signed Android AAB build | `-PushStores` ultimately targets Play internal testing, not production |
| `_deploy\release-ios.ps1` | Signed IPA build, validation, and TestFlight upload | Usually invoked by Azure Pipelines |
| `_deploy\publish-store.ps1` | Microsoft Store package update | Current version does not pass `--skipInitialPolling`; see Store workaround below |
| `_deploy\sync-deploy.ps1` | Stages Relay and Shared source into `_deploy\relay` | Run before ACR build |
| `_deploy\sign-release.ps1` | Alternative Trusted Signing helper | The main Windows release uses `signtool` plus Azure Code Signing dlib |
| `azure-pipelines-ios.yml` | Protected signed iOS pipeline | Manual only, `main` only |

The usage comments in scripts can lag their actual parameters. Read each `param(...)` block before
running it.

## 5. Credential and tool preflight

Required tools:

```powershell
dotnet --info
git --version
gh auth status
az account show
pwsh --version
msstore --version
```

Expected installed tools:

- .NET 10 SDK and MAUI workloads
- PowerShell 7+
- GitHub CLI
- Azure CLI plus Azure DevOps extension
- Microsoft Store Developer CLI
- Inno Setup 6
- JDK at `C:\Program Files\Android\openjdk\jdk-21.0.8`, or `JAVA_HOME`

Credential names only:

| Channel | Credential |
| --- | --- |
| Windows signing / Blob | Active `az login` |
| GitHub release | Active `gh auth login` with release repository access |
| Android signing | `_deploy\android-signing\mesh-upload.keystore`; password from `MESH_KEYSTORE_PASS` or ignored `CREDENTIALS.txt` |
| Google Play | `GOOGLE_PLAY_SA_JSON`, process or user environment variable |
| Microsoft Store | `MS_STORE_TENANT_ID`, `MS_STORE_CLIENT_SECRET`; optional `MS_STORE_SELLER_ID`, `MS_STORE_CLIENT_ID` |
| iOS pipeline | Secure files and secret variables configured in Azure Pipelines |

Never add ignored signing files, service-account JSON, `.p8`, `.p12`, provisioning profiles, or
credential text files to Git.

## 6. Decide release scope first

Write down which of these are authorized:

- Source push only
- Local Windows build
- GitHub/Blob Windows release
- Microsoft Store submission
- Google Play internal testing
- Google Play production
- TestFlight
- Relay deployment

Do not infer "full release" from an older plan. Use the latest explicit instruction.

## 7. Freeze and validate the release candidate

### 7.1 Inspect repository state

```powershell
Set-Location 'C:\Users\ifain\source\repos\Mesh'
git --no-pager status --short
git --no-pager diff --check
git --no-pager log -5 --oneline --decorate
git remote -v
```

Never reset, discard, or stage unrelated user changes.

### 7.2 Stop only repository-owned running clients

The Windows client locks build output and causes `MSB3021` / `MSB3027`.

```powershell
$meshProcesses = Get-CimInstance Win32_Process -Filter "Name = 'Mesh.App.exe'" |
  Where-Object { $_.ExecutablePath -like 'C:\Users\ifain\source\repos\Mesh\*' }

foreach ($process in $meshProcesses) {
  Stop-Process -Id $process.ProcessId -Force
}
```

Do not kill processes by name. Stop exact PIDs only.

### 7.3 Version fields

Update `src\Mesh.App\Mesh.App.csproj`:

```xml
<ApplicationDisplayVersion>X.Y.Z</ApplicationDisplayVersion>
<ApplicationVersion>N</ApplicationVersion>
<Version>X.Y.Z</Version>
```

- `ApplicationDisplayVersion` and `Version` are the semantic version.
- `ApplicationVersion` is the Android versionCode and default Apple build number.
- TestFlight can override the Apple build number in the pipeline without changing Android.

### 7.4 Required validation matrix

```powershell
dotnet test tests\Mesh.App.Tests\Mesh.App.Tests.csproj -c Release --nologo
dotnet build src\Mesh.Relay\Mesh.Relay.csproj -c Release --nologo
dotnet build src\Mesh.App\Mesh.App.csproj `
  -f net10.0-windows10.0.19041.0 -c Release --nologo
dotnet build src\Mesh.App\Mesh.App.csproj `
  -f net10.0-android -c Release --nologo
dotnet build src\Mesh.App\Mesh.App.csproj `
  -f net10.0-ios -c Release `
  -p:RuntimeIdentifier=iossimulator-x64 --nologo
```

For a TestFlight-only UI hotfix, the minimum gate is:

- All unit tests
- Windows compile, to catch Razor/CSS integration problems
- iOS simulator build
- JavaScript syntax checks for changed scripts
- Live or harness validation of the changed iPhone interaction
- Independent code review

### 7.5 U+2014 lint

```powershell
$em = [char]0x2014
$hits = Get-ChildItem src -Recurse -File `
  -Include *.cs,*.razor,*.js,*.css,*.html,*.xaml |
  Where-Object {
    $_.FullName -notmatch '\\(bin|obj|wwwroot\\lib)\\|\.min\.(js|css)$'
  } |
  Select-String -Pattern $em -SimpleMatch

if ($hits) {
  $hits | Select-Object Path, LineNumber
  throw 'U+2014 found'
}
```

### 7.6 Commit and push

Stage only intended files:

```powershell
git add <explicit file list>
git commit -m "<release change>" `
  -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
git push origin main
```

Confirm:

```powershell
git rev-parse HEAD
git rev-parse origin/main
```

The hashes must match before signed iOS CI or public release publication.

## 8. Windows, Blob, and GitHub

### 8.1 Use the ZIP-only wrapper

If the version fields and release changes are already committed:

```powershell
pwsh -NoProfile -File .\_deploy\release-win.ps1 `
  -Version X.Y.Z `
  -AndroidVersionCode N `
  -SkipPush
```

This builds:

- Self-contained Windows client
- Inno Setup installer
- Azure Trusted Signing signature
- ZIP containing exactly `Mesh-Setup-vX.Y.Z.exe`
- Versioned and latest Blob ZIPs
- GitHub release in `MeshRelayAI/Mesh`
- Matching GitHub release in `MeshRelayAI/Relay`
- GHCR images `ghcr.io/meshrelayai/relay:latest` and `:X.Y.Z`

Do not run `_deploy\release.ps1` directly for the public Windows release. Its shared publication
path uploads the raw EXE, which violates the ZIP-only policy.

### 8.2 Verify the installer and ZIP

```powershell
$exe = "_deploy\artifacts\Mesh-Setup-vX.Y.Z.exe"
$zip = "_deploy\artifacts\Mesh-Setup-vX.Y.Z.zip"

(Get-AuthenticodeSignature $exe).Status
Get-FileHash $exe -Algorithm SHA256
Get-FileHash $zip -Algorithm SHA256
```

The signature status must be `Valid`.

Verify public release:

```powershell
gh release view vX.Y.Z --repo MeshRelayAI/Mesh `
  --json url,assets,isDraft,isPrerelease,publishedAt

gh release view vX.Y.Z --repo MeshRelayAI/Relay `
  --json url,targetCommitish,isDraft,isPrerelease,publishedAt

gh run list --repo MeshRelayAI/Relay `
  --workflow publish-image.yml --event release --limit 1

docker pull ghcr.io/meshrelayai/relay:X.Y.Z

Invoke-WebRequest `
  -Uri "https://meshrelaydl.blob.core.windows.net/releases/Mesh-Setup-vX.Y.Z.zip" `
  -Method Head -UseBasicParsing

Invoke-WebRequest `
  -Uri "https://meshrelaydl.blob.core.windows.net/releases/Mesh-Setup-latest.zip" `
  -Method Head -UseBasicParsing
```

Both Blob URLs must return `200` and the same content length. GitHub must contain the ZIP and no
raw `Mesh-Setup-v*.exe` asset. The Relay release must exist, its image workflow must succeed, and
the versioned GHCR image must pull successfully.

## 9. Microsoft Store

### 9.1 Upload the signed Store EXE

The Store consumes the raw signed installer from a separate Blob path:

```powershell
$version = 'X.Y.Z'
$installer = "_deploy\artifacts\Mesh-Setup-v$version.exe"
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

Verify that the remote SHA-256 matches the local signed installer before editing the submission.

### 9.2 Configure Store CLI

```powershell
msstore reconfigure `
  --tenantId $env:MS_STORE_TENANT_ID `
  --sellerId 95246270 `
  --clientId f119e9e3-b77a-4ba6-9fb8-ca6858a66883 `
  --clientSecret $env:MS_STORE_CLIENT_SECRET
```

The client secret must belong to the Manager application, not the obsolete Developer application.

### 9.3 Reliable package update

Microsoft documents the `submission get -> edit JSON -> submission update` flow. In practice,
MSStore CLI `0.3.7.5` can fail or hang during its initial submission poll. Use the documented
`--skipInitialPolling` switch.

```powershell
$productId = 'cd4a1e7a-b612-419e-9503-f3c17e32bcc0'
$url = "https://meshrelaydl.blob.core.windows.net/releases/store/Mesh-Setup-v$version.exe"

$packageJson = @(& msstore submission get $productId) -join [Environment]::NewLine
if ($LASTEXITCODE -ne 0) { throw 'Store get failed' }

$package = $packageJson | ConvertFrom-Json
$target = @($package.Packages)[0]
$target.PackageUrl = $url
$target.InstallerParameters = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
$target.IsSilentInstall = $true

$payload = $package | ConvertTo-Json -Depth 20 -Compress
msstore submission update $productId $payload --skipInitialPolling --verbose
if ($LASTEXITCODE -ne 0) { throw 'Store update failed' }

msstore submission publish $productId --verbose
if ($LASTEXITCODE -ne 0) { throw 'Store publish failed' }
```

### 9.4 Polling and partial success

`msstore submission poll` can run for a long time and may exit nonzero while certification is still
active. Always query:

```powershell
msstore submission status $productId --verbose
```

This response means the publish succeeded and the submission is in review:

```json
{
  "ResponseData": {
    "IsReady": false,
    "OngoingSubmissionId": "<submission-id>"
  }
}
```

An accompanying message saying the product already has one active submission is not a release
failure. Record the submission ID and stop retrying.

## 10. Android and Google Play

### 10.1 Build the signed AAB

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

Verify:

```powershell
$env:JAVA_HOME = 'C:\Program Files\Android\openjdk\jdk-21.0.8'
& "$env:JAVA_HOME\bin\jarsigner.exe" -verify `
  "src\Mesh.App\bin\Release\net10.0-android\net.meshrelay.mesh-Signed.aab"
```

### 10.2 Important track behavior

`_deploy\release.ps1 -PushStores` sends Google Play to the `internal` track. It does not publish to
production.

For a production release, use the Android Publisher API with the same JWT logic already implemented
in `Push-GooglePlay` inside `_deploy\release.ps1`, but update the `production` track.

Required API sequence:

1. Read the service account JSON from `GOOGLE_PLAY_SA_JSON`.
2. Mint an OAuth token with scope `https://www.googleapis.com/auth/androidpublisher`.
3. `POST /applications/net.meshrelay.mesh/edits`.
4. Upload the signed AAB to the edit's `bundles` endpoint.
5. Confirm the returned versionCode is exactly the intended value.
6. `PUT` the production track:

   ```json
   {
     "track": "production",
     "releases": [
       {
         "status": "completed",
         "versionCodes": ["N"],
         "releaseNotes": [
           {
             "language": "en-US",
             "text": "<release notes>"
           }
         ]
       }
     ]
   }
   ```

7. Commit the edit with `POST /edits/{editId}:commit`.
8. Create a new read-only edit and verify production reports status `completed` and versionCode `N`.

Do not reuse an edit after committing it.

### 10.3 Emulator APK

The preview MAUI SDK has produced APKs named `Signed.apk` that were not always signed as expected.
For emulator-only testing, manually `zipalign` and `apksigner` if installation reports a signature
or invalid-APK error. This does not replace AAB signing verification for Play.

To preserve emulator test data, sign the APK with the same Feincraft upload key as the installed
candidate. Do not uninstall unless data loss is acceptable.

## 11. TestFlight

### 11.1 Queue the protected pipeline

The source commit must be pushed to `main`.

```powershell
$version = 'X.Y.Z'
$buildNumber = 123

az pipelines run `
  --id 1 `
  --branch main `
  --parameters `
    version=$version `
    buildNumber=$buildNumber `
    pushTestFlight=true `
  -o json
```

Capture the returned pipeline run ID. It is not conceptually the same as the Apple build number,
even when the numbers happen to match.

### 11.2 Track the pipeline

```powershell
$runId = <run-id>

do {
  $run = az pipelines runs show --id $runId -o json | ConvertFrom-Json
  "$($run.status) / $($run.result)"
  if ($run.status -ne 'completed') { Start-Sleep -Seconds 60 }
} while ($run.status -ne 'completed')

if ($run.result -ne 'succeeded') {
  throw "iOS pipeline failed: $($run.result)"
}
```

The Azure CLI warning about not finding an Azure DevOps Git remote is benign when defaults already
point to organization `Quonkel` and project `Mesh`.

### 11.3 Verify Apple delivery

Download the pipeline artifact:

```powershell
az pipelines runs artifact list --run-id $runId -o table

az pipelines runs artifact download `
  --run-id $runId `
  --artifact-name "Mesh-iOS-$version-$runId" `
  --path "<local evidence folder>"
```

The artifact suffix uses `Build.BuildId`, which is the pipeline run ID.

Inspect the build log and manifest. Required evidence:

- Apple code signature verified
- MSAL keychain entitlement verified
- App identity, version, build, provisioning profile, and privacy manifest verified
- `VERIFY SUCCEEDED with no errors`
- `UPLOAD SUCCEEDED with no errors`
- Final `build-status` is `VALID`
- Final `bundle-version` equals the requested Apple build number
- Manifest has `testFlightUploaded: true`

Do not report success based only on the Azure Pipeline result. The Apple log must reach `VALID`.

### 11.4 TestFlight-only hotfix

For an iOS-only hotfix:

1. Fix and validate source.
2. Keep the semantic version if appropriate.
3. Commit and push `main`.
4. Choose the next unused Apple build number.
5. Run only the iOS pipeline.
6. Confirm GitHub release assets, Play versionCode, Store submission, Blob files, and Relay revision
   were not changed.

Example from 1.8.0:

- Source commit: `35de05c`
- Apple build: `20`
- GitHub/Blob: not republished
- Play: remained versionCode `49`
- Store: remained submission `1152921505701468324`
- Relay: remained `mesh-relay:v1.7.0`

## 12. Relay deployment

Deploy Relay only if one of these changed:

- `src\Mesh.Relay`
- Shared contracts or behavior required by Relay
- Hosted model endpoint behavior
- Quota/metering behavior
- Relay persistence, routing, or backplane behavior

Current deployment:

```text
Image: cad4d4d4706dacr.azurecr.io/mesh-relay:v1.7.0
Revision: mesh-relay--v170
Container App: mesh-relay
Resource group: rg-mesh
Health: Running
```

Release flow:

```powershell
Set-Location 'C:\Users\ifain\source\repos\Mesh'

pwsh -NoProfile -File .\_deploy\sync-deploy.ps1

az acr build `
  --registry cad4d4d4706dacr `
  --image "mesh-relay:vX.Y.Z" `
  .\_deploy\relay

az containerapp update `
  --resource-group rg-mesh `
  --name mesh-relay `
  --image "cad4d4d4706dacr.azurecr.io/mesh-relay:vX.Y.Z"
```

Verify:

```powershell
az containerapp show -g rg-mesh -n mesh-relay `
  --query '{image:properties.template.containers[0].image,latestRevision:properties.latestRevisionName,runningStatus:properties.runningStatus,fqdn:properties.configuration.ingress.fqdn}' `
  -o json

Invoke-WebRequest -Uri 'https://meshrelay.net/health' -UseBasicParsing
```

Confirm the new image, healthy revision, and `200` health response. If the Container App uses
multiple revision mode, explicitly confirm the intended revision has 100 percent traffic.

## 13. Partial failure recovery

Never restart the complete release blindly.

### Windows build failed before signing

- Stop the exact repository-owned `Mesh.App.exe` PID.
- Rebuild Windows only.
- Do not touch already completed channels.

### Windows installer signed, Store failed

- Keep the signed EXE.
- Check whether Store Blob upload already succeeded.
- Use `submission update --skipInitialPolling`.
- Publish and query `submission status`.
- Resume ZIP/Blob/GitHub separately if they were not reached.

### GitHub release exists but asset is wrong

```powershell
gh release upload vX.Y.Z <zip> --repo MeshRelayAI/Mesh --clobber
gh release delete-asset vX.Y.Z <bad-asset> --repo MeshRelayAI/Mesh --yes
```

Keep only the ZIP.

### Play upload succeeded but track update failed

- Inspect the edit before abandoning it.
- If it was not committed, correct the track and commit the same edit.
- If it was committed, create a new edit for verification or correction.

### iOS pipeline succeeded but TestFlight is missing

- Inspect the App Store Connect upload log.
- Azure success without `UPLOAD SUCCEEDED` and `VALID` is not enough.
- Do not reuse the same Apple build number.

### Store poll exits nonzero

- Query `msstore submission status`.
- If an `OngoingSubmissionId` exists, the submission is already active.
- Record the ID and stop retrying.

## 14. Post-release evidence

Record:

| Channel | Evidence |
| --- | --- |
| Source | Commit hash equals `origin/main` |
| Tests | Exact passed/failed/skipped count |
| Windows | Valid Authenticode signature and ZIP hash |
| GitHub | Release URL and asset list |
| Blob | Versioned/latest HEAD status and content length |
| Microsoft Store | Submission ID and state |
| Google Play | Track, status, versionCode |
| TestFlight | Pipeline run, source commit, Apple build, delivery UUID, `VALID` |
| Relay | Image, revision, traffic, health response |

Do not say "released" until every authorized channel has either:

- Published successfully,
- Reached its expected review state, or
- Been explicitly reported as blocked.

## 15. Final agent checklist

```text
[ ] Confirm authorized channels
[ ] Inspect and preserve unrelated worktree changes
[ ] Set semantic version, Android versionCode, and Apple build number
[ ] Run U+2014 lint
[ ] Run tests and required platform builds
[ ] Perform live UX validation
[ ] Run independent code review
[ ] Commit with Copilot trailer
[ ] Push main and verify origin/main
[ ] Publish only authorized channels
[ ] Verify every channel independently
[ ] Record submission IDs, build numbers, hashes, and URLs
[ ] Clean temporary worktrees, test servers, and emulators
[ ] Leave user-owned files untouched
```

## 16. Official references

- Microsoft Store Developer CLI commands:
  `https://learn.microsoft.com/en-us/windows/apps/publish/msstore-dev-cli/commands-exe`
- Azure DevOps CLI pipeline run:
  `https://learn.microsoft.com/en-us/cli/azure/pipelines?view=azure-cli-latest#az-pipelines-run`
- .NET MAUI iOS CLI publishing:
  `https://learn.microsoft.com/en-us/dotnet/maui/ios/deployment/publish-cli?view=net-maui-10.0`
