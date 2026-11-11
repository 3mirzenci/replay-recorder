# One benchmark match: logs the game's FPS (PresentMon, asks once for admin) and Replay Recorder's resource use
# (measure.ps1) for the same number of seconds, into one folder. Results: fps.csv, usage.csv, summary.txt.
#   powershell -ExecutionPolicy Bypass -File tools\bench.ps1 -Label off -Seconds 600
# PresentMon: put PresentMon.exe (github.com/GameTechDev/PresentMon) in tools\presentmon\.
param(
    [Parameter(Mandatory = $true)][string]$Label,
    [int]$Seconds = 600,
    [string]$Process = "cod.exe",
    [string]$Out = ""
)
if (-not $Out) { $Out = Join-Path (Split-Path $PSScriptRoot) "bench-results" } # $PSScriptRoot is empty in param defaults on PowerShell 5.1
$dir = Join-Path $Out ("{0:yyyyMMdd-HHmm}-{1}" -f (Get-Date), $Label)
New-Item -ItemType Directory -Force $dir | Out-Null
$pm = Join-Path $PSScriptRoot "presentmon\PresentMon.exe"
if (Test-Path $pm) {
    Start-Process $pm -Verb RunAs -WindowStyle Hidden -ArgumentList @(
        "--process_name", $Process, "--timed", $Seconds, "--terminate_after_timed",
        "--output_file", "`"$(Join-Path $dir 'fps.csv')`"", "--no_console_stats")
} else { "PresentMon not found at ${pm}: no FPS this run" | Set-Content (Join-Path $dir "summary.txt") }
& (Join-Path $PSScriptRoot "measure.ps1") -Seconds $Seconds -Csv (Join-Path $dir "usage.csv") *>> (Join-Path $dir "summary.txt")
"done" | Set-Content (Join-Path $dir "done.txt")
