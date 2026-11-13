$ErrorActionPreference = 'Stop'
$testDir = $PSScriptRoot
$toolbarDir = Split-Path -Parent (Split-Path -Parent $testDir)
$appPath = Join-Path $toolbarDir 'dist\WindowDock.exe'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
Copy-Item $appPath (Join-Path $testDir 'WindowDock.exe') -Force
& $compiler /nologo /target:exe /platform:x64 /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "/out:$testDir\HangHost.exe" "$testDir\HangHost.cs"
if ($LASTEXITCODE -ne 0) { throw 'Host build failed' }
& $compiler /nologo /target:exe /platform:x64 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "/r:$appPath" "/out:$testDir\HangValidation.exe" "$testDir\HangValidation.cs"
if ($LASTEXITCODE -ne 0) { throw 'Validation build failed' }
$validation = Start-Process (Join-Path $testDir 'HangValidation.exe') -PassThru -Wait -RedirectStandardOutput (Join-Path $testDir 'results.log') -RedirectStandardError (Join-Path $testDir 'errors.log')
Get-Content (Join-Path $testDir 'results.log')
if ($validation.ExitCode -ne 0) { Get-Content (Join-Path $testDir 'errors.log'); throw 'Hang validation failed' }
