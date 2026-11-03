# Publishes a new version: bumps the version, builds the installer, commits + tags, pushes, and
# creates the GitHub release that the in-app update button and the download link point at.
#   powershell -ExecutionPolicy Bypass -File release.ps1 -Notes "Fixed X, added Y"            # 1.0.3 -> 1.0.4
#   powershell -ExecutionPolicy Bypass -File release.ps1 -Version 1.1.0 -Notes "Big update"
# Needs: git, gh (logged in), plus everything build.ps1 needs.
param(
    [string]$Version = "",
    [Parameter(Mandatory = $true)][string]$Notes
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$repo = "3mirzenci/replay-recorder"

$props = "Directory.Build.props"
$current = [version]([xml](Get-Content $props)).Project.PropertyGroup.Version
if (-not $Version) { $Version = "{0}.{1}.{2}" -f $current.Major, $current.Minor, ($current.Build + 1) }
if ([version]$Version -le $current -and (git tag --list "v$current")) { throw "Version $Version is not newer than the released $current" }

# Bump the version (the updater compares the running app's version with the release tag).
$text = [IO.File]::ReadAllText((Resolve-Path $props))
$text = $text -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>"
[IO.File]::WriteAllText((Resolve-Path $props), $text)

& powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Version $Version
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$setup = "dist\ReplayRecorder-Setup.exe"
if (-not (Test-Path $setup)) { throw "$setup missing" }

git add -A
git commit -m "Release v$Version" -m $Notes
git tag -a "v$Version" -m "Replay Recorder $Version"
git push origin HEAD --follow-tags
if ($LASTEXITCODE -ne 0) { throw "git push failed" }

gh release create "v$Version" $setup --repo $repo --title "Replay Recorder $Version" --notes $Notes
if ($LASTEXITCODE -ne 0) { throw "gh release failed" }
Write-Host "Released $Version. Download link: https://github.com/$repo/releases/latest/download/ReplayRecorder-Setup.exe"
