$ErrorActionPreference = "Stop"
$root = "c:\Users\Sildom Landor\Documents\half-grip-multiplayer"
$game = "C:\Program Files (x86)\Steam\steamapps\common\Half Grip Demo"
$clients = "$root\clients"
$a = "$clients\ClientA"
$b = "$clients\ClientB"

Write-Host "Building plugin…"
dotnet build "$root\plugin\HalfGripMultiplayer\HalfGripMultiplayer.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$dll = "$root\build\HalfGripMultiplayer.dll"
$bep = "$root\third_party\BepInExPack"

function Install-Client($dest, $role, $name) {
  Write-Host "Preparing $dest ($role)…"
  New-Item -ItemType Directory -Force -Path $dest | Out-Null
  if (-not (Test-Path "$dest\HalfGrip.exe")) {
    Write-Host "  Copying game (first time, may take a few minutes)…"
    robocopy $game $dest /E /XD BepInEx /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
    # robocopy exit 0-7 = success-ish
  }
  # BepInEx
  Copy-Item -Force "$bep\winhttp.dll" $dest
  Copy-Item -Force "$bep\doorstop_config.ini" $dest
  if (Test-Path "$bep\.doorstop_version") { Copy-Item -Force "$bep\.doorstop_version" $dest }
  if (-not (Test-Path "$dest\BepInEx\core\BepInEx.dll")) {
    Copy-Item -Recurse -Force "$bep\BepInEx" "$dest\BepInEx"
  }
  New-Item -ItemType Directory -Force -Path "$dest\BepInEx\plugins\HalfGripMultiplayer" | Out-Null
  Copy-Item -Force $dll "$dest\BepInEx\plugins\HalfGripMultiplayer\HalfGripMultiplayer.dll"
  Set-Content -Encoding ASCII -Path "$dest\BepInEx\plugins\HalfGripMultiplayer\hgmp.role" -Value $role
  Set-Content -Encoding ASCII -Path "$dest\steam_appid.txt" -Value "4163360"
  # Console on
  $cfgDir = "$dest\BepInEx\config"
  New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
  if (-not (Test-Path "$cfgDir\BepInEx.cfg")) {
    @"
[Logging.Console]
Enabled = true
"@ | Set-Content "$cfgDir\BepInEx.cfg" -Encoding UTF8
  }
  Write-Host "  OK $name → role=$role"
}

Install-Client $a "host-lan" "ClientA"
Install-Client $b "join-lan" "ClientB"

# Also refresh Steam install plugin
$steamPlug = "$game\BepInEx\plugins\HalfGripMultiplayer"
if (Test-Path "$game\BepInEx\core\BepInEx.dll") {
  New-Item -ItemType Directory -Force -Path $steamPlug | Out-Null
  Copy-Item -Force $dll "$steamPlug\HalfGripMultiplayer.dll"
  # Steam install: manual Steam lobbies (no auto LAN)
  if (Test-Path "$steamPlug\hgmp.role") { Remove-Item "$steamPlug\hgmp.role" -Force }
  Write-Host "Steam install plugin refreshed (manual Steam/LAN via overlay)"
}

Write-Host ""
Write-Host "Dual clients ready:"
Write-Host "  A (LAN host): $a"
Write-Host "  B (LAN join): $b"
Write-Host "Launch: powershell -File $root\scripts\launch-dual.ps1"
