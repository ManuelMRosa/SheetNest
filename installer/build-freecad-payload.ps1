<#
  build-freecad-payload.ps1

  Assembles a self-contained, offline FreeCAD payload that ships INSIDE SheetNest so that 3D
  (STEP/IGES) import + sheet-metal unfold works with no separate FreeCAD install and no internet.

  The payload contains: FreeCAD (OCCT + Python + Qt, headless freecadcmd.exe), the SheetMetal
  workbench, the networkx dependency (V2 unfolder), and the unfold macro.

  RELEASE FLOW:
    1. dotnet publish the app  ->  <publishDir>
    2. .\installer\build-freecad-payload.ps1 -Dest "<publishDir>\freecad"
    3. wix build installer\SheetNest.wxs -d PublishDir=<publishDir> ...   (harvests <publishDir>\**)

  So the payload just needs to land in <publishDir>\freecad and the existing
  `<Files Include="$(var.PublishDir)\**" />` harvest ships it. No .wxs change required.
#>
param(
  # Source FreeCAD install to clone (the one bundled must match what the macro was tested on).
  [string]$Source        = "$env:LOCALAPPDATA\Programs\FreeCAD 1.1",
  # SheetMetal workbench to inject (defaults to the user's Addon-Manager copy).
  [string]$SheetMetalSrc = "$env:APPDATA\FreeCAD\Mod\SheetMetal",
  # The validated unfold macro from the repo.
  [string]$MacroSrc      = (Join-Path $PSScriptRoot "..\assets\freecad\unfold_macro.py"),
  # Output payload dir. Point this at <publishDir>\freecad for a release build.
  [string]$Dest          = (Join-Path $env:TEMP "sheetnest-freecad-payload\freecad"),
  # Drop workbenches not needed for unfold (~150 MB). Off by default for maximum reliability.
  [switch]$Trim
)
$ErrorActionPreference = "Stop"

if (-not (Test-Path $Source))        { throw "FreeCAD source not found: $Source" }
if (-not (Test-Path $SheetMetalSrc)) { throw "SheetMetal workbench not found: $SheetMetalSrc (install it via FreeCAD Addon Manager)" }
if (-not (Test-Path $MacroSrc))      { throw "Unfold macro not found: $MacroSrc" }

Write-Output "1/5 Copying FreeCAD  $Source -> $Dest"
if (Test-Path $Dest) { Remove-Item $Dest -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Dest | Out-Null
Copy-Item "$Source\*" $Dest -Recurse -Force

Write-Output "2/5 Injecting SheetMetal workbench"
$modDest = Join-Path $Dest "Mod\SheetMetal"
Copy-Item $SheetMetalSrc $modDest -Recurse -Force
Remove-Item (Join-Path $modDest ".git") -Recurse -Force -ErrorAction SilentlyContinue

Write-Output "3/5 Adding unfold macro to the workbench"
Copy-Item $MacroSrc $modDest -Force

Write-Output "4/5 Ensuring networkx (V2 unfolder dependency)"
$py = Join-Path $Dest "bin\python.exe"
try { & $py -c "import networkx" -ErrorAction Stop } catch {}
if (-not $?) {
  Write-Output "    networkx missing in payload python; installing..."
  & $py -m pip install --no-warn-script-location networkx
}

if ($Trim) {
  Write-Output "5/5 Trimming workbenches not used by unfold"
  $drop = "BIM","Fem","Idf","CAM","OpenSCAD","Assembly","Robot","Inspection","Surface"
  foreach ($m in $drop) {
    Remove-Item (Join-Path $Dest "Mod\$m") -Recurse -Force -ErrorAction SilentlyContinue
  }
} else {
  Write-Output "5/5 Trim skipped (pass -Trim to drop ~150 MB of unused workbenches)"
}

$mb = [math]::Round(((Get-ChildItem $Dest -Recurse -File | Measure-Object Length -Sum).Sum/1MB),0)
Write-Output ""
Write-Output "Payload ready: $Dest  ($mb MB)"
Write-Output "freecadcmd:    $(Join-Path $Dest 'bin\freecadcmd.exe')"
