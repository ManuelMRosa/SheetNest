# Builds the full SheetNest 1.1.14 MSI (publish + FreeCAD payload + wix). Run it from the checkout it belongs to:
# it works in its own repo root, so a release worktree builds its own code.
$ErrorActionPreference = "Stop"
Set-Location (Split-Path -Parent $PSScriptRoot)

Write-Output "=== 1/3 dotnet publish (self-contained win-x64) ==="
Get-Process SheetNest -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish DeepNestSharp/DeepNestSharp.csproj -c Release -r win-x64 --self-contained -o publish-1.1.14 -v quiet --nologo
if (-not (Test-Path "publish-1.1.14\SheetNest.exe")) { throw "publish failed: no SheetNest.exe" }

Write-Output "=== 2/3 FreeCAD payload ==="
# build-freecad-payload.ps1 clones an installed FreeCAD 1.1, and this machine does not have one, so the
# payload is copied from the 1.1.13 publish in the main checkout: byte for byte what 1.1.13 back to 1.1.10
# shipped and what the unfold macro was tested against. With FreeCAD 1.1 installed, the script
# rebuilds it instead.
$payloadSource = "$env:LOCALAPPDATA\Programs\FreeCAD 1.1"
$previousPayload = "C:\Users\rosam\source\repos\DeepNestSharp\publish-1.1.13\freecad"
if (Test-Path "$payloadSource\bin\freecadcmd.exe") {
  & installer\build-freecad-payload.ps1 -Dest "publish-1.1.14\freecad"
}
else {
  Write-Output "FreeCAD 1.1 not installed; reusing the payload 1.1.13 shipped."
  if (-not (Test-Path "$previousPayload\bin\freecadcmd.exe")) { throw "no FreeCAD source and no 1.1.13 payload to reuse" }
  Copy-Item $previousPayload "publish-1.1.14\freecad" -Recurse -Force
}
if (-not (Test-Path "publish-1.1.14\freecad\bin\freecadcmd.exe")) { throw "payload failed: no freecadcmd.exe" }

Write-Output "=== 3/3 wix build ==="
# PublishDir MUST be absolute: wix resolves relative -d paths against the .wxs location
# (installer\), which silently harvests nothing and produces an empty MSI.
$repo = (Get-Location).Path
wix build installer\SheetNest.wxs `
  -d PublishDir="$repo\publish-1.1.14" `
  -d Version=1.1.14 `
  -d IconPath="$repo\DeepNestSharp\Branding\sheetnest.ico" `
  -ext WixToolset.UI.wixext `
  -arch x64 -o SheetNest-1.1.14-win-x64.msi
$mb = [math]::Round((Get-Item "SheetNest-1.1.14-win-x64.msi").Length / 1048576)
Write-Output "MSI listo: SheetNest-1.1.14-win-x64.msi ($mb MB)"
