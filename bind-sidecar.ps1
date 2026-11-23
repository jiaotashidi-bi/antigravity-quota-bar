[CmdletBinding()]
param([switch]$SkipLegacyStartupCleanup, [switch]$Disable)

$ErrorActionPreference = 'Stop'
$id = 'gemini-quota'
$exe = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'GeminiQuota\GeminiQuota.exe'))
$configRoot = Join-Path $env:USERPROFILE '.gemini\config'
$manifestDir = Join-Path $configRoot "sidecars\$id"
$manifestPath = Join-Path $manifestDir 'sidecar.json'
$configPath = Join-Path $configRoot 'config.json'
$utf8 = New-Object System.Text.UTF8Encoding($false, $true)

# Keep unrelated JSON byte-for-byte, including formatting, date strings, and large numbers.
# The tokenizer only locates the three members in sidecars -> gemini-quota -> enabled.
function Set-QuotaSidecarEnabled([string]$json, [bool]$enabled) {
    $null = ConvertFrom-Json -InputObject $json -ErrorAction Stop
    $tokens = [regex]::Matches($json, '"(?:\\.|[^"\\])*"|[{}\[\]:,]|[^\s{}\[\]:,"]+')
    $path = @('sidecars', $id, 'enabled')
    $start = 0
    for ($level = 0; $level -lt $path.Count; $level++) {
        if ($tokens[$start].Value -ne '{') {
            throw "Expected a JSON object for the owner of '$($path[$level])' in $configPath."
        }
        $depth = 0
        $at = -1
        for ($i = $start + 1; $i -lt $tokens.Count; $i++) {
            $value = $tokens[$i].Value
            if (($value -eq '}') -and ($depth -eq 0)) { break }
            if ($value -in @('{','[')) { $depth++; continue }
            if ($value -in @('}',']')) { $depth--; continue }
            if ($depth -eq 0 -and $value.StartsWith('"') -and $tokens[$i + 1].Value -eq ':') {
                $key = ConvertFrom-Json -InputObject $value
                if ($key -ceq $path[$level]) {
                    if ($at -ge 0) { throw "Duplicate '$key' in $configPath." }
                    $at = $i + 2
                }
            }
        }
        if ($at -lt 0) {
            if (-not $enabled) { return $json }
            $entry = 'true'
            for ($j = $path.Count - 1; $j -ge $level; $j--) {
                if ($j -lt $path.Count - 1) { $entry = '{' + $entry + '}' }
                $entry = '"' + $path[$j] + '": ' + $entry
            }
            $open = $tokens[$start].Index + 1
            $gap = $json.Substring($open, $tokens[$start + 1].Index - $open)
            if ($tokens[$start + 1].Value -ne '}') { $entry += ',' }
            return $json.Insert($open, $gap + $entry)
        }
        if ($level -eq $path.Count - 1) {
            $flag = $tokens[$at]
            if ($flag.Value -cnotin @('true','false')) { throw "Expected a boolean '$id.enabled' in $configPath." }
            $target = if ($enabled) { 'true' } else { 'false' }
            if ($flag.Value -ceq $target) { return $json }
            return $json.Remove($flag.Index, $flag.Length).Insert($flag.Index, $target)
        }
        $start = $at
    }
}

function Read-BytesOrNull([string]$file) {
    if ([IO.File]::Exists($file)) { return ,([IO.File]::ReadAllBytes($file)) }
}

function Equal-Bytes($a, $b) {
    if ($null -eq $a -or $null -eq $b) { return ($null -eq $a -and $null -eq $b) }
    return [Convert]::ToBase64String($a) -ceq [Convert]::ToBase64String($b)
}

function Decode-Json($bytes) {
    return $utf8.GetString($bytes).TrimStart([char]0xFEFF)
}

# A sibling temp file keeps each replacement on the same volume. A second read detects
# external edits. File.Replace preserves the previous file as a recoverable exact backup.
function Write-Checked([string]$file, $expected, [string]$content) {
    $bytes = $utf8.GetBytes($content)
    if (-not (Equal-Bytes (Read-BytesOrNull $file) $expected)) {
        throw "File changed during setup. Run again to merge the latest version: $file"
    }
    if (Equal-Bytes $expected $bytes) { return }
    $temp = "$file.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllBytes($temp, $bytes)
        if ($null -eq $expected) {
            [IO.File]::Move($temp, $file)
        } else {
            $backup = "$file.$([Guid]::NewGuid().ToString('N')).bak"
            [IO.File]::Replace($temp, $file, $backup)
            if (-not (Equal-Bytes (Read-BytesOrNull $backup) $expected)) {
                if (Equal-Bytes (Read-BytesOrNull $file) $bytes) {
                    # A writer changed the original between the comparison and replacement.
                    [IO.File]::Copy($backup, $temp)
                    [IO.File]::Replace($temp, $file, "$backup.rollback")
                }
                throw "Concurrent update detected. Latest pre-install contents are in $backup. Re-run setup."
            }
        }
    } finally {
        if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) }
    }
}

if (-not $Disable -and -not [IO.File]::Exists($exe)) { throw "Install the quota bar first: $exe" }
$cfgOld = Read-BytesOrNull $configPath
$cfgText = if ($null -eq $cfgOld) { '{}' } else { Decode-Json $cfgOld }
$cfgNew = Set-QuotaSidecarEnabled $cfgText (-not $Disable)
$null = ConvertFrom-Json -InputObject $cfgNew
$oldManifest = Read-BytesOrNull $manifestPath
if ($null -ne $oldManifest) {
    $manifestText = Decode-Json $oldManifest
    $existing = ConvertFrom-Json -InputObject $manifestText
    if ($existing.command -ine $exe -or $existing.builtin -or @($existing.args | Where-Object { $_ }).Count) {
        throw "Sidecar ID '$id' is already used by another command: $manifestPath"
    }
} elseif (-not $Disable) {
    $manifest = [ordered]@{
        display_name = 'Gemini Quota'
        description = 'Show Gemini quota near the Antigravity composer.'
        command = $exe
        restart_policy = 'on-failure'
    } | ConvertTo-Json
    $manifestText = $manifest + [Environment]::NewLine
}
if ($Disable) {
    if ($null -ne $oldManifest) {
        if ($null -ne $cfgOld) { Write-Checked $configPath $cfgOld $cfgNew }
        if (-not (Equal-Bytes (Read-BytesOrNull $manifestPath) $oldManifest)) { throw "Sidecar manifest changed: $manifestPath" }
        [IO.File]::Delete($manifestPath)
        if ([IO.Directory]::GetFileSystemEntries($manifestDir).Length -eq 0) { [IO.Directory]::Delete($manifestDir) }
    }
} else {
    $null = [IO.Directory]::CreateDirectory($manifestDir)
    Write-Checked $manifestPath $oldManifest $manifestText
    Write-Checked $configPath $cfgOld $cfgNew
}

if (-not $SkipLegacyStartupCleanup) {
    $runKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run', $true)
    if ($null -ne $runKey) {
        try {
            $name = 'GeminiQuotaBar'
            $old = $runKey.GetValue($name)
            if ($old -is [string] -and $old.Trim() -iin @($exe, ('"' + $exe + '"'))) {
                $runKey.DeleteValue($name, $false)
            }
        } finally { $runKey.Dispose() }
    }
}
if ($Disable) { Write-Output "Removed the quota bar's Antigravity sidecar registration." }
else { Write-Output "Enabled Antigravity sidecar '$id' at $manifestPath" }
