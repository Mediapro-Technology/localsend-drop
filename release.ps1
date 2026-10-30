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

git add $csproj
git commit -m "Release $Version" --allow-empty
git tag "v$Version"
git push origin HEAD "v$Version"

gh release create "v$Version" dist/LocalSendDrop.exe --repo Mediapro-Technology/localsend-drop `
    --title "LocalSend Drop $Version" --notes $Notes
