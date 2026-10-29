# Builds the full SheetNest 1.1.12 MSI (publish + FreeCAD payload + wix). Run from the repo root.
$ErrorActionPreference = "Stop"
Set-Location "C:\Users\rosam\source\repos\DeepNestSharp"

Write-Output "=== 1/3 dotnet publish (self-contained win-x64) ==="
Get-Process SheetNest -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish DeepNestSharp/DeepNestSharp.csproj -c Release -r win-x64 --self-contained -o publish-1.1.12 -v quiet --nologo
if (-not (Test-Path "publish-1.1.12\SheetNest.exe")) { throw "publish failed: no SheetNest.exe" }

Write-Output "=== 2/3 FreeCAD payload ==="
# build-freecad-payload.ps1 clones an installed FreeCAD 1.1, and this machine does not have one, so the
# payload is copied from the 1.1.11 publish: byte for byte what 1.1.11 shipped (itself the 1.1.10
# payload) and what the unfold macro was tested against. With FreeCAD 1.1 installed, the script
# rebuilds it instead.
$payloadSource = "$env:LOCALAPPDATA\Programs\FreeCAD 1.1"
if (Test-Path "$payloadSource\bin\freecadcmd.exe") {
  & installer\build-freecad-payload.ps1 -Dest "publish-1.1.12\freecad"
}
else {
  Write-Output "FreeCAD 1.1 not installed; reusing the payload 1.1.11 shipped."
  if (-not (Test-Path "publish-1.1.11\freecad\bin\freecadcmd.exe")) { throw "no FreeCAD source and no 1.1.11 payload to reuse" }
  Copy-Item "publish-1.1.11\freecad" "publish-1.1.12\freecad" -Recurse -Force
}
if (-not (Test-Path "publish-1.1.12\freecad\bin\freecadcmd.exe")) { throw "payload failed: no freecadcmd.exe" }

Write-Output "=== 3/3 wix build ==="
# PublishDir MUST be absolute: wix resolves relative -d paths against the .wxs location
# (installer\), which silently harvests nothing and produces an empty MSI.
$repo = (Get-Location).Path
wix build installer\SheetNest.wxs `
  -d PublishDir="$repo\publish-1.1.12" `
  -d Version=1.1.12 `
  -d IconPath="$repo\DeepNestSharp\Branding\sheetnest.ico" `
  -ext WixToolset.UI.wixext `
  -arch x64 -o SheetNest-1.1.12-win-x64.msi
$mb = [math]::Round((Get-Item "SheetNest-1.1.12-win-x64.msi").Length / 1048576)
Write-Output "MSI listo: SheetNest-1.1.12-win-x64.msi ($mb MB)"
