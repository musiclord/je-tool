#requires -Version 7.4

using namespace System.IO

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('SourcePreflight', 'Verify')]
    [string] $Mode,

    [Parameter(Mandatory = $true)]
    [string] $SourceRoot,

    [Parameter(Mandatory = $true)]
    [string] $ManifestPath,

    [string] $CandidatePath = '',
    [string] $OwnedRoot = '',
    [string] $RegistryPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$maximumFileBytes = 512MB
$maximumConfigurationBytes = 1MB
$validationErrors = New-Object 'System.Collections.Generic.List[object]'

function Add-ValidationError {
    param(
        [Parameter(Mandatory = $true)] [string] $Code,
        [string] $Path = ''
    )

    $entry = [ordered]@{ code = $Code }
    if (-not [string]::IsNullOrWhiteSpace($Path)) {
        $entry.path = $Path.Replace(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar)
    }
    $script:validationErrors.Add($entry)
}

function Test-DescendantPath {
    param(
        [Parameter(Mandatory = $true)] [string] $Root,
        [Parameter(Mandatory = $true)] [string] $Candidate
    )

    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $candidateFull = [IO.Path]::GetFullPath($Candidate)
    return $candidateFull.StartsWith(
        $rootFull + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)
}

function Get-RelativeFilePath {
    param(
        [Parameter(Mandatory = $true)] [string] $Root,
        [Parameter(Mandatory = $true)] [string] $Path
    )

    $relative = [IO.Path]::GetRelativePath($Root, $Path).Replace(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    if ($relative -eq '..' -or $relative.StartsWith('../', [StringComparison]::Ordinal)) {
        throw [ArgumentException]::new('A package file escaped its declared root.')
    }
    return $relative
}

function Write-AtomicJson {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] $Value
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $parent = Split-Path -Parent $fullPath
    [void][IO.Directory]::CreateDirectory($parent)
    $temporaryPath = "$fullPath.tmp-$([Guid]::NewGuid().ToString('N'))"
    try {
        [IO.File]::WriteAllText(
            $temporaryPath,
            ($Value | ConvertTo-Json -Depth 20) + "`n",
            $script:utf8)
        [IO.File]::Move($temporaryPath, $fullPath, $true)
    }
    finally {
        if ([IO.File]::Exists($temporaryPath)) {
            [IO.File]::Delete($temporaryPath)
        }
    }
}

function Get-TextSha256 {
    param([Parameter(Mandatory = $true)] [AllowEmptyString()] [string] $Value)

    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($script:utf8.GetBytes($Value)))
}

function Get-BoundedFileSha256 {
    param(
        [Parameter(Mandatory = $true)] [IO.FileInfo] $File,
        [Parameter(Mandatory = $true)] [string] $RelativePath
    )

    if ($File.Length -gt $script:maximumFileBytes) {
        Add-ValidationError -Code 'file_exceeds_512_mib' -Path $RelativePath
        return $null
    }
    return (Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Test-SecretValuePresent {
    param($Value)

    if ($null -eq $Value) {
        return $false
    }
    if ($Value -is [string]) {
        return -not [string]::IsNullOrWhiteSpace([string]$Value)
    }
    return $true
}

function Test-JsonContainsCredential {
    param($Value)

    if ($Value -is [Collections.IDictionary]) {
        foreach ($keyObject in $Value.Keys) {
            $key = [string]$keyObject
            $child = $Value[$keyObject]
            if ($key -match '^(?i:password|pwd|client[_-]?secret|access[_-]?key|api[_-]?key|token)$' -and
                (Test-SecretValuePresent -Value $child)) {
                return $true
            }
            if ($child -is [string] -and $key -match '(?i:connectionstrings?|connectionstring)$' -and
                [regex]::IsMatch(
                    [string]$child,
                    '(?i)(?:^|;)\s*(?:password|pwd)\s*=\s*[^;\s][^;]*')) {
                return $true
            }
            if (Test-JsonContainsCredential -Value $child) {
                return $true
            }
        }
        return $false
    }

    if ($Value -is [Collections.IEnumerable] -and $Value -isnot [string]) {
        foreach ($item in $Value) {
            if (Test-JsonContainsCredential -Value $item) {
                return $true
            }
        }
    }
    return $false
}

function Assert-NoReparseItem {
    param(
        [Parameter(Mandatory = $true)] [IO.FileSystemInfo] $Item,
        [Parameter(Mandatory = $true)] [string] $RelativePath
    )

    if (($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        Add-ValidationError -Code 'reparse_point_forbidden' -Path $RelativePath
        return $false
    }
    return $true
}

function Invoke-SourcePreflight {
    param([Parameter(Mandatory = $true)] [string] $ResolvedSourceRoot)

    $configurationFiles = New-Object 'System.Collections.Generic.List[object]'
    $sourceItem = Get-Item -LiteralPath $ResolvedSourceRoot -Force
    if (-not (Assert-NoReparseItem -Item $sourceItem -RelativePath '.')) {
        return $configurationFiles.ToArray()
    }

    foreach ($file in @(Get-ChildItem -LiteralPath $ResolvedSourceRoot -Filter 'appsettings*.json' -File -Force |
            Sort-Object Name)) {
        $relative = $file.Name
        if (-not (Assert-NoReparseItem -Item $file -RelativePath $relative)) {
            continue
        }
        if ($file.Length -gt $script:maximumConfigurationBytes) {
            Add-ValidationError -Code 'configuration_exceeds_1_mib' -Path $relative
            continue
        }

        $hash = Get-BoundedFileSha256 -File $file -RelativePath $relative
        $configurationFiles.Add([ordered]@{
            path = $relative
            bytes = $file.Length
            sha256 = $hash
        })
        try {
            $document = [IO.File]::ReadAllText($file.FullName, $script:utf8) |
                ConvertFrom-Json -AsHashtable -Depth 30
            if (Test-JsonContainsCredential -Value $document) {
                Add-ValidationError -Code 'tracked_credential_present' -Path $relative
            }
        }
        catch {
            Add-ValidationError -Code 'configuration_json_invalid' -Path $relative
        }
    }

    return $configurationFiles.ToArray()
}

function Get-TreeItems {
    param(
        [Parameter(Mandatory = $true)] [string] $Root,
        [Parameter(Mandatory = $true)] [AllowEmptyString()] [string] $DisplayPrefix
    )

    $items = New-Object 'System.Collections.Generic.List[object]'
    foreach ($item in @(Get-ChildItem -LiteralPath $Root -Recurse -Force | Sort-Object FullName)) {
        $relative = Get-RelativeFilePath -Root $Root -Path $item.FullName
        $display = if ([string]::IsNullOrWhiteSpace($DisplayPrefix)) {
            $relative
        }
        else {
            "$DisplayPrefix/$relative"
        }
        if (-not (Assert-NoReparseItem -Item $item -RelativePath $display)) {
            continue
        }
        $items.Add($item)
    }
    return $items.ToArray()
}

function Find-ByteSequence {
    param(
        [Parameter(Mandatory = $true)] [IO.FileStream] $Stream,
        [Parameter(Mandatory = $true)] [byte[]] $Needle
    )

    $streamPosition = $Stream.Position
    try {
        $Stream.Position = 0
        $chunkBytes = 65536
        $buffer = [byte[]]::new($chunkBytes + $Needle.Length - 1)
        $needleText = [Text.Encoding]::Latin1.GetString($Needle)
        $carry = 0
        $absoluteRead = [long]0
        while ($true) {
            $read = $Stream.Read($buffer, $carry, $chunkBytes)
            if ($read -eq 0) {
                return [long]-1
            }
            $total = $carry + $read
            $chunkText = [Text.Encoding]::Latin1.GetString($buffer, 0, $total)
            $index = $chunkText.IndexOf($needleText, [StringComparison]::Ordinal)
            if ($index -ge 0) {
                return $absoluteRead - $carry + $index
            }
            $carry = [Math]::Min($Needle.Length - 1, $total)
            [Array]::Copy($buffer, $total - $carry, $buffer, 0, $carry)
            $absoluteRead += $read
        }
    }
    finally {
        $Stream.Position = $streamPosition
    }
}

function Test-JetExecutable {
    param([Parameter(Mandatory = $true)] [IO.FileInfo] $Executable)

    if ($Executable.Length -gt $script:maximumFileBytes) {
        return
    }

    $stream = [IO.File]::Open(
        $Executable.FullName,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    $reader = [IO.BinaryReader]::new($stream, [Text.Encoding]::UTF8, $true)
    try {
        if ($stream.Length -lt 64 -or $reader.ReadByte() -ne 0x4D -or $reader.ReadByte() -ne 0x5A) {
            Add-ValidationError -Code 'jet_executable_not_mz' -Path 'JET.exe'
            return
        }

        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or $peOffset + 26 -gt $stream.Length) {
            Add-ValidationError -Code 'jet_executable_pe_header_invalid' -Path 'JET.exe'
            return
        }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            Add-ValidationError -Code 'jet_executable_pe_signature_invalid' -Path 'JET.exe'
            return
        }
        $machine = $reader.ReadUInt16()
        $stream.Position = $peOffset + 24
        $optionalMagic = $reader.ReadUInt16()
        if ($machine -ne 0x8664 -or $optionalMagic -ne 0x020B) {
            Add-ValidationError -Code 'jet_executable_not_x64_pe32plus' -Path 'JET.exe'
        }

        $bundleSignature = [byte[]](
            0x8B, 0x12, 0x02, 0xB9, 0x6A, 0x61, 0x20, 0x38,
            0x72, 0x7B, 0x93, 0x02, 0x14, 0xD7, 0xA0, 0x32,
            0x13, 0xF5, 0xB9, 0xE6, 0xEF, 0xAE, 0x33, 0x18,
            0xEE, 0x3B, 0x2D, 0xCE, 0x24, 0xB3, 0x6A, 0xAE)
        $signaturePosition = Find-ByteSequence -Stream $stream -Needle $bundleSignature
        $bundleHeaderOffset = [long]0
        if ($signaturePosition -ge 8) {
            $stream.Position = $signaturePosition - 8
            $bundleHeaderOffset = $reader.ReadInt64()
        }
        if ($bundleHeaderOffset -le 0 -or $bundleHeaderOffset -ge $stream.Length) {
            Add-ValidationError -Code 'jet_executable_not_single_file_bundle' -Path 'JET.exe'
        }

        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($Executable.FullName)
        if ($version.FileDescription -cne 'JET' -or
            $version.ProductName -cne 'JET' -or
            $version.OriginalFilename -cne 'JET.dll') {
            Add-ValidationError -Code 'jet_executable_identity_invalid' -Path 'JET.exe'
        }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function New-PreflightManifest {
    param(
        [Parameter(Mandatory = $true)] [object[]] $ConfigurationFiles,
        [Parameter(Mandatory = $true)] [DateTime] $StartedUtc
    )

    $completedUtc = [DateTime]::UtcNow
    return [ordered]@{
        schemaVersion = 1
        mode = 'source_preflight'
        ok = $script:validationErrors.Count -eq 0
        status = if ($script:validationErrors.Count -eq 0) { 'passed' } else { 'failed' }
        startedUtc = $StartedUtc.ToString('O')
        completedUtc = $completedUtc.ToString('O')
        durationSeconds = [Math]::Round(($completedUtc - $StartedUtc).TotalSeconds, 3)
        configurationFiles = $ConfigurationFiles
        errors = $script:validationErrors.ToArray()
        privateData = [ordered]@{ pathInspected = $false }
    }
}

function Write-EnvelopeAndExit {
    param(
        [Parameter(Mandatory = $true)] $Manifest,
        [Parameter(Mandatory = $true)] [int] $ExitCode
    )

    Write-AtomicJson -Path $ManifestPath -Value $Manifest
    [Console]::Out.WriteLine(([ordered]@{
            schemaVersion = 1
            ok = $Manifest.ok
            status = $Manifest.status
            exitCode = $ExitCode
            manifest = [IO.Path]::GetFileName($ManifestPath)
            privateData = [ordered]@{ pathInspected = $false }
        } | ConvertTo-Json -Depth 8 -Compress))
    exit $ExitCode
}

$startedUtc = [DateTime]::UtcNow
try {
    $resolvedSourceRoot = (Resolve-Path -LiteralPath $SourceRoot -ErrorAction Stop).Path
    if (-not (Test-Path -LiteralPath $resolvedSourceRoot -PathType Container)) {
        throw [ArgumentException]::new('SourceRoot must be a directory.')
    }
    $configurationFiles = @(Invoke-SourcePreflight -ResolvedSourceRoot $resolvedSourceRoot)
    if ($Mode -ceq 'SourcePreflight' -or $validationErrors.Count -gt 0) {
        $preflightManifest = New-PreflightManifest `
            -ConfigurationFiles $configurationFiles `
            -StartedUtc $startedUtc
        $preflightExit = if ($preflightManifest.ok) { 0 } else { 1 }
        Write-EnvelopeAndExit -Manifest $preflightManifest -ExitCode $preflightExit
    }

    foreach ($requiredValue in @($CandidatePath, $OwnedRoot, $RegistryPath)) {
        if ([string]::IsNullOrWhiteSpace($requiredValue)) {
            throw [ArgumentException]::new('Verify requires CandidatePath, OwnedRoot, and RegistryPath.')
        }
    }
    $resolvedOwnedRoot = (Resolve-Path -LiteralPath $OwnedRoot -ErrorAction Stop).Path
    $resolvedCandidate = (Resolve-Path -LiteralPath $CandidatePath -ErrorAction Stop).Path
    if (-not (Test-DescendantPath -Root $resolvedOwnedRoot -Candidate $resolvedCandidate)) {
        throw [ArgumentException]::new('CandidatePath must be inside OwnedRoot.')
    }
    $registry = Get-Content -LiteralPath $RegistryPath -Raw -Encoding utf8 |
        ConvertFrom-Json -AsHashtable -Depth 20
    $templateRelativePaths = @($registry.packageSettings.templateRelativePaths)
    if ($templateRelativePaths.Count -eq 0) {
        throw [InvalidDataException]::new('Package template inventory is empty.')
    }

    $expected = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    $expected['JET.exe'] = [pscustomobject]@{ Hash = $null; Bytes = $null }

    foreach ($file in @(Get-ChildItem -LiteralPath $resolvedSourceRoot -Filter 'appsettings*.json' -File -Force |
            Sort-Object Name)) {
        $hash = Get-BoundedFileSha256 -File $file -RelativePath $file.Name
        $expected[$file.Name] = [pscustomobject]@{ Hash = $hash; Bytes = $file.Length }
    }

    $wwwroot = Join-Path $resolvedSourceRoot 'wwwroot'
    if (-not (Test-Path -LiteralPath $wwwroot -PathType Container)) {
        Add-ValidationError -Code 'source_wwwroot_missing' -Path 'wwwroot'
    }
    else {
        foreach ($file in @(Get-TreeItems -Root $wwwroot -DisplayPrefix 'wwwroot' |
                Where-Object { $_ -is [IO.FileInfo] })) {
            $relative = "wwwroot/$(Get-RelativeFilePath -Root $wwwroot -Path $file.FullName)"
            $hash = Get-BoundedFileSha256 -File $file -RelativePath $relative
            $expected[$relative] = [pscustomobject]@{ Hash = $hash; Bytes = $file.Length }
        }
    }

    foreach ($templateRelativePathObject in $templateRelativePaths) {
        $templateRelativePath = ([string]$templateRelativePathObject).Replace(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar)
        if ([IO.Path]::IsPathRooted($templateRelativePath) -or
            $templateRelativePath -eq '..' -or
            $templateRelativePath.StartsWith('../', [StringComparison]::Ordinal)) {
            throw [InvalidDataException]::new('Package template paths must be source-relative.')
        }
        $sourceTemplate = Join-Path $resolvedSourceRoot $templateRelativePath
        if (-not (Test-Path -LiteralPath $sourceTemplate -PathType Leaf)) {
            Add-ValidationError -Code 'source_template_missing' -Path $templateRelativePath
            continue
        }
        $file = Get-Item -LiteralPath $sourceTemplate -Force
        if (-not (Assert-NoReparseItem -Item $file -RelativePath $templateRelativePath)) {
            continue
        }
        $hash = Get-BoundedFileSha256 -File $file -RelativePath $templateRelativePath
        $expected[$templateRelativePath] = [pscustomobject]@{ Hash = $hash; Bytes = $file.Length }
    }

    $candidateRootItem = Get-Item -LiteralPath $resolvedCandidate -Force
    $actual = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    $candidateFiles = if (Assert-NoReparseItem -Item $candidateRootItem -RelativePath '.') {
        @(Get-TreeItems -Root $resolvedCandidate -DisplayPrefix '' |
            Where-Object { $_ -is [IO.FileInfo] })
    }
    else {
        @()
    }
    foreach ($file in $candidateFiles) {
        $relative = Get-RelativeFilePath -Root $resolvedCandidate -Path $file.FullName
        $extension = [IO.Path]::GetExtension($relative).ToLowerInvariant()
        $fileName = [IO.Path]::GetFileName($relative).ToLowerInvariant()
        $lowerRelative = $relative.ToLowerInvariant()
        if (@('.dll', '.pdb', '.xml') -ccontains $extension) {
            Add-ValidationError -Code 'loose_binary_or_symbols_forbidden' -Path $relative
        }
        $runtimeDirectory = $lowerRelative -match '(^|/)(projects|\.jet-locks?)(/|$)'
        $runtimeName = @('project.json', 'report-artifacts.json') -ccontains $fileName
        $runtimeExtension = @('.db', '.sqlite', '.sqlite3', '.duckdb', '.wal', '.shm', '.journal', '.tmp') -ccontains $extension
        $runtimeSuffix = $lowerRelative -match '(-wal|-shm|-journal)$'
        $unexpectedWorkbook = $extension -ceq '.xlsx' -and -not $expected.ContainsKey($relative)
        if ($runtimeDirectory -or $runtimeName -or $runtimeExtension -or $runtimeSuffix -or $unexpectedWorkbook) {
            Add-ValidationError -Code 'runtime_data_forbidden' -Path $relative
        }
        $hash = Get-BoundedFileSha256 -File $file -RelativePath $relative
        $actual[$relative] = [pscustomobject]@{ Hash = $hash; Bytes = $file.Length; File = $file }
    }

    foreach ($expectedPath in @($expected.Keys | Sort-Object)) {
        if (-not $actual.ContainsKey($expectedPath)) {
            Add-ValidationError -Code 'package_file_missing' -Path $expectedPath
            continue
        }
        if ($null -ne $expected[$expectedPath].Hash -and
            [string]$actual[$expectedPath].Hash -cne [string]$expected[$expectedPath].Hash) {
            Add-ValidationError -Code 'package_file_hash_mismatch' -Path $expectedPath
        }
    }
    foreach ($actualPath in @($actual.Keys | Sort-Object)) {
        if (-not $expected.ContainsKey($actualPath)) {
            Add-ValidationError -Code 'package_file_extra' -Path $actualPath
        }
    }
    if ($actual.ContainsKey('JET.exe')) {
        Test-JetExecutable -Executable $actual['JET.exe'].File
    }

    $fileManifest = @($actual.Keys | Sort-Object | ForEach-Object {
            [ordered]@{
                path = $_
                bytes = $actual[$_].Bytes
                sha256 = $actual[$_].Hash
            }
        })
    $inventoryLines = @($fileManifest | ForEach-Object { "$($_.path)`t$($_.bytes)`t$($_.sha256)" })
    $completedUtc = [DateTime]::UtcNow
    $manifest = [ordered]@{
        schemaVersion = 1
        mode = 'package_verification'
        ok = $validationErrors.Count -eq 0
        status = if ($validationErrors.Count -eq 0) { 'passed' } else { 'failed' }
        startedUtc = $startedUtc.ToString('O')
        completedUtc = $completedUtc.ToString('O')
        durationSeconds = [Math]::Round(($completedUtc - $startedUtc).TotalSeconds, 3)
        counters = [ordered]@{
            expectedFiles = $expected.Count
            actualFiles = $actual.Count
        }
        inventorySha256 = Get-TextSha256 -Value (($inventoryLines -join "`n") + "`n")
        executableSha256 = if ($actual.ContainsKey('JET.exe')) { $actual['JET.exe'].Hash } else { $null }
        files = $fileManifest
        errors = $validationErrors.ToArray()
        privateData = [ordered]@{ pathInspected = $false }
    }
    $manifestExit = if ($manifest.ok) { 0 } else { 1 }
    Write-EnvelopeAndExit -Manifest $manifest -ExitCode $manifestExit
}
catch {
    $completedUtc = [DateTime]::UtcNow
    $manifest = [ordered]@{
        schemaVersion = 1
        mode = $Mode.ToLowerInvariant()
        ok = $false
        status = 'infrastructure_error'
        startedUtc = $startedUtc.ToString('O')
        completedUtc = $completedUtc.ToString('O')
        durationSeconds = [Math]::Round(($completedUtc - $startedUtc).TotalSeconds, 3)
        errors = @([ordered]@{
                code = 'package_verifier_exception'
                type = $_.Exception.GetType().Name
            })
        privateData = [ordered]@{ pathInspected = $false }
    }
    Write-EnvelopeAndExit -Manifest $manifest -ExitCode 2
}
