<#
  Publishes the TotalControl MCP server (desktop control) into _deploy/totalcontrol so the Mesh
  Windows build can bundle it under mcp/totalcontrol (see the CopyTotalControl target in
  Mesh.App.csproj). Run this whenever TotalControl changes. TotalControl lives in a separate repo;
  adjust $tcProject if its path differs on your machine.
#>
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot            # .../Mesh
$stage = Join-Path $repo "_deploy/totalcontrol"

$tcProject = "C:\Users\ifain\OneDrive - Microsoft\Power CAT\TotalControl\TotalControl.csproj"
if (-not (Test-Path -LiteralPath $tcProject)) {
    Write-Error "TotalControl project not found at: $tcProject"
}

# Stop any running instances so the exe is not locked, then publish framework-dependent.
Get-Process -Name TotalControl -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force }
Start-Sleep -Seconds 2
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

dotnet publish $tcProject -c Release -f net10.0-windows -o $stage
Write-Host "TotalControl published to $stage"
