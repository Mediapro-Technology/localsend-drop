# Builds dist/LocalSendDrop.exe at the given version and publishes it as a GitHub
# release. Installed copies pick it up within 6 hours (or via "Check for updates").
#   ./release.ps1 1.2.1 "Fixed the thing"
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Notes = ""
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
# Publish as the org admin account, whichever gh account is active.
$env:GH_TOKEN = gh auth token --user rizwan-mediapro

$csproj = "LocalSendDrop.csproj"
(Get-Content $csproj -Raw) -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>" | Set-Content $csproj -NoNewline

dotnet publish -c Release -o dist -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

# git reports progress on stderr, which Windows PowerShell treats as an error; check exit codes instead.
$ErrorActionPreference = "Continue"
git add $csproj
git commit -q -m "Release $Version" --allow-empty
git tag "v$Version"
git push -q origin HEAD "v$Version" 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "git push failed" }
$ErrorActionPreference = "Stop"

gh release create "v$Version" dist/LocalSendDrop.exe --repo Mediapro-Technology/localsend-drop `
    --title "LocalSend Drop $Version" --notes $Notes
