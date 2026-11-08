# Logs Replay Recorder's and SteelSeries GG's resource use every second while you play, then prints a
# summary of the seconds a game was running. Start it, play, and wait for it to finish (or press Ctrl+C
# and run it with -Summarize to summarize what was logged so far).
#   powershell -ExecutionPolicy Bypass -File tools\measure.ps1 -Seconds 600
#   powershell -ExecutionPolicy Bypass -File tools\measure.ps1 -Summarize
param(
    [int]$Seconds = 600,
    [string]$Csv = (Join-Path $env:TEMP "replayrecorder-measure.csv"),
    [string]$GamePattern = '^(cs2|cod|VALORANT-Win64-Shipping|League of Legends|FortniteClient-Win64-Shipping|r5apex)',
    [switch]$Summarize
)
[Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::InvariantCulture
$groups = [ordered]@{
    "Replay Recorder (recorder + ffmpeg)" = @("ReplayRecorder.Recorder", "ffmpeg", "ffmpeg-capture")
    "Replay Recorder library"             = @("ReplayRecorder")
    "SteelSeries Moments (capture)"       = @("SteelSeriesCaptureSvc", "SteelSeriesMoments")
    "SteelSeries GG (app)"                = @("SteelSeriesGG", "SteelSeriesEngine", "SteelSeriesGGEZ", "SteelSeriesPrism")
}

if (-not $Summarize) {
    $cores = [Environment]::ProcessorCount
    $prev = @{}
    "time,group,cpu,ramMB,gpu3d,enc" | Set-Content $Csv
    Write-Host "Logging for $Seconds s to $Csv. Play now."
    for ($i = 0; $i -lt $Seconds; $i++) {
        $tick = Get-Date
        # Per-process GPU use: the "GPU Engine" counters are named pid_<id>_..._engtype_<engine>.
        $engines = Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine -ErrorAction SilentlyContinue
        $game = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $GamePattern } | Select-Object -First 1
        $all = $groups + [ordered]@{ Game = @(if ($game) { $game.Name } else { "__none__" }) }
        $rows = foreach ($g in $all.Keys) {
            $cpu = 0.0; $ram = 0.0; $g3 = 0.0; $enc = 0.0
            foreach ($p in (Get-Process -Name $all[$g] -ErrorAction SilentlyContinue)) {
                $t = $p.TotalProcessorTime.TotalSeconds
                if ($prev.ContainsKey($p.Id)) { $cpu += ($t - $prev[$p.Id]) * 100 / $cores }
                $prev[$p.Id] = $t
                $ram += $p.PrivateMemorySize64 / 1MB
                foreach ($e in $engines | Where-Object { $_.Name -like "pid_$($p.Id)_*" }) {
                    if ($e.Name -like "*engtype_3D") { $g3 += $e.UtilizationPercentage }
                    elseif ($e.Name -like "*engtype_VideoEncode*") { $enc += $e.UtilizationPercentage }
                }
            }
            "{0:HH:mm:ss},{1},{2:0.00},{3:0},{4},{5}" -f $tick, $g, $cpu, $ram, $g3, $enc
        }
        Add-Content $Csv $rows
        $wait = 1000 - ((Get-Date) - $tick).TotalMilliseconds
        if ($wait -gt 0) { Start-Sleep -Milliseconds $wait }
    }
}

# Summary over the seconds a game was running (its CPU above 1 %).
$rows = Import-Csv $Csv
$inGame = @{}
$rows | Where-Object { $_.group -eq 'Game' -and [double]$_.cpu -gt 1 } | ForEach-Object { $inGame[$_.time] = 1 }
Write-Host ""
Write-Host "Seconds in game: $($inGame.Count)   (avg / 95th percentile)"
foreach ($g in @($groups.Keys) + "Game") {
    $s = $rows | Where-Object { $_.group -eq $g -and $inGame[$_.time] -and [double]$_.ramMB -gt 0 }
    if (-not $s) { continue }
    $stat = { param($p) $v = $s | ForEach-Object { [double]$_.$p } | Sort-Object; "{0,6:N1} / {1,6:N1}" -f ($v | Measure-Object -Average).Average, $v[[math]::Min($v.Count - 1, [int]($v.Count * 0.95))] }
    "{0,-37} CPU % {1}   RAM MB {2}   GPU % {3}   encoder % {4}" -f $g, (& $stat cpu), (& $stat ramMB), (& $stat gpu3d), (& $stat enc)
}
