# Builds the Windows 11 right-click menu pieces into shell\bin:
#   LocalSendDropShell.dll  - the menu handler (copied next to LocalSendDrop.exe at runtime)
#   LocalSendDrop.msix      - sparse package that registers it, signed
#   LocalSendDrop.cer       - public certificate PCs must trust (TrustedPeople)
# The private signing key lives outside the repo (default D:\Tools\LocalSendDrop-signing).
#   ./shell/build.ps1 -Version 1.0.0.0
param(
    [string]$Version = "1.0.0.0",
    [string]$SigningDir = "D:\Tools\LocalSendDrop-signing"
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$bin = Join-Path $PSScriptRoot "bin"
$stage = Join-Path $PSScriptRoot "obj\package"
New-Item -ItemType Directory -Force $bin, $stage, "$stage\Assets" | Out-Null

$kit = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Directory |
    Where-Object { Test-Path "$($_.FullName)\x64\makeappx.exe" } | Sort-Object Name | Select-Object -Last 1
$makeappx = "$($kit.FullName)\x64\makeappx.exe"
$signtool = "$($kit.FullName)\x64\signtool.exe"

# 1. DLL (MSVC from Visual Studio)
$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$vcvars = "$vs\VC\Auxiliary\Build\vcvars64.bat"
$cl = "cl /nologo /O2 /EHsc /std:c++17 /LD /MT /DUNICODE /D_UNICODE ExplorerCommand.cpp /Fe:`"$bin\LocalSendDropShell.dll`" /Fo:`"$stage\\`" /link /DEF:LocalSendDropShell.def"
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"
$out = cmd /c "`"$vcvars`" >nul 2>&1 && $cl 2>&1"
if ($LASTEXITCODE -ne 0) { $out; throw "DLL build failed" }
Remove-Item "$bin\LocalSendDropShell.exp", "$bin\LocalSendDropShell.lib" -ErrorAction SilentlyContinue

# 2. Package contents: manifest + logos (app files stay outside, next to the exe)
(Get-Content AppxManifest.xml -Raw).Replace('$VERSION$', $Version) | Set-Content "$stage\AppxManifest.xml" -Encoding utf8
Copy-Item Assets\*.png "$stage\Assets\"
& $makeappx pack /o /nv /d $stage /p "$bin\LocalSendDrop.msix" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx failed" }

# 3. Sign with the self-made "CN=Mediapro Technology" certificate (created on first run)
$pfx = Join-Path $SigningDir "signing.pfx"
$pwFile = Join-Path $SigningDir "password.txt"
if (-not (Test-Path $pfx)) {
    New-Item -ItemType Directory -Force $SigningDir | Out-Null
    $pw = [Convert]::ToBase64String((1..24 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]])
    $cert = New-SelfSignedCertificate -Type Custom -Subject "CN=Mediapro Technology" -KeyUsage DigitalSignature `
        -FriendlyName "LocalSend Drop package signing" -CertStoreLocation Cert:\CurrentUser\My `
        -NotAfter (Get-Date).AddYears(20) `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $pw -AsPlainText -Force) | Out-Null
    Set-Content $pwFile $pw -NoNewline
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"
}
$pw = Get-Content $pwFile -Raw
& $signtool sign /q /fd SHA256 /f $pfx /p $pw "$bin\LocalSendDrop.msix"
if ($LASTEXITCODE -ne 0) { throw "signing failed" }

$c = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfx, $pw)
[IO.File]::WriteAllBytes("$bin\LocalSendDrop.cer", $c.Export('Cert'))
Get-ChildItem $bin | Select-Object Name, Length
