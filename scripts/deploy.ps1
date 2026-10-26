$ErrorActionPreference = "Stop"
$root = "c:\Users\Sildom Landor\Documents\half-grip-multiplayer"
$game = "C:\Program Files (x86)\Steam\steamapps\common\Half Grip Demo"
$bepSrc = "$root\third_party\BepInExPack"
$pluginDll = "$root\build\HalfGripMultiplayer.dll"

Write-Host "Building…"
dotnet build "$root\plugin\HalfGripMultiplayer\HalfGripMultiplayer.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "build failed" }

if (-not (Test-Path "$game\BepInEx\core\BepInEx.dll")) {
  Write-Host "Installing BepInEx…"
  Copy-Item -Force "$bepSrc\winhttp.dll" $game
  Copy-Item -Force "$bepSrc\doorstop_config.ini" $game
  if (Test-Path "$bepSrc\.doorstop_version") { Copy-Item -Force "$bepSrc\.doorstop_version" $game }
  Copy-Item -Recurse -Force "$bepSrc\BepInEx" "$game\BepInEx"
}

New-Item -ItemType Directory -Force -Path "$game\BepInEx\plugins\HalfGripMultiplayer" | Out-Null
Copy-Item -Force $pluginDll "$game\BepInEx\plugins\HalfGripMultiplayer\HalfGripMultiplayer.dll"
# Steam copy should not auto LAN
Remove-Item -Force -ErrorAction SilentlyContinue "$game\BepInEx\plugins\HalfGripMultiplayer\hgmp.role"
Write-Host "Deployed to Steam install."
