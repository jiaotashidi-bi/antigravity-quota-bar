param([string]$OutputPath = (Join-Path $PSScriptRoot 'GeminiQuota.exe'))
$ErrorActionPreference = 'Stop'
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs = @('System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll',
    'WPF\UIAutomationClient.dll','WPF\UIAutomationTypes.dll','WPF\WindowsBase.dll') |
    ForEach-Object { '/reference:' + (Join-Path $fw $_) }
$source = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File | ForEach-Object { $_.FullName }
$out = [IO.Path]::GetFullPath($OutputPath)
& (Join-Path $fw 'csc.exe') /nologo /target:winexe /platform:x64 /langversion:5 /codepage:65001 /optimize+ "/out:$out" "/win32icon:$(Join-Path $PSScriptRoot 'GeminiQuota.ico')" @refs @source
if ($LASTEXITCODE) { throw "C# compiler exited with code $LASTEXITCODE" }
