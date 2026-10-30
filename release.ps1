# Builds LocalSend Drop at the given version and publishes it as a GitHub release.
# Installed copies pick it up within 6 hours (or via "Check for updates").
#   ./release.ps1 2.0.1 "Fixed the thing"
# Release assets:
#   LocalSendDrop.msix  the app package (installed copies update from this)
#   LocalSendDrop.exe   plain exe: a 1.x install updates to it, then it moves into the package
#   LocalSendDrop.cer   certificate install.ps1 trusts
#   install.ps1         one-line installer for new PCs
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Notes = ""
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
Set-Location $root
# Publish as the org admin account, whichever gh account is active.
$env:GH_TOKEN = gh auth token --user rizwan-mediapro

$csproj = "$root\LocalSendDrop.csproj"
(Get-Content $csproj -Raw) -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>" | Set-Content $csproj -NoNewline

dotnet publish $csproj -c Release -o "$root\dist" -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
& "$root\shell\build.ps1" -Version "$Version.0" | Out-Null
Set-Location $root

# git reports progress on stderr, which Windows PowerShell treats as an error; check exit codes instead.
$ErrorActionPreference = "Continue"
git add $csproj shell/bin/LocalSendDrop.cer
git commit -q -m "Release $Version" --allow-empty
git tag "v$Version"
git push -q origin HEAD "v$Version" 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "git push failed" }
$ErrorActionPreference = "Stop"

# Notes go through a file: Windows PowerShell mangles quotes inside native arguments.
$notesFile = New-TemporaryFile
Set-Content $notesFile $Notes -Encoding utf8
gh release create "v$Version" "$root\dist\LocalSendDrop.msix" "$root\dist\LocalSendDrop.exe" "$root\dist\LocalSendDrop.cer" "$root\install.ps1" `
    --repo Mediapro-Technology/localsend-drop --title "LocalSend Drop $Version" --notes-file $notesFile
Remove-Item $notesFile
