<#
  Stages the relay build context under _deploy/relay/ as a clean multi-project copy
  (Mesh.Relay + Mesh.Shared, source only, no bin/obj), so `az acr build _deploy/relay`
  produces the container image. The relay csproj already references ..\Mesh.Shared, which
  matches this layout.
#>
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot          # .../Mesh
$src  = Join-Path $repo "src"
$dst  = Join-Path $repo "_deploy/relay"

$exclude = @("bin", "obj")
function Copy-Project($name) {
  $from = Join-Path $src $name
  $to   = Join-Path $dst $name
  if (Test-Path $to) { Remove-Item $to -Recurse -Force }
  New-Item -ItemType Directory -Force -Path $to | Out-Null
  Get-ChildItem -Path $from -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($from.Length).TrimStart('\')
    if ($exclude | Where-Object { $rel -like "$_\*" -or $rel -like "*\$_\*" }) { return }
    $target = Join-Path $to $rel
    New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
    Copy-Item $_.FullName $target -Force
  }
}

Copy-Project "Mesh.Shared"
Copy-Project "Mesh.Relay"
Write-Host "Staged relay build context at $dst (Mesh.Relay + Mesh.Shared)."
