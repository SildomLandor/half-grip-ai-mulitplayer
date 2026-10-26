$ErrorActionPreference = "Stop"
$root = "c:\Users\Sildom Landor\Documents\half-grip-multiplayer"
$a = "$root\clients\ClientA"
$b = "$root\clients\ClientB"

if (-not (Test-Path "$a\HalfGrip.exe")) {
  Write-Host "ClientA missing - running setup..."
  & "$root\scripts\setup-dual-clients.ps1"
}
if (-not (Test-Path "$b\HalfGrip.exe")) {
  Write-Host "ClientB missing - running setup..."
  & "$root\scripts\setup-dual-clients.ps1"
}

Get-Process HalfGrip -ErrorAction SilentlyContinue | ForEach-Object {
  try {
    $path = $_.Path
    if ($path -and ($path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase))) {
      Write-Host "Stopping old test PID $($_.Id)"
      Stop-Process -Id $_.Id -Force
    }
  } catch {}
}

Start-Sleep -Seconds 1

$env:HGMP_NAME = "HostA"
$env:HGMP_ROLE = "host-lan"
$env:HGMP_LAN_PORT = "27777"
Write-Host "Starting ClientA (LAN host)..."
Start-Process -FilePath "$a\HalfGrip.exe" -WorkingDirectory $a -ArgumentList "-screen-width","1280","-screen-height","720","-screen-fullscreen","0"

Start-Sleep -Seconds 6

$env:HGMP_NAME = "GuestB"
$env:HGMP_ROLE = "join-lan"
$env:HGMP_LAN_HOST = "127.0.0.1"
$env:HGMP_LAN_PORT = "27777"
Write-Host "Starting ClientB (LAN join)..."
Start-Process -FilePath "$b\HalfGrip.exe" -WorkingDirectory $b -ArgumentList "-screen-width","1280","-screen-height","720","-screen-fullscreen","0"

Write-Host ""
Write-Host "Both clients launching."
Write-Host "Wait for menus - overlay should show LAN host / LAN join."
Write-Host "Start the same race on BOTH clients for shared countdown + PvP."
Write-Host "Logs: clients\ClientA\BepInEx\LogOutput.log and ClientB\..."
