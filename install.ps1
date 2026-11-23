#Requires -Version 5.1
[CmdletBinding()]
param([switch]$Uninstall, [switch]$SkipLegacyStartupCleanup)

$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitOperatingSystem) { throw 'The quota bar requires 64-bit Windows.' }
$installDir = Join-Path $env:LOCALAPPDATA 'GeminiQuota'
$exe = Join-Path $installDir 'GeminiQuota.exe'
$receipt = Join-Path $installDir 'gemini-quota-install.json'
$manifest = Join-Path $env:USERPROFILE '.gemini\config\sidecars\gemini-quota\sidecar.json'
$bind = Join-Path $PSScriptRoot 'bind-sidecar.ps1'
$owner = 'Antigravity.GeminiQuotaBar'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function FileHash([string]$path) {
    if ([IO.File]::Exists($path)) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
}

function SourceHash {
    $inputs = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File) +
        @(Get-Item -LiteralPath (Join-Path $PSScriptRoot 'build.ps1'), (Join-Path $PSScriptRoot 'GeminiQuota.ico'))
    $items = $inputs | Sort-Object Name | ForEach-Object { $_.Name + '=' + (FileHash $_.FullName) }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [Convert]::ToBase64String($sha.ComputeHash($utf8.GetBytes(($items -join "`n")))) }
    finally { $sha.Dispose() }
}

function RequireStopped {
    foreach ($app in @(Get-Process -Name 'GeminiQuota' -ErrorAction SilentlyContinue)) {
        try {
            if ($app.Path -ieq $exe) {
                throw 'Exit the quota bar from its tray menu, or close Antigravity normally, then run this command again.'
            }
        } finally { $app.Dispose() }
    }
}

function OwnedManifest {
    if (-not [IO.File]::Exists($manifest)) { return $false }
    $existing = [IO.File]::ReadAllText($manifest) | ConvertFrom-Json
    if ($existing.command -ine $exe -or $existing.builtin -or @($existing.args | Where-Object { $_ }).Count) {
        throw "The gemini-quota sidecar belongs to another command. Inspect $manifest before installing."
    }
    return $true
}

function SaveReceipt($hash, $source) {
    $json = [ordered]@{ owner=$owner; binaryHash=$hash; sourceHash=$source } | ConvertTo-Json -Compress
    if ([IO.File]::Exists($receipt) -and [IO.File]::ReadAllText($receipt) -ceq $json) { return }
    $temp = "$receipt.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temp, $json, $utf8)
        if ([IO.File]::Exists($receipt)) { [IO.File]::Replace($temp, $receipt, [System.Management.Automation.Language.NullString]::Value) }
        else { [IO.File]::Move($temp, $receipt) }
    } finally { if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) } }
}

function Set-InstalledBinary([string]$inputFile, $oldHash) {
    $null = [IO.Directory]::CreateDirectory($installDir)
    $stage = "$exe.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::Copy($inputFile, $stage)
        if ((FileHash $exe) -ne $oldHash) { throw "Installed file changed during setup. Retry: $exe" }
        if ($oldHash) {
            $backup = "$exe.$([Guid]::NewGuid().ToString('N')).bak"
            [IO.File]::Replace($stage, $exe, $backup)
            return $backup
        }
        [IO.File]::Move($stage, $exe)
    } finally { if ([IO.File]::Exists($stage)) { [IO.File]::Delete($stage) } }
}

$mutex = New-Object Threading.Mutex($false,'Local\Antigravity.GeminiQuotaBar.Install')
$locked = $false
$buildDir = $null
try {
    try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw 'Another quota bar setup is running. Wait for it to finish.' }
    $registered = OwnedManifest
    $record = $null
    if ([IO.File]::Exists($receipt)) {
        $record = [IO.File]::ReadAllText($receipt) | ConvertFrom-Json
        if ($record.owner -cne $owner -or $record.binaryHash -notmatch '^[0-9A-Fa-f]{64}$') {
            throw "Unrecognized install receipt. Inspect $receipt before continuing."
        }
    }
    $oldHash = FileHash $exe
    if ($record -and $oldHash -and $record.binaryHash -ine $oldHash) {
        throw "The installed executable was modified outside setup. Move it aside first: $exe"
    }
    if ($oldHash -and $registered -and -not $record -and
        [Reflection.AssemblyName]::GetAssemblyName($exe).Name -cne 'GeminiQuota') {
        throw "The existing file is not the quota bar. Move it aside first: $exe"
    }

    if ($Uninstall) {
        if (-not $record -and -not $registered) {
            if ($oldHash) { throw "No matching installation record. Leaving the existing file at $exe." }
            Write-Output 'The quota bar is already uninstalled.'
            return
        }
        RequireStopped
        & $bind -Disable -SkipLegacyStartupCleanup:$SkipLegacyStartupCleanup
        if ((FileHash $exe) -ne $oldHash) { throw "Installed file changed during removal. Leaving $exe." }
        if ($oldHash) { [IO.File]::Delete($exe) }
        if ($record) { [IO.File]::Delete($receipt) }
        if ([IO.Directory]::Exists($installDir) -and [IO.Directory]::GetFileSystemEntries($installDir).Length -eq 0) {
            [IO.Directory]::Delete($installDir)
        }
        Write-Output 'Uninstalled the Gemini quota bar.'
        return
    }

    $source = $null
    $inputFile = Join-Path $PSScriptRoot 'GeminiQuota.exe'
    if ([IO.File]::Exists((Join-Path $PSScriptRoot 'Program.cs'))) {
        $source = SourceHash
        if ($record -and $record.sourceHash -ceq $source -and $oldHash) {
            $inputFile = $exe
        } else {
            $buildDir = Join-Path ([IO.Path]::GetTempPath()) ('GeminiQuotaBuild-' + [Guid]::NewGuid().ToString('N'))
            $null = [IO.Directory]::CreateDirectory($buildDir)
            $inputFile = Join-Path $buildDir 'GeminiQuota.exe'
            & (Join-Path $PSScriptRoot 'build.ps1') -OutputPath $inputFile
        }
    }
    $newHash = FileHash $inputFile
    if (-not $newHash -or [Reflection.AssemblyName]::GetAssemblyName($inputFile).Name -cne 'GeminiQuota') {
        throw "The package must contain GeminiQuota.exe or the complete source checkout: $PSScriptRoot"
    }
    $backup = $null
    $changed = $oldHash -ne $newHash
    if ($changed) {
        if ($oldHash -and -not ($registered -or $record)) { throw "Unrecognized existing executable. Move it aside first: $exe" }
        if ($oldHash) { RequireStopped }
        $backup = Set-InstalledBinary $inputFile $oldHash
    }
    try {
        & $bind -SkipLegacyStartupCleanup:$SkipLegacyStartupCleanup
        SaveReceipt $newHash $source
    } catch {
        if ($changed -and (FileHash $exe) -eq $newHash) {
            if ($backup) { [IO.File]::Replace($backup, $exe, [System.Management.Automation.Language.NullString]::Value) }
            else { [IO.File]::Delete($exe) }
        }
        throw
    }
    if ($backup) { [IO.File]::Delete($backup) }
    Write-Output "Installed $exe. Open Antigravity normally to use the quota bar."
    $cliLocations = @('agy\bin\agy.exe','GeminiQuota\tools\agy.exe',
        'Microsoft\WinGet\Packages\Google.AntigravityCLI_Microsoft.Winget.Source_8wekyb3d8bbwe\agy.exe')
    if (-not ($cliLocations | Where-Object { [IO.File]::Exists((Join-Path $env:LOCALAPPDATA $_)) })) {
        Write-Warning 'For account quotas, install and sign in to the official Antigravity CLI: https://antigravity.google/download'
    }
} finally {
    if ($buildDir) {
        $output = Join-Path $buildDir 'GeminiQuota.exe'
        if ([IO.File]::Exists($output)) { [IO.File]::Delete($output) }
        if ([IO.Directory]::Exists($buildDir) -and [IO.Directory]::GetFileSystemEntries($buildDir).Length -eq 0) {
            [IO.Directory]::Delete($buildDir)
        }
    }
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
