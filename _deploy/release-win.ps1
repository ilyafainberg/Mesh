<#
  release-win.ps1  -  Windows-only Mesh release (fast path)

  Runs the full release pipeline but SKIPS the Android AAB build, which is the slow
  part (AOT compile of every assembly takes the bulk of the wall-clock time). Use this
  for the common case where only client/relay code changed and you want a quick Windows
  drop: version bump -> em-dash lint -> Windows publish + sign + installer ->
  git commit+push -> Azure Blob upload -> GitHub release.

  USAGE
    ./_deploy/release-win.ps1 -Version 1.5.8
    ./_deploy/release-win.ps1 -Version 1.5.8 -DryRun
    ./_deploy/release-win.ps1 -Version 1.5.8 -PushStores      # also submit to Microsoft Store

  This is a thin wrapper over release.ps1 -SkipAndroid, so all auth/prerequisites and
  behavior are identical to the full pipeline (minus Android). Run release-android.ps1
  separately when you need an Android build, or use release.ps1 to do both at once.

  This script contains no em-dash (U+2014) characters, per project rule.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$Version,
  [string]$NotesFile = "",
  [switch]$SkipBlob,
  [switch]$SkipGitHub,
  [switch]$SkipPush,
  [switch]$PushStores,
  [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$release = Join-Path $PSScriptRoot "release.ps1"
if (-not (Test-Path $release)) { Write-Host "  [fail] release.ps1 not found next to this wrapper" -ForegroundColor Red; exit 1 }

# Forward every bound parameter, then force -SkipAndroid so the slow AAB build is skipped.
$forward = @{ Version = $Version; SkipAndroid = $true }
foreach ($p in 'NotesFile','SkipBlob','SkipGitHub','SkipPush','PushStores','DryRun') {
  if ($PSBoundParameters.ContainsKey($p)) { $forward[$p] = $PSBoundParameters[$p] }
}

& $release @forward
exit $LASTEXITCODE
