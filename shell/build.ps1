# Packages dist\LocalSendDrop.exe as dist\LocalSendDrop.msix (signed), writes
# dist\LocalSendDrop.cer (the certificate PCs must trust) and builds
# dist\LocalSendDrop-Setup.exe, which carries both and installs everything in one go.
#   ./shell/build.ps1 -Version 2.0.0.0 [-RebuildDll]
# The right-click handler (shell\bin\LocalSendDropShell.dll) is committed; it's only
# recompiled with -RebuildDll. Needs the Windows SDK and Visual Studio's C++ tools.
# The private signing key lives outside the repo (default D:\Tools\LocalSendDrop-signing);
# losing it means every PC has to trust a new certificate.
param(
    [Parameter(Mandatory)][string]$Version,
    [switch]$RebuildDll,
    [string]$SigningDir = "D:\Tools\LocalSendDrop-signing"
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot
$shell = $PSScriptRoot
$bin = Join-Path $shell "bin"
$dist = Join-Path $root "dist"
$stage = Join-Path $shell "obj\package"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $bin, $stage, "$stage\Assets" | Out-Null

$kit = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Directory |
    Where-Object { Test-Path "$($_.FullName)\x64\makeappx.exe" } | Sort-Object Name | Select-Object -Last 1
$makeappx = "$($kit.FullName)\x64\makeappx.exe"
$signtool = "$($kit.FullName)\x64\signtool.exe"

$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$vcvars = "$vs\VC\Auxiliary\Build\vcvars64.bat"
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"

function Msvc([string]$dir, [string]$commands) {
    $out = cmd /c "`"$vcvars`" >nul 2>&1 && cd /d `"$dir`" && $commands 2>&1"
    if ($LASTEXITCODE -ne 0) { $out | Write-Host; throw "C++ build failed in $dir" }
}

# 1. Right-click handler DLL
if ($RebuildDll -or -not (Test-Path "$bin\LocalSendDropShell.dll")) {
    Msvc $shell "cl /nologo /O2 /EHsc /std:c++17 /LD /MT /DUNICODE /D_UNICODE ExplorerCommand.cpp /Fe:`"$bin\LocalSendDropShell.dll`" /Fo:`"$stage\\`" /link /DEF:LocalSendDropShell.def"
    Remove-Item "$bin\LocalSendDropShell.exp", "$bin\LocalSendDropShell.lib", "$stage\*.obj" -ErrorAction SilentlyContinue
}

# 2. Package contents
if (-not (Test-Path "$dist\LocalSendDrop.exe")) { throw "Publish first: dotnet publish -c Release -o dist" }
Copy-Item "$dist\LocalSendDrop.exe", "$bin\LocalSendDropShell.dll" $stage
(Get-Content "$shell\AppxManifest.xml" -Raw).Replace('$VERSION$', $Version) | Set-Content "$stage\AppxManifest.xml" -Encoding utf8
Copy-Item "$shell\Assets\*.png" "$stage\Assets\"
$out = & $makeappx pack /o /d $stage /p "$dist\LocalSendDrop.msix" 2>&1
if ($LASTEXITCODE -ne 0) { $out | Write-Host; throw "makeappx failed" }

# 3. Sign with the self-made "CN=Mediapro Technology" certificate (created on first run)
$pfx = Join-Path $SigningDir "signing.pfx"
$pwFile = Join-Path $SigningDir "password.txt"
if (-not (Test-Path $pfx)) {
    New-Item -ItemType Directory -Force $SigningDir | Out-Null
    $bytes = New-Object byte[] 24
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $pw = [Convert]::ToBase64String($bytes)
    $cert = New-SelfSignedCertificate -Type Custom -Subject "CN=Mediapro Technology" -KeyUsage DigitalSignature `
        -FriendlyName "LocalSend Drop package signing" -CertStoreLocation Cert:\CurrentUser\My `
        -NotAfter (Get-Date).AddYears(20) `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $pw -AsPlainText -Force) | Out-Null
    Set-Content $pwFile $pw -NoNewline
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"
}
$pw = Get-Content $pwFile -Raw
& $signtool sign /q /fd SHA256 /f $pfx /p $pw "$dist\LocalSendDrop.msix" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "signing failed" }

$c = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfx, $pw)
[IO.File]::WriteAllBytes("$bin\LocalSendDrop.cer", $c.Export('Cert'))
Copy-Item "$bin\LocalSendDrop.cer" $dist

# 4. LocalSendDrop-Setup.exe: certificate + package in one file
$setup = Join-Path $root "setup"
$obj = Join-Path $setup "obj"
New-Item -ItemType Directory -Force $obj | Out-Null
$v = $Version.Split('.')
Set-Content "$setup\version.h" "#define SETUP_VERSION $($v -join ',')`r`n#define SETUP_VERSION_STR `"$Version`"" -Encoding ascii
Msvc $setup ("rc /nologo /fo `"$obj\Setup.res`" Setup.rc && " +
    "cl /nologo /O2 /EHsc /std:c++17 /MT /DUNICODE /D_UNICODE Setup.cpp `"$obj\Setup.res`" /Fo:`"$obj\\`" /Fe:`"$dist\LocalSendDrop-Setup.exe`" /link /SUBSYSTEM:WINDOWS")

Get-Item "$dist\LocalSendDrop.msix", "$dist\LocalSendDrop.cer", "$dist\LocalSendDrop-Setup.exe" | Select-Object Name, Length
