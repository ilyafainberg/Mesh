<#
  publish-relay-release.ps1 - publish a version-matched Relay release and image.

  The public Relay mirror is updated asynchronously from the private monorepo. This
  script waits until the mirror contains the latest committed Relay/Shared source,
  creates (or reuses) vX.Y.Z on MeshRelayAI/Relay, and waits for the GHCR workflow.

  This script contains no em-dash (U+2014) characters, per project rule.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [ValidatePattern('^\d+\.\d+\.\d+$')]
  [string]$Version,
  [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
  [string]$RelayRepo = "MeshRelayAI/Relay",
  [string]$ImageWorkflow = "publish-image.yml",
  [int]$SyncTimeoutSeconds = 300,
  [int]$RunStartTimeoutSeconds = 120,
  [switch]$DryRun
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Say($message)  { Write-Host "`n=== $message ===" -ForegroundColor Cyan }
function Ok($message)   { Write-Host "  [ok] $message" -ForegroundColor Green }
function Note($message) { Write-Host "  $message" -ForegroundColor Gray }
function Warn($message) { Write-Host "  [warn] $message" -ForegroundColor Yellow }

function Invoke-Gh {
  param([string[]]$Arguments, [string]$What)

  & gh @Arguments
  if ($LASTEXITCODE -ne 0) { throw "$What failed (exit $LASTEXITCODE)." }
}

function Get-GhJson {
  param([string[]]$Arguments, [string]$What)

  $output = & gh @Arguments 2>&1
  if ($LASTEXITCODE -ne 0) { throw "$What failed:`n$($output -join "`n")" }
  $text = ($output -join "`n").Trim()
  if (-not $text) { return $null }
  return ($text | ConvertFrom-Json)
}

function Get-LatestRelaySync {
  $commits = Get-GhJson -Arguments @("api", "repos/$RelayRepo/commits?per_page=100") -What "Relay commit lookup"
  foreach ($commit in @($commits)) {
    $match = [regex]::Match([string]$commit.commit.message, '^Sync source from monorepo \(([0-9a-f]{7,40})\)')
    if ($match.Success) {
      return [pscustomobject]@{
        MonorepoSha = $match.Groups[1].Value
        RelaySha = [string]$commit.sha
      }
    }
  }
  return $null
}

function Test-SyncContainsSource {
  param([string]$SourceSha, [string]$SyncSha)

  $resolved = & git -C $RepoRoot rev-parse --verify "$SyncSha^{commit}" 2>$null
  if ($LASTEXITCODE -ne 0) { return $false }

  & git -C $RepoRoot merge-base --is-ancestor $SourceSha ([string]$resolved).Trim() *> $null
  if ($LASTEXITCODE -eq 0) { return $true }
  if ($LASTEXITCODE -eq 1) { return $false }
  throw "Could not compare monorepo commits $SourceSha and $SyncSha."
}

function Wait-ForRelayMirror {
  $sourceOutput = & git -C $RepoRoot log -1 --format=%H -- "src/Mesh.Relay" "src/Mesh.Shared" 2>$null
  if ($LASTEXITCODE -ne 0 -or -not $sourceOutput) {
    throw "Could not find the latest committed Relay/Shared source revision."
  }
  $sourceSha = ([string]$sourceOutput).Trim()
  $deadline = [DateTime]::UtcNow.AddSeconds($SyncTimeoutSeconds)

  do {
    $sync = Get-LatestRelaySync
    if ($sync -and (Test-SyncContainsSource -SourceSha $sourceSha -SyncSha $sync.MonorepoSha)) {
      Ok "Relay mirror contains source through monorepo $($sync.MonorepoSha)"
      return
    }
    if ([DateTime]::UtcNow -ge $deadline) { break }
    Note "waiting for the Relay mirror to catch up..."
    Start-Sleep -Seconds 10
  } while ($true)

  $seen = if ($sync) { $sync.MonorepoSha } else { "none" }
  throw "Relay mirror is stale (latest sync: $seen; required source: $sourceSha)."
}

function Wait-ForWorkflowRun {
  param(
    [string]$Event,
    [string]$HeadSha,
    [DateTime]$CreatedAfter,
    [switch]$AllowTimeout
  )

  $deadline = [DateTime]::UtcNow.AddSeconds($RunStartTimeoutSeconds)
  do {
    $runs = Get-GhJson -Arguments @(
      "run", "list", "--repo", $RelayRepo, "--workflow", $ImageWorkflow,
      "--limit", "30", "--json", "databaseId,event,headSha,status,conclusion,createdAt,url,displayTitle"
    ) -What "Relay workflow lookup"

    $run = @($runs) |
      Where-Object {
        ([string]$_.event -eq $Event) -and
        ([string]$_.headSha -eq $HeadSha) -and
        ([DateTimeOffset]$_.createdAt).UtcDateTime -ge $CreatedAfter
      } |
      Sort-Object { [DateTimeOffset]$_.createdAt } -Descending |
      Select-Object -First 1

    if ($run) { return $run }
    if ([DateTime]::UtcNow -ge $deadline) { break }
    Start-Sleep -Seconds 5
  } while ($true)

  if ($AllowTimeout) { return $null }
  throw "Timed out waiting for $ImageWorkflow to start for $HeadSha."
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "Required tool 'gh' is not on PATH." }
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw "Required tool 'git' is not on PATH." }
if (-not (Test-Path (Join-Path $RepoRoot ".git"))) { throw "RepoRoot is not a git worktree: $RepoRoot" }

& gh auth status *> $null
if ($LASTEXITCODE -ne 0) { throw "gh is not authenticated." }

Say "Relay: release v$Version and publish GHCR image"
Wait-ForRelayMirror

$tag = "v$Version"
$title = "Mesh Relay v$Version"
$releaseView = & gh release view $tag --repo $RelayRepo 2>&1
$releaseExists = $LASTEXITCODE -eq 0
if (-not $releaseExists -and (($releaseView -join "`n") -notmatch '(?i)(release not found|HTTP 404|not found)')) {
  throw "Could not inspect Relay release ${tag}:`n$($releaseView -join "`n")"
}

if ($releaseExists) {
  $tagCommit = Get-GhJson -Arguments @("api", "repos/$RelayRepo/commits/$tag") -What "Relay tag lookup"
  $targetSha = [string]$tagCommit.sha
} else {
  $relayHead = Get-GhJson -Arguments @("api", "repos/$RelayRepo/commits/main") -What "Relay main lookup"
  $targetSha = [string]$relayHead.sha
}

if ($DryRun) {
  $action = if ($releaseExists) { "refresh" } else { "create" }
  Warn "DryRun: would $action $RelayRepo release $tag at $targetSha and publish GHCR tags."
  return
}

$run = $null
if ($releaseExists) {
  Invoke-Gh -Arguments @("release", "edit", $tag, "--repo", $RelayRepo, "--title", $title) -What "Relay release update"
  $dispatchStarted = [DateTime]::UtcNow.AddSeconds(-5)
  Invoke-Gh -Arguments @(
    "workflow", "run", $ImageWorkflow, "--repo", $RelayRepo, "--ref", $tag,
    "-f", "version=$Version"
  ) -What "Relay image workflow dispatch"
  $run = Wait-ForWorkflowRun -Event "workflow_dispatch" -HeadSha $targetSha -CreatedAfter $dispatchStarted
} else {
  $releaseStarted = [DateTime]::UtcNow.AddSeconds(-5)
  Invoke-Gh -Arguments @(
    "release", "create", $tag, "--repo", $RelayRepo, "--target", $targetSha,
    "--title", $title, "--generate-notes"
  ) -What "Relay release creation"
  $run = Wait-ForWorkflowRun -Event "release" -HeadSha $targetSha -CreatedAfter $releaseStarted -AllowTimeout

  if (-not $run) {
    Warn "release event did not start the image workflow; dispatching it explicitly."
    $dispatchStarted = [DateTime]::UtcNow.AddSeconds(-5)
    Invoke-Gh -Arguments @(
      "workflow", "run", $ImageWorkflow, "--repo", $RelayRepo, "--ref", $tag,
      "-f", "version=$Version"
    ) -What "Relay image workflow dispatch"
    $run = Wait-ForWorkflowRun -Event "workflow_dispatch" -HeadSha $targetSha -CreatedAfter $dispatchStarted
  }
}

Invoke-Gh -Arguments @(
  "run", "watch", ([string]$run.databaseId), "--repo", $RelayRepo, "--exit-status"
) -What "Relay image workflow"

$image = "ghcr.io/$($RelayRepo.ToLowerInvariant())"
Ok "released: https://github.com/$RelayRepo/releases/tag/$tag"
Ok "published: ${image}:latest and ${image}:$Version"
