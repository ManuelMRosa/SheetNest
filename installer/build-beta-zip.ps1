<#
  build-beta-zip.ps1

  Builds a portable SheetNest BETA: the self-contained publish plus the offline FreeCAD payload,
  zipped. The tester unzips it and runs it — nothing is installed, so whatever stable they already
  have stays exactly where it is and they never have to uninstall anything to try a beta.

  Betas are deliberately NOT shipped as MSIs. See installer\RELEASING.md for the reason.

  Run from anywhere in the repo:
    .\installer\build-beta-zip.ps1 -Version 1.2.0-beta1
#>
param(
  # The full beta version, suffix included: it names the folder inside the zip and the zip itself.
  # The pattern also refuses a plain X.Y.Z — a stable ships as an MSI, through build-msi-<ver>.ps1.
  [Parameter(Mandatory = $true)]
  [ValidatePattern('^\d+\.\d+\.\d+-(alpha|beta|rc)\d+$')]
  [string]$Version
)
$ErrorActionPreference = "Stop"

# Repo root, derived instead of hardcoded, so the script travels with the repo.
Set-Location (Split-Path $PSScriptRoot -Parent)

$stage = "SheetNest-$Version-win-x64"
$zip = "$stage.zip"

Write-Output "=== 1/3 dotnet publish (self-contained win-x64) ==="
Get-Process SheetNest -ErrorAction SilentlyContinue | Stop-Process -Force
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
dotnet publish DeepNestSharp/DeepNestSharp.csproj -c Release -r win-x64 --self-contained -o $stage -v quiet --nologo
if (-not (Test-Path "$stage\SheetNest.exe")) { throw "publish failed: no SheetNest.exe" }

Write-Output "=== 2/3 FreeCAD payload ==="
& installer\build-freecad-payload.ps1 -Dest "$stage\freecad"
if (-not (Test-Path "$stage\freecad\bin\freecadcmd.exe")) { throw "payload failed: no freecadcmd.exe" }

Write-Output "=== 3/3 zip ==="
# NOT Compress-Archive: it gives up past 2 GB and the FreeCAD payload alone is 2.1 GB. ZipFile does
# ZIP64, and includeBaseDirectory puts the lot under one folder instead of unpacking 32,000 loose
# files into whatever directory the tester happened to be in.
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path $zip) { Remove-Item $zip -Force }
[IO.Compression.ZipFile]::CreateFromDirectory(
  (Resolve-Path $stage).Path,
  (Join-Path (Get-Location).Path $zip),
  [IO.Compression.CompressionLevel]::Optimal,
  $true)

$mb = [math]::Round((Get-Item $zip).Length / 1MB)
Write-Output "Beta lista: $zip ($mb MB)"
