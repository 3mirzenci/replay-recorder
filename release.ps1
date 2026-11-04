# Publishes a new version: bumps the version, commits, tags and pushes. GitHub then builds the
# installer and creates the release (.github/workflows/release.yml), which the in-app update
# button and the download link point at. Only the tests run on this PC.
#   powershell -ExecutionPolicy Bypass -File release.ps1 -Notes "Fixed X, added Y"            # 1.0.3 -> 1.0.4
#   powershell -ExecutionPolicy Bypass -File release.ps1 -Version 1.1.0 -Notes "Big update"
#   ... -Notes "- Fixed X" -NotesTr "- (the same in Turkish)"   # Turkish users see the Turkish part
# The notes become the release text and the "What's new" window. Needs: git (and gh to watch the build).
param(
    [string]$Version = "",
    [Parameter(Mandatory = $true)][string]$Notes,
    [string]$NotesTr = ""
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$repo = "3mirzenci/replay-recorder"

$props = "Directory.Build.props"
$current = [version]([xml](Get-Content $props)).Project.PropertyGroup.Version
if (-not $Version) { $Version = "{0}.{1}.{2}" -f $current.Major, $current.Minor, ($current.Build + 1) }
if ([version]$Version -le $current -and (git tag --list "v$current")) { throw "Version $Version is not newer than the released $current" }

# Tests run here first so a failure stops before anything is tagged or pushed.
dotnet test tests/ReplayRecorder.Tests -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Tests failed; nothing was released" }

# Bump the version (the updater compares the running app's version with the release tag).
$text = [IO.File]::ReadAllText((Resolve-Path $props))
$text = $text -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>"
[IO.File]::WriteAllText((Resolve-Path $props), $text)

# Turkish part after a hidden marker (Lang.NotesTrMarker): the app shows the part in its language.
if ($NotesTr) { $Notes = "$Notes`n`n<!-- tr -->`n$NotesTr" }

# The tag message carries the notes to the GitHub build (a file keeps multi-line notes intact).
$notesFile = [IO.Path]::GetTempFileName()
[IO.File]::WriteAllText($notesFile, $Notes)
git add -A
git commit -m "Release v$Version" -m $Notes
git tag -a "v$Version" -F $notesFile
Remove-Item $notesFile
git push origin HEAD --follow-tags
if ($LASTEXITCODE -ne 0) { throw "git push failed" }

Write-Host "Pushed v$Version. GitHub is building it (about 5 minutes): https://github.com/$repo/actions"
Write-Host "Then it's live at https://github.com/$repo/releases/latest/download/ReplayRecorder-Setup.exe"
