#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$dir = Join-Path $temp ('quota-usage-test-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($dir)
$exe = Join-Path $dir 'Test.exe'
try {
    & $compiler /nologo /platform:x64 /target:exe /codepage:65001 "/out:$exe" (Join-Path $root 'ContextReader.cs') (Join-Path $root 'UsageReader.cs') (Join-Path $PSScriptRoot 'UsageReaderProbe.cs')
    if ($LASTEXITCODE) { throw "Test compilation failed ($LASTEXITCODE)." }
    & $exe $dir
    if ($LASTEXITCODE) { throw "Usage reader tests failed ($LASTEXITCODE)." }
} finally {
    if (([IO.DirectoryInfo]$dir).Parent.FullName -eq $temp.TrimEnd('\') -and (Split-Path -Leaf $dir) -match '^quota-usage-test-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $dir -Force -Recurse
    }
}
