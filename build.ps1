# Builds the Replay Recorder installer.
#   powershell -ExecutionPolicy Bypass -File build.ps1                 # small installer; setup installs ffmpeg + mpv via winget
#   powershell -ExecutionPolicy Bypass -File build.ps1 -BundleTools    # offline installer with ffmpeg + mpv inside (~300 MB)
# Needs: .NET 10 SDK, Inno Setup 6 (winget install JRSoftware.InnoSetup); for -BundleTools also ffmpeg + mpv installed.
param(
    [string]$Version = "",   # default: the <Version> in Directory.Build.props
    [switch]$BundleTools
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
if (-not $Version) { $Version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }

$dist = Join-Path $PSScriptRoot "dist"
$app = Join-Path $dist "ReplayRecorder"
if (Test-Path $app) { Remove-Item -Recurse -Force $app }
New-Item -ItemType Directory -Force $app | Out-Null

# Both apps are published self-contained into one folder: no .NET install needed on the target PC,
# and the tray finds ReplayRecorder.exe next to itself.
foreach ($proj in "src\ReplayRecorder.Recorder", "src\ReplayRecorder.Library") {
    Write-Host "Publishing $proj ..."
    dotnet publish $proj -c Release -r win-x64 --self-contained true -o $app `
        -p:Version=$Version -p:PublishReadyToRun=true -p:DebugType=none -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $proj" }
}
Copy-Item README.md $app

# Our capture ffmpeg (ffmpeg/build.sh), if it was built: the recorder uses it for WGC + NVENC.
$capture = Join-Path $PSScriptRoot "ffmpeg\.work\out"
if (Test-Path "$capture\ffmpeg-capture.exe") {
    New-Item -ItemType Directory -Force "$app\capture" | Out-Null
    Copy-Item "$capture\*" "$app\capture"
} elseif ($env:GITHUB_ACTIONS) {
    throw "ffmpeg\.work\out\ffmpeg-capture.exe not found: a release must include it"
} else {
    Write-Warning "ffmpeg\.work\out\ffmpeg-capture.exe not found (run: sh ffmpeg/build.sh); recording will use the normal ffmpeg"
}

if ($BundleTools) {
    Write-Host "Bundling ffmpeg and mpv ..."
    $ffmpeg = (Get-Item (Get-Command ffmpeg -ErrorAction Stop).Source)
    $ffmpegExe = if ($ffmpeg.Target) { $ffmpeg.Target } else { $ffmpeg.FullName }
    $ffBin = Split-Path $ffmpegExe
    New-Item -ItemType Directory -Force "$app\ffmpeg" | Out-Null
    Copy-Item "$ffBin\ffmpeg.exe", "$ffBin\ffprobe.exe" "$app\ffmpeg"
    $lic = Join-Path (Split-Path $ffBin) "LICENSE"
    if (Test-Path $lic) { Copy-Item $lic "$app\ffmpeg\LICENSE.txt" }

    $mpvDir = @("$env:ProgramFiles\MPV Player", "$env:ProgramFiles\mpv") | Where-Object { Test-Path "$_\mpv.exe" } | Select-Object -First 1
    if (-not $mpvDir) { throw "mpv not found (winget install shinchiro.mpv)" }
    New-Item -ItemType Directory -Force "$app\mpv" | Out-Null
    Copy-Item "$mpvDir\mpv.exe" "$app\mpv"
    Get-ChildItem $mpvDir -Filter *.dll | Copy-Item -Destination "$app\mpv"
}

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup not found (winget install JRSoftware.InnoSetup)" }
$defs = @("/DAppVersion=$Version", "/DSourceDir=$app")
if ($BundleTools) { $defs += "/DBundled" }
& $iscc @defs /Q "installer\ReplayRecorder.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }

Get-ChildItem $dist -Filter "ReplayRecorder-Setup*.exe" | Sort-Object LastWriteTime | Select-Object -Last 1 |
    ForEach-Object { Write-Host ("Built {0} ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) }
