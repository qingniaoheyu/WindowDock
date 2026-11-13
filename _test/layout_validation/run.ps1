$ErrorActionPreference = 'Stop'
$validationDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$toolbarDir = Split-Path -Parent (Split-Path -Parent $validationDir)
$appPath = Join-Path $toolbarDir 'dist\WindowDock.exe'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
Copy-Item $appPath (Join-Path $validationDir 'WindowDock.exe') -Force
Copy-Item (Join-Path $toolbarDir '_test\feature_validation\PreviewHost.exe') (Join-Path $validationDir 'PreviewHost.exe') -Force
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "/r:$appPath" "/out:$validationDir\LayoutValidation.exe" "$validationDir\LayoutValidation.cs"
if ($LASTEXITCODE -ne 0) { throw 'Layout validation build failed' }
$validationProcess = Start-Process (Join-Path $validationDir 'LayoutValidation.exe') -PassThru -Wait -RedirectStandardOutput (Join-Path $validationDir 'results.log') -RedirectStandardError (Join-Path $validationDir 'errors.log')
Get-Content (Join-Path $validationDir 'results.log')
if ($validationProcess.ExitCode -ne 0) { Get-Content (Join-Path $validationDir 'errors.log'); throw 'Layout validation failed' }
