# Launches the published exe in each look (dark and light), checks it stays up, and saves a screenshot.
# Runs on the CI runner, which is already elevated, so no UAC prompt appears.
param(
    [Parameter(Mandatory)] [string] $Exe,
    [string] $OutDir = "screenshots"
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$settingsDir = Join-Path $env:LOCALAPPDATA "ProDHCPServer"
New-Item -ItemType Directory -Force -Path $settingsDir | Out-Null

function Save-Screen([string] $path) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

# Each shot: dark or light, look, file name, extra arguments. "--open-settings" opens the settings slide-over.
# The first three keep their names: README screenshots are taken from them.
$shots = @(
    @{ Theme = "Dark"; Look = "Faceplate"; Name = "main-dark"; Args = @() },
    @{ Theme = "Light"; Look = "Faceplate"; Name = "main-light"; Args = @() },
    @{ Theme = "Dark"; Look = "Faceplate"; Name = "settings-dark"; Args = @("--open-settings") },
    @{ Theme = "Light"; Look = "FrutigerAero"; Name = "aero-light"; Args = @() },
    @{ Theme = "Dark"; Look = "FrutigerAero"; Name = "aero-dark"; Args = @() },
    @{ Theme = "Light"; Look = "FrutigerAero"; Name = "aero-settings-light"; Args = @("--open-settings") },
    @{ Theme = "Dark"; Look = "MetalHeart"; Name = "metal-dark"; Args = @() },
    @{ Theme = "Light"; Look = "MetalHeart"; Name = "metal-light"; Args = @() },
    @{ Theme = "Dark"; Look = "MetalHeart"; Name = "metal-settings-dark"; Args = @("--open-settings") }
)
foreach ($shot in $shots) {
    @{ Theme = $shot.Theme; Look = $shot.Look } | ConvertTo-Json | Set-Content -Path (Join-Path $settingsDir "settings.json") -Encoding utf8
    if ($shot.Args.Count -gt 0) { $p = Start-Process -FilePath $Exe -ArgumentList $shot.Args -PassThru }
    else { $p = Start-Process -FilePath $Exe -PassThru }
    Start-Sleep -Seconds 15
    if ($p.HasExited) {
        Get-ChildItem (Join-Path $env:ProgramData "ProDHCPServer") -Filter "crash-*.txt" -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName }
        throw "ProDHCPServer exited during startup ($($shot.Name)) with code $($p.ExitCode)."
    }
    Save-Screen (Join-Path $OutDir "$($shot.Name).png")
    Write-Host "$($shot.Name): running, screenshot saved. Main window title: '$($p.MainWindowTitle)'"
    Stop-Process -Id $p.Id -Force
    Start-Sleep -Seconds 2
}
Remove-Item (Join-Path $settingsDir "settings.json") -ErrorAction SilentlyContinue

