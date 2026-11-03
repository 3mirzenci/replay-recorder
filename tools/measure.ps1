# Samples Replay Recorder's resource use while you play. Run it in a terminal, alt-tab into the game,
# play, then come back; it prints a summary when done.
#   powershell -ExecutionPolicy Bypass -File tools\measure.ps1 -Seconds 300
# Compare with SteelSeries GG:  -Names "SteelSeriesGG","SteelSeriesEngine","SteelSeriesSonar"
param(
    [int]$Seconds = 120,
    [string[]]$Names = @("ReplayRecorder.Recorder", "ffmpeg")
)

$cores = [Environment]::ProcessorCount
$samples = @()
$prevCpu = @{}
$t0 = Get-Date
Write-Host "Measuring $($Names -join ', ') for $Seconds s ..."

for ($i = 0; $i -lt $Seconds; $i++) {
    $procs = Get-Process -Name $Names -ErrorAction SilentlyContinue
    $cpuSum = 0.0; $privSum = 0.0
    foreach ($p in $procs) {
        $key = $p.Id
        if ($prevCpu.ContainsKey($key)) { $cpuSum += ($p.TotalProcessorTime.TotalSeconds - $prevCpu[$key]) * 100 / $cores }
        $prevCpu[$key] = $p.TotalProcessorTime.TotalSeconds
        $pws = (Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess=$($p.Id)" -ErrorAction SilentlyContinue).WorkingSetPrivate
        $privSum += $pws / 1MB
    }
    $gpu = (& nvidia-smi --query-gpu=utilization.gpu,utilization.encoder --format=csv,noheader,nounits 2>$null) -split ',\s*'
    if ($i -gt 0) {
        $samples += [pscustomobject]@{ Cpu = $cpuSum; RamMB = $privSum; Gpu = [double]$gpu[0]; Enc = [double]$gpu[1] }
    }
    Start-Sleep -Milliseconds (1000 - ((Get-Date) - $t0).Milliseconds % 1000)
}

function Stat($prop) {
    $v = $samples | ForEach-Object { $_.$prop } | Sort-Object
    $avg = ($v | Measure-Object -Average).Average
    $p95 = $v[[math]::Min($v.Count - 1, [int]($v.Count * 0.95))]
    "{0,-26} avg {1,7:N1}   p95 {2,7:N1}   max {3,7:N1}" -f $prop, $avg, $p95, $v[-1]
}
Write-Host ""
Write-Host "Processes: $($Names -join ', ')  ($($samples.Count) samples)"
Stat Cpu    | ForEach-Object { $_ -replace '^Cpu\s+', 'CPU % (of whole CPU)      ' }
Stat RamMB  | ForEach-Object { $_ -replace '^RamMB\s+', 'RAM MB (Task Manager)     ' }
Stat Gpu    | ForEach-Object { $_ -replace '^Gpu\s+', 'GPU 3D % (whole GPU)      ' }
Stat Enc    | ForEach-Object { $_ -replace '^Enc\s+', 'NVENC % (encoder block)   ' }
