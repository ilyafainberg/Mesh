<#
.SYNOPSIS
  Manually mirror the relay + shared-library source from this private monorepo to the
  public open-source repos. This is the local fallback for the CI workflow
  (.github/workflows/sync-open-source.yml); it does the same thing on demand.

  src/Mesh.Relay  -> MeshRelayAI/Relay   (AGPL-3.0)
  src/Mesh.Shared -> MeshRelayAI/Relay AND MeshRelayAI/Shared (Apache-2.0)

  Only source is mirrored. Each public repo keeps its own LICENSE, README, Dockerfile,
  docker-compose, SELF-HOSTING, workflows, .slnx, .gitignore and NOTICE.

  Requires: git + gh authenticated with push rights to the MeshRelayAI repos.
#>
[CmdletBinding()]
param(
  [string]$MonoRoot = (Resolve-Path "$PSScriptRoot\.."),
  [switch]$Push
)

$ErrorActionPreference = "Stop"
$work = Join-Path $env:TEMP "mesh-sync-$(Get-Random)"
New-Item -ItemType Directory -Force -Path $work | Out-Null

# Run git without letting its progress-on-stderr trip $ErrorActionPreference. Throws
# only on a nonzero exit code.
function Invoke-Git {
  $old = $ErrorActionPreference
  $ErrorActionPreference = "Continue"
  try {
    $out = & git @args 2>&1
  } finally {
    $ErrorActionPreference = $old
  }
  if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed:`n$out" }
  return $out
}

function Sync-Mirror {
  param([string]$Repo, [switch]$IncludeRelay)

  Write-Host "=== $Repo ===" -ForegroundColor Cyan
  $dst = Join-Path $work ($Repo -replace '/', '_')
  Invoke-Git clone --depth 1 "https://github.com/$Repo.git" $dst | Out-Null

  # Mesh.Shared source (all mirrors). Protect the mirror's own LICENSE and any *.user.
  robocopy "$MonoRoot\src\Mesh.Shared" "$dst\src\Mesh.Shared" /MIR /XD bin obj /XF LICENSE *.user *.csproj.user | Out-Null

  if ($IncludeRelay) {
    robocopy "$MonoRoot\src\Mesh.Relay" "$dst\src\Mesh.Relay" /MIR /XD bin obj /XF *.user *.csproj.user | Out-Null
  }

  Push-Location $dst
  $changes = Invoke-Git status --porcelain
  if ([string]::IsNullOrWhiteSpace($changes)) {
    Write-Host "  No changes to sync." -ForegroundColor Green
  } else {
    Write-Host "  Changes detected:"; Invoke-Git status --short
    if ($Push) {
      $sha = (Invoke-Git -C $MonoRoot rev-parse --short HEAD)
      Invoke-Git add -A | Out-Null
      Invoke-Git -c user.name="Quonkel" -c user.email="ilyafainberg@users.noreply.github.com" commit -q -m "Sync source from monorepo ($sha)" | Out-Null
      Invoke-Git push origin HEAD:main | Out-Null
      Write-Host "  Pushed to $Repo." -ForegroundColor Green
    } else {
      Write-Host "  (dry run; re-run with -Push to publish)" -ForegroundColor Yellow
    }
  }
  Pop-Location
}

Sync-Mirror -Repo "MeshRelayAI/Relay"  -IncludeRelay
Sync-Mirror -Repo "MeshRelayAI/Shared"

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Done." -ForegroundColor Cyan
