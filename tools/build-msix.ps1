# Wraps the published single-file exe in an MSIX package for the Microsoft Store.
# The Store signs what it publishes, so the Store package is left unsigned. -TestSign signs a copy with a
# throwaway self-signed certificate instead, so a test laptop can install it after trusting the .cer.
# Windows only: needs MakeAppx.exe and SignTool.exe from the Windows SDK (installed on GitHub's Windows runners).
param(
    [Parameter(Mandatory)] [string] $Exe,
    # x.y.z; the Store wants the fourth part to be 0.
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $OutFile,
    # Partner Center > the app > Product identity: Package/Identity/Name, Package/Identity/Publisher and Package/Properties/PublisherDisplayName.
    [string] $IdentityName = "ProDHCPServer.Test",
    [string] $Publisher = "CN=ProDHCPServer Test",
    [string] $PublisherDisplayName = "ProDHCPServer test build",
    [switch] $TestSign
)
$ErrorActionPreference = "Stop"
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version '$Version' must look like 1.2.3" }

$sdkBin = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64" -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName "makeappx.exe") } |
    Sort-Object { [version]$_.Name } | Select-Object -Last 1
if (-not $sdkBin) { throw "Can't find MakeAppx.exe. Install the Windows 10/11 SDK." }
$makeAppx = Join-Path $sdkBin.FullName "makeappx.exe"
$signTool = Join-Path $sdkBin.FullName "signtool.exe"

$root = Split-Path $PSScriptRoot -Parent
$layout = Join-Path ([System.IO.Path]::GetTempPath()) "ProDHCPServer-msix-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $layout | Out-Null
try {
    Copy-Item $Exe (Join-Path $layout "ProDHCPServer.exe")
    Copy-Item (Join-Path $root "packaging/msix/Images") (Join-Path $layout "Images") -Recurse
    $manifest = Get-Content (Join-Path $root "packaging/msix/AppxManifest.xml") -Raw
    $values = @{ IdentityName = $IdentityName; Publisher = $Publisher; PublisherDisplayName = $PublisherDisplayName; Version = "$Version.0" }
    foreach ($key in $values.Keys) { $manifest = $manifest.Replace("`$$key`$", [System.Security.SecurityElement]::Escape($values[$key])) }
    if ($manifest -match '\$\w+\$') { throw "AppxManifest.xml still has a placeholder: $($Matches[0])" }
    Set-Content (Join-Path $layout "AppxManifest.xml") $manifest -Encoding utf8

    New-Item -ItemType Directory -Force -Path (Split-Path $OutFile -Parent) | Out-Null
    & $makeAppx pack /d $layout /p $OutFile /o
    if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed with exit code $LASTEXITCODE." }

    if ($TestSign) {
        # The certificate subject has to match the manifest's Publisher exactly.
        $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature -FriendlyName "ProDHCPServer test signing" `
            -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(1) `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
        try {
            $password = ConvertTo-SecureString ([guid]::NewGuid().ToString()) -AsPlainText -Force
            $pfx = Join-Path $layout "test.pfx"
            Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $password | Out-Null
            & $signTool sign /fd SHA256 /f $pfx /p ([System.Net.NetworkCredential]::new("", $password).Password) $OutFile
            if ($LASTEXITCODE -ne 0) { throw "SignTool failed with exit code $LASTEXITCODE." }
            Export-Certificate -Cert $cert -FilePath ([System.IO.Path]::ChangeExtension($OutFile, ".cer")) | Out-Null
        }
        finally { Remove-Item $cert.PSPath -Force }
    }
    Get-Item $OutFile
}
finally { Remove-Item $layout -Recurse -Force -ErrorAction SilentlyContinue }
