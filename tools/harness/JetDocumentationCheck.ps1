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

function ConvertTo-HeadingSlug {
    # 依 GitHub 的標題錨點規則：去掉行內格式，轉小寫，移除標點與符號（保留字母、數字、組合符號、空白、連字號與底線），
    # 空白換成連字號。中文字是字母類別，會原樣保留。
    param([Parameter(Mandatory = $true)] [string] $Heading)

    $text = $Heading.Trim()
    $text = [regex]::Replace($text, '`([^`]*)`', '$1')
    $text = [regex]::Replace($text, '\*\*([^*]*)\*\*', '$1')
    $text = [regex]::Replace($text, '\[([^\]]*)\]\([^)]*\)', '$1')
    $text = $text.ToLowerInvariant()
    $text = [regex]::Replace($text, '[^\p{L}\p{N}\p{M}\s_-]', '')
    $text = [regex]::Replace($text, '\s', '-')
    return $text
}

function Get-HeadingSlugs {
    param([Parameter(Mandatory = $true)] [string] $Text)

    $slugs = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $counts = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
    $inFence = $false
    foreach ($line in ($Text -split "\r?\n")) {
        if ([regex]::IsMatch($line, '^\s*(```|~~~)')) { $inFence = -not $inFence; continue }
        if ($inFence) { continue }
        $match = [regex]::Match($line, '^\s{0,3}#{1,6}\s+(.*?)\s*#*\s*$')
        if (-not $match.Success) { continue }
        $slug = ConvertTo-HeadingSlug -Heading $match.Groups[1].Value
        if ($counts.ContainsKey($slug)) {
            $counts[$slug] = $counts[$slug] + 1
            [void]$slugs.Add("$slug-$($counts[$slug])")
        }
        else {
            $counts[$slug] = 0
            [void]$slugs.Add($slug)
        }
    }
    return $slugs
}

function Get-MarkdownLinks {
    # 只收 Markdown 內文的 [文字](目標) 連結；程式碼區塊與行內程式碼不算。回傳目標與行號。
    param([Parameter(Mandatory = $true)] [string] $Text)

    $links = New-Object 'System.Collections.Generic.List[object]'
    $lines = $Text -split "\r?\n"
    $inFence = $false
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $line = $lines[$index]
        if ([regex]::IsMatch($line, '^\s*(```|~~~)')) { $inFence = -not $inFence; continue }
        if ($inFence) { continue }
        $prose = [regex]::Replace($line, '`[^`]*`', '')
        foreach ($match in [regex]::Matches($prose, '\]\(\s*<?([^)\s>]+)>?(?:\s+"[^"]*")?\s*\)')) {
            $links.Add([pscustomobject]@{ Target = $match.Groups[1].Value; Line = $index + 1 })
        }
    }
    return $links
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

    # 必要指向：某份文件必須提到某個路徑，而且那個路徑要存在。這取代了過去用整句文字當必要字串的做法，
    # 文件搬移或改寫時只要指向還在就不會誤判。
    $requiredReferences = if ($policy.ContainsKey('requiredReferences')) { @($policy.requiredReferences) } else { @() }
    foreach ($reference in $requiredReferences) {
        if ([string]::IsNullOrWhiteSpace([string]$reference.id) -or
            [string]::IsNullOrWhiteSpace([string]$reference.file) -or
            [string]::IsNullOrWhiteSpace([string]$reference.target) -or
            @($files) -cnotcontains [string]$reference.file) {
            throw [InvalidDataException]::new(
                'Required references need an identifier, a declared file, and a repository-relative target.')
        }
    }
    $linkSettings = if ($policy.ContainsKey('links') -and $null -ne $policy.links) { $policy.links } else { @{} }
    $checkAnchors = if ($linkSettings.ContainsKey('checkAnchors')) { [bool]$linkSettings.checkAnchors } else { $true }
    $skipPrefixes = if ($linkSettings.ContainsKey('skipPrefixes')) { @($linkSettings.skipPrefixes) } else { @('http://', 'https://', 'mailto:') }
    $linkSkipFiles = if ($linkSettings.ContainsKey('skipFiles')) { @($linkSettings.skipFiles | ForEach-Object { ([string]$_).Replace('\', '/') }) } else { @() }

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

    # 相對連結與錨點：目標檔案要存在；指向 Markdown 標題的錨點要對得到實際標題。
    $linksChecked = 0
    $anchorsChecked = 0
    $headingCache = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($relativePath in @($fileText.Keys)) {
        if ($linkSkipFiles -ccontains $relativePath) { continue }
        if (-not $relativePath.EndsWith('.md', [StringComparison]::OrdinalIgnoreCase)) { continue }
        $sourceFull = [IO.Path]::GetFullPath((Join-Path $repositoryFull $relativePath))
        $sourceDirectory = Split-Path -Parent $sourceFull
        foreach ($link in (Get-MarkdownLinks -Text $fileText[$relativePath])) {
            $target = [string]$link.Target
            $skip = $false
            foreach ($prefix in $skipPrefixes) {
                if ($target.StartsWith([string]$prefix, [StringComparison]::OrdinalIgnoreCase)) { $skip = $true; break }
            }
            if ($skip) { continue }
            $anchor = $null
            $hashIndex = $target.IndexOf('#')
            if ($hashIndex -ge 0) {
                $anchor = $target.Substring($hashIndex + 1)
                $target = $target.Substring(0, $hashIndex)
            }
            $target = [Uri]::UnescapeDataString($target)
            $targetFull = if ([string]::IsNullOrEmpty($target)) { $sourceFull } else { [IO.Path]::GetFullPath((Join-Path $sourceDirectory $target)) }
            $linksChecked++
            if (-not (Test-DescendantPath -Root $repositoryFull -Candidate $targetFull) -or
                -not (Test-Path -LiteralPath $targetFull)) {
                $errors.Add([ordered]@{
                    code = 'link_target_missing'
                    file = $relativePath
                    line = $link.Line
                    target = $target
                })
                continue
            }
            if (-not $checkAnchors -or [string]::IsNullOrEmpty($anchor)) { continue }
            if (-not $targetFull.EndsWith('.md', [StringComparison]::OrdinalIgnoreCase)) { continue }
            $anchorsChecked++
            if (-not $headingCache.ContainsKey($targetFull)) {
                $headingCache[$targetFull] = Get-HeadingSlugs -Text ([IO.File]::ReadAllText($targetFull, $utf8))
            }
            $wanted = [Uri]::UnescapeDataString($anchor).ToLowerInvariant()
            if (-not $headingCache[$targetFull].Contains($wanted)) {
                $errors.Add([ordered]@{
                    code = 'link_anchor_missing'
                    file = $relativePath
                    line = $link.Line
                    target = "$target#$anchor"
                })
            }
        }
    }

    foreach ($reference in $requiredReferences) {
        $referenceFile = [string]$reference.file
        if (-not $fileText.ContainsKey($referenceFile)) { continue }
        $targetRelative = ([string]$reference.target).Replace('\', '/').TrimEnd('/')
        $targetFull = [IO.Path]::GetFullPath((Join-Path $repositoryFull $targetRelative))
        $sourceDirectory = Split-Path -Parent ([IO.Path]::GetFullPath((Join-Path $repositoryFull $referenceFile)))
        $relativeFromSource = [IO.Path]::GetRelativePath($sourceDirectory, $targetFull).Replace('\', '/')
        $mentioned = $fileText[$referenceFile].Contains($targetRelative, [StringComparison]::Ordinal) -or
            $fileText[$referenceFile].Contains($relativeFromSource, [StringComparison]::Ordinal)
        if (-not $mentioned) {
            foreach ($link in (Get-MarkdownLinks -Text $fileText[$referenceFile])) {
                $candidate = ([string]$link.Target -split '#')[0]
                if ([string]::IsNullOrEmpty($candidate)) { continue }
                $candidateFull = [IO.Path]::GetFullPath((Join-Path $sourceDirectory ([Uri]::UnescapeDataString($candidate))))
                if ([string]::Equals($candidateFull.TrimEnd('\', '/'), $targetFull.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)) {
                    $mentioned = $true
                    break
                }
            }
        }
        if (-not $mentioned -or -not (Test-Path -LiteralPath $targetFull)) {
            $errors.Add([ordered]@{
                code = 'required_reference_missing'
                file = $referenceFile
                line = $null
                ruleId = [string]$reference.id
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
            linksChecked = $linksChecked
            anchorsChecked = $anchorsChecked
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
