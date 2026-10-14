param(
    [string]$Autoload = "",     # dev: save folder to load straight from the title screen, e.g. Rimdew_451598802
    [string]$Test = ""          # dev: "power" runs the power/turret test, "food" the food/temperature/repair test
)
Start-Transcript -Path "$PSScriptRoot\launcher.log" -Force | Out-Null
# Starts the RimDew stack: relay hub, RimWorld (hidden sim), then Stardew via SMAPI.
$root = $PSScriptRoot
$stardew = "C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"

# 1. Hub (skip if something is already listening on 7777)
if (-not (Get-NetTCPConnection -LocalPort 7777 -State Listen -ErrorAction SilentlyContinue)) {
    Start-Process pythonw -ArgumentList "`"$root\hub\hub.py`"" -WindowStyle Hidden
    Write-Host "Hub started."
} else {
    Write-Host "Hub already running."
}

# 2. RimWorld through Steam (the mod keeps it paused and running in the background)
if (-not (Get-Process RimWorldWin64 -ErrorAction SilentlyContinue)) {
    Start-Process "steam://rungameid/294100"
    Write-Host "Launching RimWorld... waiting for it to start."
    $t = 0
    while (-not (Get-Process RimWorldWin64 -ErrorAction SilentlyContinue) -and $t -lt 120) { Start-Sleep 2; $t += 2 }
    Start-Sleep 20   # let it reach the main menu
}
# RimWorld auto-loads your latest save (Mods\RimDewBridge\autoload.txt) and connects to the hub once the colony is up.
Write-Host "Waiting for RimWorld to load the colony and connect to the hub..."
$t = 0
while (-not (Get-NetTCPConnection -RemotePort 7777 -State Established -ErrorAction SilentlyContinue) -and $t -lt 240) {
    Start-Sleep 3; $t += 3
}
if ($t -ge 240) { Write-Host "RimWorld did not connect in 4 minutes; launching Stardew anyway (see launcher.log)." }

Add-Type @"
using System; using System.Runtime.InteropServices;
public class Win { [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
                   [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); }
"@

# 3. Hide RimWorld: it only simulates in the background, Stardew is the game you play.
$rw = Get-Process RimWorldWin64 -ErrorAction SilentlyContinue | Select-Object -First 1
if ($rw) { [Win]::ShowWindow($rw.MainWindowHandle, 6) | Out-Null }   # 6 = minimize

# 4. Stardew through SMAPI, brought to the front
$modDir = "$stardew\Mods\StardewBridge"
if ($Autoload) { Set-Content -Path "$modDir\dev_autoload.txt" -Value $Autoload -NoNewline }
if ($Test -eq "power") { New-Item -ItemType File -Force -Path "$modDir\dev_powertest.flag" | Out-Null }
if ($Test -eq "food") { New-Item -ItemType File -Force -Path "$modDir\dev_foodtest.flag" | Out-Null }
if (-not (Get-Process "StardewModdingAPI" -ErrorAction SilentlyContinue)) {
    Start-Process "$stardew\StardewModdingAPI.exe" -WorkingDirectory $stardew
    Write-Host "Launching Stardew via SMAPI."
    $sv = $null; $t = 0
    while (-not $sv -and $t -lt 90) {
        Start-Sleep 2; $t += 2
        $sv = Get-Process "Stardew Valley" -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    }
    if ($sv) { [Win]::SetForegroundWindow($sv.MainWindowHandle) | Out-Null }
}
Write-Host "Done. Play Stardew; RimWorld is running minimized in the background."

Stop-Transcript | Out-Null
