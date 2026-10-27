# Installs a test-signed MSIX on the CI runner, starts the packaged app with settings open, checks it stays up,
# saves a screenshot, then uninstalls it and removes the test certificate. Needs an elevated runner.
param(
    [Parameter(Mandatory)] [string] $Package,
    [string] $OutDir = "screenshots"
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$cer = [System.IO.Path]::ChangeExtension($Package, ".cer")
$cert = Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
try {
    Add-AppxPackage -Path $Package
    $installed = Get-AppxPackage -Name "ProDHCPServer.Test"
    if (-not $installed) { throw "The MSIX installed but Windows doesn't list it." }
    Write-Host "Installed $($installed.PackageFullName) to $($installed.InstallLocation)"
    try {
        $p = Start-Process -FilePath (Join-Path $installed.InstallLocation "ProDHCPServer.exe") -ArgumentList "--open-settings" -PassThru
        Start-Sleep -Seconds 15
        if ($p.HasExited) {
            Get-ChildItem (Join-Path $env:ProgramData "ProDHCPServer") -Filter "crash-*.txt" -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName }
            throw "The packaged ProDHCPServer exited during startup with code $($p.ExitCode)."
        }
        $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
        $bmp.Save((Join-Path $OutDir "msix-settings.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        Write-Host "Packaged app running, screenshot saved. Main window title: '$($p.MainWindowTitle)'"
        Stop-Process -Id $p.Id -Force
        Start-Sleep -Seconds 2
    }
    finally { Remove-AppxPackage -Package $installed.PackageFullName }
}
finally { Remove-Item $cert.PSPath -Force }
