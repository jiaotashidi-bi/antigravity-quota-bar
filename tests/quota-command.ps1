#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$dir = Join-Path ([IO.Path]::GetTempPath()) ('quota-command-test-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($dir)
$exe = Join-Path $dir 'Probe.exe'
$errors = Join-Path $dir 'stderr.txt'
try {
    & $compiler /nologo /platform:x64 /target:exe "/out:$exe" (Join-Path $root 'QuotaCommand.cs') (Join-Path $PSScriptRoot 'QuotaCommandProbe.cs')
    if ($LASTEXITCODE) { throw "Probe compilation failed ($LASTEXITCODE)." }
    $probe = Start-Process -FilePath $exe -WindowStyle Hidden -Wait -PassThru -RedirectStandardError $errors
    try {
        if ($probe.ExitCode -ne 0) { throw "Quota command test failed ($($probe.ExitCode)). $([IO.File]::ReadAllText($errors))" }
    } finally { $probe.Dispose() }
    'PASS: child creation was blocked with error 367 and UTF-8 quota output was preserved.'
} finally {
    [IO.File]::Delete($exe)
    [IO.File]::Delete($errors)
    [IO.Directory]::Delete($dir)
}
