<#
  release-android.ps1  -  Android-only Mesh release

  Runs the release pipeline but SKIPS the Windows build, installer, signing, Blob upload
  and GitHub release (those are Windows-artifact steps). It does the version bump, em-dash
  lint, the signed Android AAB build, and the git commit+push, plus the optional Google
  Play push. Use this when you specifically need a new Android build.

  USAGE
    ./_deploy/release-android.ps1 -Version 1.5.8
    ./_deploy/release-android.ps1 -Version 1.5.8 -AndroidVersionCode 33
    ./_deploy/release-android.ps1 -Version 1.5.8 -DryRun
    ./_deploy/release-android.ps1 -Version 1.5.8 -PushStores   # also submit to Google Play

  Notes
    - The AAB build is the slow part of a release (AOT compile); that is why it lives in
      its own script separate from the fast Windows path (release-win.ps1).
    - This is a thin wrapper over release.ps1 -SkipWindows -SkipBlob -SkipGitHub, so
      auth/prerequisites and behavior are identical to the full pipeline (minus Windows).
    - When shipping the SAME version on both platforms, run release-win.ps1 first (it
      creates the GitHub release and tag); this Android script skips GitHub so there is no
      tag conflict. Use release.ps1 to do both at once instead.

  This script contains no em-dash (U+2014) characters, per project rule.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$Version,
  [int]$AndroidVersionCode = 0,
  [switch]$SkipPush,
  [switch]$PushStores,
  [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$release = Join-Path $PSScriptRoot "release.ps1"
if (-not (Test-Path $release)) { Write-Host "  [fail] release.ps1 not found next to this wrapper" -ForegroundColor Red; exit 1 }

# Force the Windows-artifact steps off; keep version bump, lint, AAB build, git push.
$forward = @{ Version = $Version; SkipWindows = $true; SkipBlob = $true; SkipGitHub = $true }
if ($PSBoundParameters.ContainsKey('AndroidVersionCode')) { $forward['AndroidVersionCode'] = $AndroidVersionCode }
foreach ($p in 'SkipPush','PushStores','DryRun') {
  if ($PSBoundParameters.ContainsKey($p)) { $forward[$p] = $PSBoundParameters[$p] }
}

& $release @forward
exit $LASTEXITCODE
