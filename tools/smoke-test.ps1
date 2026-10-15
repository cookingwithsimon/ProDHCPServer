# Launches the published exe in each theme, checks it stays up, and saves a screenshot.
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

foreach ($theme in @("Dark", "Light")) {
    @{ Theme = $theme } | ConvertTo-Json | Set-Content -Path (Join-Path $settingsDir "settings.json") -Encoding utf8
    $p = Start-Process -FilePath $Exe -PassThru
    Start-Sleep -Seconds 15
    if ($p.HasExited) {
        Get-ChildItem (Join-Path $env:ProgramData "ProDHCPServer") -Filter "crash-*.txt" -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName }
        throw "ProDHCPServer exited during startup ($theme theme) with code $($p.ExitCode)."
    }
    Save-Screen (Join-Path $OutDir "main-$($theme.ToLower()).png")
    Write-Host "$theme theme: running, screenshot saved. Main window title: '$($p.MainWindowTitle)'"
    Stop-Process -Id $p.Id -Force
    Start-Sleep -Seconds 2
}
Remove-Item (Join-Path $settingsDir "settings.json") -ErrorAction SilentlyContinue

# The artifact store isn't reachable from every reviewer's environment, so also print each
# screenshot (downscaled JPEG, base64) to the log between markers.
Add-Type -AssemblyName System.Drawing
foreach ($png in Get-ChildItem $OutDir -Filter *.png) {
    $img = [System.Drawing.Image]::FromFile($png.FullName)
    $w = [Math]::Min(1280, $img.Width); $h = [int]($img.Height * $w / $img.Width)
    $small = New-Object System.Drawing.Bitmap $img, $w, $h
    $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq "image/jpeg"
    $params = New-Object System.Drawing.Imaging.EncoderParameters 1
    $params.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 70L
    $ms = New-Object System.IO.MemoryStream
    $small.Save($ms, $codec, $params)
    $b64 = [Convert]::ToBase64String($ms.ToArray())
    Write-Host "BEGIN-SCREENSHOT $($png.BaseName) $($img.Width)x$($img.Height)"
    for ($i = 0; $i -lt $b64.Length; $i += 4000) { Write-Host $b64.Substring($i, [Math]::Min(4000, $b64.Length - $i)) }
    Write-Host "END-SCREENSHOT $($png.BaseName)"
    $img.Dispose(); $small.Dispose(); $ms.Dispose()
}
