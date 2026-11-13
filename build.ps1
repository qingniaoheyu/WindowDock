param(
    [switch]$SkipIcon
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir = Join-Path $root 'src'
$distDir = Join-Path $root 'dist'
$assetDir = Join-Path $root 'assets'

New-Item -ItemType Directory -Force -Path $distDir | Out-Null
New-Item -ItemType Directory -Force -Path $assetDir | Out-Null

$iconPath = Join-Path $assetDir 'app.ico'

if (-not $SkipIcon) {
    Add-Type -AssemblyName System.Drawing

    $bmp = New-Object System.Drawing.Bitmap -ArgumentList 64, 64
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $bg = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(255, 30, 31, 35))
    $g.FillRectangle($bg, 4, 8, 56, 48)

    $rail = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(255, 72, 132, 214))
    $g.FillRectangle($rail, 8, 12, 8, 40)

    $slot = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(255, 205, 212, 224))
    $g.FillRectangle($slot, 22, 14, 14, 36)
    $g.FillRectangle($slot, 42, 14, 14, 36)

    $hIcon = $bmp.GetHicon()
    $icon = [System.Drawing.Icon]::FromHandle($hIcon)
    $fs = [System.IO.File]::Create($iconPath)
    $icon.Save($fs)
    $fs.Close()
    $icon.Dispose()
    $g.Dispose()
    $bmp.Dispose()

    Write-Host "Icon: $iconPath"
}

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    throw "csc.exe not found: $csc"
}

$outPath = Join-Path $distDir 'WindowDock.exe'
$sources = @(Get-ChildItem -Path $srcDir -Filter *.cs | Sort-Object Name | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) {
    throw "No source files found in $srcDir"
}

$cscArgs = @(
    '/nologo',
    '/target:winexe',
    '/platform:x64',
    '/optimize+',
    '/codepage:65001',
    '/r:System.dll',
    '/r:System.Core.dll',
    '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll',
    "/out:$outPath"
)

if ((-not $SkipIcon) -and (Test-Path $iconPath)) {
    $cscArgs += "/win32icon:$iconPath"
}
$cscArgs += $sources

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) {
    throw "Compile failed with exit code $LASTEXITCODE"
}

Write-Host ("OK: {0} ({1:N0} bytes)" -f $outPath, (Get-Item $outPath).Length)
