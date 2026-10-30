# Installs LocalSend Drop on this PC. Run in PowerShell as administrator:
#   irm https://github.com/Mediapro-Technology/localsend-drop/releases/latest/download/install.ps1 | iex
# Trusts the Mediapro Technology certificate, then installs the latest package for
# the current user and starts it.
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"   # Invoke-WebRequest is very slow with the progress bar
$base = "https://github.com/Mediapro-Technology/localsend-drop/releases/latest/download"
$tmp = Join-Path $env:TEMP "LocalSendDrop-install"
New-Item -ItemType Directory -Force $tmp | Out-Null

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { Write-Host "Run PowerShell as administrator, then paste the command again." -ForegroundColor Yellow; return }

Write-Host "Downloading LocalSend Drop..."
Invoke-WebRequest "$base/LocalSendDrop.cer" -OutFile "$tmp\LocalSendDrop.cer"
Invoke-WebRequest "$base/LocalSendDrop.msix" -OutFile "$tmp\LocalSendDrop.msix"

Write-Host "Trusting the Mediapro Technology certificate..."
Import-Certificate -FilePath "$tmp\LocalSendDrop.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null

# A 1.3 install left a small "sparse" package with the same name; it can't be upgraded in place.
Get-AppxPackage MediaproTechnology.LocalSendDrop | Where-Object { $_.Version -lt [version]"2.0.0.0" } | Remove-AppxPackage

Write-Host "Installing..."
Get-Process LocalSendDrop -ErrorAction SilentlyContinue | Stop-Process -Force
Add-AppxPackage -Path "$tmp\LocalSendDrop.msix" -ForceApplicationShutdown

$family = (Get-AppxPackage MediaproTechnology.LocalSendDrop).PackageFamilyName
Start-Process "shell:AppsFolder\$family!LocalSendDrop"
# .NET, not Remove-Item: Windows PowerShell chokes on short "USER~1" temp paths.
try { [IO.Directory]::Delete($tmp, $true) } catch { }
Write-Host "Done. LocalSend Drop is running: drag files into the top-left corner of your screen." -ForegroundColor Green
