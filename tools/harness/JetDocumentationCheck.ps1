#requires -Version 7.4

using namespace System.IO

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
    [Parameter(Mandatory = $true)] [string] $ReportPath,
    [Parameter(Mandatory = $true)] [string] $PolicyPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false, $true)
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8

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

function Assert-NoReparsePoint {
    param(
        [Parameter(Mandatory = $true)] [string] $Root,
        [Parameter(Mandatory = $true)] [string] $Candidate
    )

    if (-not (Test-DescendantPath -Root $Root -Candidate $Candidate)) {
        throw [ArgumentException]::new('Documentation paths must remain inside the selected repository root.')
    }

    $current = [IO.Path]::GetFullPath($Root)
    $relative = [IO.Path]::GetRelativePath($current, [IO.Path]::GetFullPath($Candidate))
    foreach ($segment in $relative.Split(
            @([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar),
            [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $segment
        if (-not (Test-Path -LiteralPath $current)) {
            continue
        }
        $item = Get-Item -LiteralPath $current -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw [InvalidDataException]::new('Documentation paths cannot cross a reparse point.')
        }
    }
}

function Write-Report {
    param([Parameter(Mandatory = $true)] $Value)

    $parent = Split-Path -Parent $script:reportFull
    [void][IO.Directory]::CreateDirectory($parent)
    [IO.File]::WriteAllText(
        $script:reportFull,
        ($Value | ConvertTo-Json -Depth 12) + "`n",
        $script:utf8)
    [Console]::Out.WriteLine(($Value | ConvertTo-Json -Depth 12 -Compress))
}

$repositoryFull = [IO.Path]::GetFullPath($RepositoryRoot)
$reportFull = [IO.Path]::GetFullPath($ReportPath)
$policyFull = [IO.Path]::GetFullPath($PolicyPath)
$startedUtc = [DateTime]::UtcNow

try {
    if (-not (Test-Path -LiteralPath $repositoryFull -PathType Container)) {
        throw [DirectoryNotFoundException]::new('The selected repository root does not exist.')
    }
    Assert-NoReparsePoint -Root $repositoryFull -Candidate $reportFull
    Assert-NoReparsePoint -Root $repositoryFull -Candidate $policyFull
    if (-not (Test-Path -LiteralPath $policyFull -PathType Leaf)) {
        throw [FileNotFoundException]::new('The documentation check file is missing.')
    }

    $policy = Get-Content -LiteralPath $policyFull -Raw -Encoding utf8 |
        ConvertFrom-Json -AsHashtable -Depth 12
    if ([int]$policy.schemaVersion -ne 2) {
        throw [InvalidDataException]::new('The documentation check version is not supported.')
    }
    foreach ($required in @('files', 'forbiddenClaims', 'styleWarnings', 'requiredMarkers', 'manualReview')) {
        if (-not $policy.ContainsKey($required)) {
            throw [InvalidDataException]::new("The documentation check is missing '$required'.")
        }
    }

    $files = @($policy.files)
    $forbiddenClaims = @($policy.forbiddenClaims)
    $styleWarnings = @($policy.styleWarnings)
    $requiredMarkers = @($policy.requiredMarkers)
    $manualReview = @($policy.manualReview)
    # 樣式規則分兩種：styleWarnings 比對固定片語；stylePatterns 用正規表示式抓「符號代句」這類寫法。
    # 兩種都只產生 warning。stylePatterns 不看程式碼區塊與行內程式碼，那裡的符號是程式，不是句子。
    $stylePatterns = if ($policy.ContainsKey('stylePatterns')) { @($policy.stylePatterns) } else { @() }
    if ($files.Count -eq 0 -or @($files | Sort-Object -Unique).Count -ne $files.Count) {
        throw [InvalidDataException]::new('The documentation file list must be non-empty and unique.')
    }
    if ($forbiddenClaims.Count -eq 0 -or $styleWarnings.Count -eq 0 -or
        $requiredMarkers.Count -eq 0 -or $manualReview.Count -eq 0) {
        throw [InvalidDataException]::new(
            'The documentation check needs factual failures, style warnings, required markers, and human review items.')
    }

    $ruleIds = @($forbiddenClaims + $styleWarnings + $stylePatterns + $requiredMarkers |
        ForEach-Object { [string]$_.id })
    if (@($ruleIds | Sort-Object -Unique).Count -ne $ruleIds.Count) {
        throw [InvalidDataException]::new('Documentation rule identifiers must be unique.')
    }
    foreach ($rule in @($forbiddenClaims + $styleWarnings)) {
        if ([string]::IsNullOrWhiteSpace([string]$rule.id) -or
            [string]::IsNullOrWhiteSpace([string]$rule.text)) {
            throw [InvalidDataException]::new('Documentation phrase rules need both an identifier and text.')
        }
    }
    foreach ($marker in $requiredMarkers) {
        if ([string]::IsNullOrWhiteSpace([string]$marker.id) -or
            [string]::IsNullOrWhiteSpace([string]$marker.file) -or
            [string]::IsNullOrWhiteSpace([string]$marker.text) -or
            @($files) -cnotcontains [string]$marker.file) {
            throw [InvalidDataException]::new(
                'Required markers need an identifier, a declared file, and expected text.')
        }
    }

    $compiledPatterns = New-Object 'System.Collections.Generic.List[object]'
    foreach ($rule in $stylePatterns) {
        $id = if ($rule.ContainsKey('id')) { [string]$rule.id } else { '' }
        $patternText = if ($rule.ContainsKey('pattern')) { [string]$rule.pattern } else { '' }
        $hint = if ($rule.ContainsKey('hint')) { [string]$rule.hint } else { '' }
        if ([string]::IsNullOrWhiteSpace($id) -or [string]::IsNullOrWhiteSpace($patternText) -or
            [string]::IsNullOrWhiteSpace($hint)) {
            throw [InvalidDataException]::new('Documentation pattern rules need an identifier, a pattern, and a hint.')
        }
        try {
            $regex = [Text.RegularExpressions.Regex]::new(
                $patternText,
                [Text.RegularExpressions.RegexOptions]::CultureInvariant,
                [TimeSpan]::FromSeconds(1))
        }
        catch {
            throw [InvalidDataException]::new("Documentation pattern rule '$id' is not a valid regular expression.")
        }
        $compiledPatterns.Add([pscustomobject]@{ Id = $id; Regex = $regex; Hint = $hint })
    }

    $errors = New-Object 'System.Collections.Generic.List[object]'
    $warnings = New-Object 'System.Collections.Generic.List[object]'
    $fileText = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $checkedFiles = 0
    foreach ($relativePathValue in $files) {
        $relativePath = ([string]$relativePathValue).Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($relativePath) -or [IO.Path]::IsPathRooted($relativePath) -or
            $relativePath -match '(^|/)\.\.(/|$)') {
            throw [InvalidDataException]::new('Documentation files must use safe repository-relative paths.')
        }

        $fullPath = [IO.Path]::GetFullPath((Join-Path $repositoryFull $relativePath))
        Assert-NoReparsePoint -Root $repositoryFull -Candidate $fullPath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            $errors.Add([ordered]@{
                code = 'document_missing'
                file = $relativePath
                line = $null
                phraseId = $null
            })
            continue
        }

        $text = [IO.File]::ReadAllText($fullPath, $utf8)
        $fileText[$relativePath] = $text
        $lines = $text -split "\r?\n"
        $inFence = $false
        for ($lineIndex = 0; $lineIndex -lt $lines.Count; $lineIndex++) {
            $isFenceLine = [regex]::IsMatch($lines[$lineIndex], '^\s*(```|~~~)')
            if ($isFenceLine) { $inFence = -not $inFence }
            foreach ($rule in $forbiddenClaims) {
                if ($lines[$lineIndex].Contains([string]$rule.text, [StringComparison]::Ordinal)) {
                    $errors.Add([ordered]@{
                        code = 'forbidden_claim'
                        file = $relativePath
                        line = $lineIndex + 1
                        ruleId = [string]$rule.id
                    })
                }
            }
            foreach ($rule in $styleWarnings) {
                if ($lines[$lineIndex].Contains([string]$rule.text, [StringComparison]::Ordinal)) {
                    $warnings.Add([ordered]@{
                        code = 'style_warning'
                        file = $relativePath
                        line = $lineIndex + 1
                        ruleId = [string]$rule.id
                    })
                }
            }
            if ($compiledPatterns.Count -gt 0 -and -not $isFenceLine -and -not $inFence) {
                $prose = [regex]::Replace($lines[$lineIndex], '`[^`]*`', '')
                foreach ($pattern in $compiledPatterns) {
                    if ($pattern.Regex.IsMatch($prose)) {
                        $warnings.Add([ordered]@{
                            code = 'style_pattern'
                            file = $relativePath
                            line = $lineIndex + 1
                            ruleId = $pattern.Id
                            hint = $pattern.Hint
                        })
                    }
                }
            }
        }
        $checkedFiles++
    }

    foreach ($marker in $requiredMarkers) {
        $markerFile = [string]$marker.file
        if (-not $fileText.ContainsKey($markerFile)) {
            continue
        }
        if (-not $fileText[$markerFile].Contains([string]$marker.text, [StringComparison]::Ordinal)) {
            $errors.Add([ordered]@{
                code = 'required_marker_missing'
                file = $markerFile
                line = $null
                ruleId = [string]$marker.id
            })
        }
    }

    $completedUtc = [DateTime]::UtcNow
    $status = if ($errors.Count -eq 0) { 'passed' } else { 'failed' }
    $report = [ordered]@{
        schemaVersion = 2
        status = $status
        startedUtc = $startedUtc.ToString('O')
        completedUtc = $completedUtc.ToString('O')
        counters = [ordered]@{
            filesDeclared = $files.Count
            filesChecked = $checkedFiles
            errors = $errors.Count
            warnings = $warnings.Count
        }
        errors = $errors.ToArray()
        warnings = $warnings.ToArray()
        manualReview = [ordered]@{
            required = $true
            items = $manualReview
        }
        privateData = [ordered]@{ pathInspected = $false }
    }
    Write-Report -Value $report
    if ($status -ceq 'passed') { exit 0 } else { exit 1 }
}
catch {
    $completedUtc = [DateTime]::UtcNow
    $report = [ordered]@{
        schemaVersion = 2
        status = 'infrastructure_error'
        startedUtc = $startedUtc.ToString('O')
        completedUtc = $completedUtc.ToString('O')
        counters = [ordered]@{ filesDeclared = 0; filesChecked = 0; errors = 1; warnings = 0 }
        errors = @([ordered]@{
            code = 'documentation_check_invalid'
            type = $_.Exception.GetType().Name
        })
        warnings = @()
        manualReview = [ordered]@{ required = $true; items = @() }
        privateData = [ordered]@{ pathInspected = $false }
    }
    try {
        Write-Report -Value $report
    }
    catch {
        [Console]::Out.WriteLine(($report | ConvertTo-Json -Depth 12 -Compress))
    }
    exit 4
}
