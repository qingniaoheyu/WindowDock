$ErrorActionPreference = 'Stop'
$validationDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$toolbarDir = Split-Path -Parent (Split-Path -Parent $validationDir)
$appPath = Join-Path $toolbarDir 'dist\WindowDock.exe'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
Copy-Item $appPath (Join-Path $validationDir 'WindowDock.exe') -Force
foreach ($name in @('PreviewHost', 'FeatureValidation')) {
    $target = if ($name -eq 'PreviewHost') { '/target:winexe' } else { '/target:exe' }
    $validationSources = @("$validationDir\$name.cs")
    if ($name -eq 'PreviewHost') { $validationSources += "$validationDir\QtRenderingHost.cs" }
    & $compiler /nologo $target /platform:x64 /codepage:65001 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "/r:$appPath" "/out:$validationDir\$name.exe" $validationSources
    if ($LASTEXITCODE -ne 0) { throw "Validation build failed: $name" }
}
$validationProcess = Start-Process (Join-Path $validationDir 'FeatureValidation.exe') -PassThru -Wait -RedirectStandardOutput (Join-Path $validationDir 'results.log') -RedirectStandardError (Join-Path $validationDir 'errors.log')
Get-Content (Join-Path $validationDir 'results.log')
if ($validationProcess.ExitCode -ne 0) {
    Get-Content (Join-Path $validationDir 'errors.log')
    throw 'Feature validation failed; see results.log and errors.log'
}
