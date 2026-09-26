# dsh-desktop hot replacement watcher (run detached, hidden window).
# Waits until no dsh-desktop process is running, swaps in dsh-desktop-new.exe,
# then ensures the system tray guard (DSH-Tray.exe) is running.
$app = $PSScriptRoot
$root = if ($env:DSH_ROOT) { $env:DSH_ROOT } else { Join-Path $env:USERPROFILE 'DeepSeek_harness' }
$tray = if ($env:DSH_TRAY_EXE) { $env:DSH_TRAY_EXE } else { Join-Path $root 'dsh-tray\DSH-Tray.exe' }
$new = Join-Path $app 'dsh-desktop-new.exe'
$log = Join-Path $app 'apply-new.log'
function L($m) { Add-Content -Path $log -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '  ' + $m) -Encoding UTF8 }

L 'watcher start'
$deadline = (Get-Date).AddHours(72)
while ((Get-Date) -lt $deadline) {
    if (-not (Get-Process -Name 'dsh-desktop' -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Seconds 3
}
if (Get-Process -Name 'dsh-desktop' -ErrorAction SilentlyContinue) {
    L 'timeout: dsh-desktop still running after 72h, abort replace'
    exit 2
}
Start-Sleep -Seconds 2
if (-not (Test-Path $new)) {
    L 'dsh-desktop-new.exe missing, abort'
    exit 3
}
try {
    Copy-Item -Path $new -Destination (Join-Path $app 'dsh-desktop.exe') -Force
    L 'exe replaced: dsh-desktop-new.exe -> dsh-desktop.exe'
}
catch {
    L ('replace failed: ' + $_.Exception.Message)
    exit 4
}
if (-not (Get-Process -Name 'DSH-Tray' -ErrorAction SilentlyContinue)) {
    try {
        Start-Process -FilePath $tray
        L 'tray started (was not running)'
    }
    catch {
        L ('tray start failed: ' + $_.Exception.Message)
    }
}
else {
    L 'tray already running'
}
Remove-Item -Path $new -Force -ErrorAction SilentlyContinue
L 'done'
