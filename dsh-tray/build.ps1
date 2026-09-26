# Build DSH-Tray.exe (system tray companion). Output goes to build\ so a running exe stays unlocked.
param(
    [string]$Out = 'build\DSH-Tray.exe'
)
$ErrorActionPreference = 'Stop'
$app = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { Write-Host "MISSING: $csc"; exit 1 }
$outPath = Join-Path $app $Out
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outPath) | Out-Null
& $csc /nologo /target:winexe /out:"$outPath" /codepage:65001 `
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
  (Join-Path $app 'dsh-tray.cs')
if ($LASTEXITCODE -ne 0) { Write-Host 'BUILD FAILED'; exit 1 }
Write-Host ("Build OK: " + $outPath + "  (" + (Get-Item $outPath).Length + " bytes)")
