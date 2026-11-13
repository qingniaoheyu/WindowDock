param(
    [string]$Version = '0.0.0',
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $root
if (-not $OutDir) { $OutDir = Join-Path $repo 'artifacts' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# 1) Build the executable (build.ps1 regenerates the icon and compiles all of src/*.cs)
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw "build.ps1 failed with exit code $LASTEXITCODE" }

$exe = Join-Path $repo 'dist\WindowDock.exe'
if (-not (Test-Path $exe)) { throw "missing build output: $exe" }
$exeItem = Get-Item $exe
if ($exeItem.Length -lt 10000) { throw "build output suspiciously small: $($exeItem.Length) bytes" }

# sanity: a Windows PE starts with MZ and has a PE header
$bytes = [IO.File]::ReadAllBytes($exe)
if ($bytes.Length -lt 0x40 -or [Text.Encoding]::ASCII.GetString($bytes, 0, 2) -ne 'MZ') {
    throw 'build output is not a valid PE image (missing MZ)'
}

# 2) Stamp the version into a fresh copy of the sources used by CI
#    (AssemblyInfo.cs is patched in-place by the workflow before calling this script)

$tag = 'v' + $Version
$exeName = "WindowDock.exe"
$zipName = "WindowDock-$tag-win-x64.zip"
$exeOut = Join-Path $OutDir $exeName
$zipOut = Join-Path $OutDir $zipName

Copy-Item -LiteralPath $exe -Destination $exeOut -Force

$staging = Join-Path $OutDir 'staging'
New-Item -ItemType Directory -Force -Path $staging | Out-Null
Copy-Item -LiteralPath $exe -Destination (Join-Path $staging 'WindowDock.exe') -Force
$readme = @"
WindowDock $tag
================

Windows 10/11 x64 (需要 .NET Framework 4.x)。免安装，直接双击 WindowDock.exe。

默认快捷键 Ctrl + Alt + Space，可在设置中修改。通过停靠栏或系统托盘菜单
添加窗口、设置布局与外观。更新前退出旧实例，替换 EXE，保留原有
WindowDock.ini 即可保留任务与设置。

校验值见同目录下的 SHA256SUMS.txt。
源码：https://github.com/qingniaoheyu/WindowDock
"@
Set-Content -LiteralPath (Join-Path $staging 'README.txt') -Value $readme -Encoding UTF8

if (Test-Path $zipOut) { Remove-Item -LiteralPath $zipOut -Force }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipOut -CompressionLevel Optimal

# 3) checksums for both published artifacts
$lines = @()
foreach ($f in @($exeOut, $zipOut)) {
    $h = (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash.ToLower()
    $lines += "$h  $([IO.Path]::GetFileName($f))"
}
Set-Content -LiteralPath (Join-Path $OutDir 'SHA256SUMS.txt') -Value ($lines -join "`n") -Encoding ascii

Remove-Item -LiteralPath $staging -Recurse -Force

Write-Host "tag=$tag"
Write-Host "exe=$exeOut ($($exeItem.Length) bytes)"
Write-Host "zip=$zipOut ($((Get-Item $zipOut).Length) bytes)"
Get-Content -LiteralPath (Join-Path $OutDir 'SHA256SUMS.txt') | ForEach-Object { Write-Host "sum: $_" }
