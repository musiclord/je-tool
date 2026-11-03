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

function Find-SizeRule {
    # 依序比對，第一條符合的規則決定上限：「*」符合所有檔，結尾是「/」的符合整個目錄，其餘要完全相同。
    param([object[]] $Rules, [string] $RelativePath)

    foreach ($rule in $Rules) {
        foreach ($pattern in @($rule.match)) {
            $text = ([string]$pattern).Replace('\', '/')
            if ($text -ceq '*' -or $text -ceq $RelativePath -or
                ($text.EndsWith('/') -and $RelativePath.StartsWith($text, [StringComparison]::Ordinal))) {
                return $rule
            }
        }
    }
    return $null
}

function Split-MarkdownRow {
    param([Parameter(Mandatory = $true)] [string] $Line)

    return @([regex]::Split($Line.Trim().Trim('|'), '(?<!\\)\|') | ForEach-Object { $_.Trim() })
}

function Get-MarkdownSectionRows {
    # 回傳某個「## 標題」底下表格的資料列，表頭與分隔列不算；每列帶行號與儲存格。找不到這一節時回傳 $null。
    param(
        [Parameter(Mandatory = $true)] [string] $Text,
        [Parameter(Mandatory = $true)] [string] $Section
    )

    $rows = New-Object 'System.Collections.Generic.List[object]'
    $found = $false
    $inSection = $false
    $inTable = $false
    $inFence = $false
    $lines = $Text -split "\r?\n"
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $line = $lines[$index]
        if ([regex]::IsMatch($line, '^\s*(```|~~~)')) { $inFence = -not $inFence; $inTable = $false; continue }
        if ($inFence) { continue }
        $heading = [regex]::Match($line, '^##\s+(.+?)\s*$')
        if ($heading.Success) {
            $inSection = $heading.Groups[1].Value -ceq $Section
            if ($inSection) { $found = $true }
            $inTable = $false
            continue
        }
        if (-not $inSection) { continue }
        if (-not $line.StartsWith('|')) { $inTable = $false; continue }
        if ([regex]::IsMatch($line, '^\|[\s:\-|]+\|\s*$')) { continue }
        if (-not $inTable) { $inTable = $true; continue }
        $rows.Add([pscustomobject]@{ Line = $index + 1; Cells = @(Split-MarkdownRow -Line $line) })
    }
    if (-not $found) { return $null }
    return , $rows
}

function Get-QuotedNames {
    # 儲存格裡用反引號或「」框起來的名稱，依出現順序回傳。
    param([AllowEmptyString()] [string] $Cell)

    return @([regex]::Matches($Cell, '`([^`]+)`|「([^」]+)」') | ForEach-Object {
            if ($_.Groups[1].Success) { $_.Groups[1].Value } else { $_.Groups[2].Value }
        })
}

function Get-LineNumber {
    param([Parameter(Mandatory = $true)] [string] $Text, [Parameter(Mandatory = $true)] [int] $Index)

    return [regex]::Matches($Text.Substring(0, $Index), "`n").Count + 1
}

function Assert-RepositoryRelativePaths {
    # 設定裡要掃的資料夾或檔案只能寫儲存庫內的相對路徑。
    param([object[]] $Paths, [Parameter(Mandatory = $true)] [string] $Label)

    foreach ($path in $Paths) {
        $text = ([string]$path).Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($text) -or [IO.Path]::IsPathRooted($text) -or $text -match '(^|/)\.\.(/|$)') {
            throw [InvalidDataException]::new("$Label paths must use safe repository-relative paths.")
        }
    }
}

function Get-ScanFiles {
    # 從各資料夾往下找指定副檔名的檔案；skipDirectories 列的資料夾與重新解析點不進去。回傳依序排好的完整路徑。
    param(
        [Parameter(Mandatory = $true)] [string] $Root,
        [object[]] $Folders,
        [object[]] $Extensions,
        [object[]] $SkipDirectories
    )

    $wanted = @($Extensions | ForEach-Object { ([string]$_).ToLowerInvariant() })
    $pending = [Collections.Generic.Stack[string]]::new()
    foreach ($folder in $Folders) {
        $folderFull = [IO.Path]::GetFullPath((Join-Path $Root ([string]$folder)))
        Assert-NoReparsePoint -Root $Root -Candidate $folderFull
        if (Test-Path -LiteralPath $folderFull -PathType Container) { $pending.Push($folderFull) }
    }
    $found = New-Object 'System.Collections.Generic.List[string]'
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($child in [IO.Directory]::EnumerateDirectories($directory)) {
            $childInfo = [IO.DirectoryInfo]::new($child)
            if (($childInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
            if (@($SkipDirectories) -contains $childInfo.Name) { continue }
            $pending.Push($child)
        }
        foreach ($path in [IO.Directory]::EnumerateFiles($directory)) {
            if ($wanted -ccontains [IO.Path]::GetExtension($path).ToLowerInvariant()) { $found.Add($path) }
        }
    }
    $found.Sort([StringComparer]::Ordinal)
    return , $found
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

    # 文件長度上限：依文件類型設上限，數字以 KiB（1,024 位元組）計；exempt 的規則表示這類文件不設上限。
    # severity 是 warning 時只提醒，改成 error 之後超過上限就失敗。
    $sizeRules = @()
    $sizeSeverity = 'warning'
    if ($policy.ContainsKey('sizeLimits') -and $null -ne $policy.sizeLimits) {
        $sizeSettings = $policy.sizeLimits
        $sizeSeverity = if ($sizeSettings.ContainsKey('severity')) { [string]$sizeSettings.severity } else { '' }
        $sizeRules = if ($sizeSettings.ContainsKey('rules')) { @($sizeSettings.rules) } else { @() }
        if ($sizeSeverity -cnotin @('warning', 'error') -or $sizeRules.Count -eq 0) {
            throw [InvalidDataException]::new('Document size limits need a severity of warning or error and at least one rule.')
        }
        foreach ($rule in $sizeRules) {
            $exempt = $rule.ContainsKey('exempt') -and [bool]$rule.exempt
            $valid = $rule.ContainsKey('id') -and -not [string]::IsNullOrWhiteSpace([string]$rule.id) -and
                $rule.ContainsKey('match') -and @($rule.match).Count -gt 0 -and
                ($exempt -or ($rule.ContainsKey('maxKiB') -and [double]$rule.maxKiB -gt 0))
            if (-not $valid) {
                throw [InvalidDataException]::new(
                    'Document size rules need an identifier, paths to match, and a positive limit or an exemption.')
            }
        }
    }

    # 裁定總表的落實位置：指定欄位裡用行內程式碼寫的檔案都要存在。
    $rulingLocations = $null
    if ($policy.ContainsKey('rulingLocations') -and $null -ne $policy.rulingLocations) {
        $rulingLocations = $policy.rulingLocations
        foreach ($key in @('id', 'file', 'column')) {
            if (-not $rulingLocations.ContainsKey($key) -or [string]::IsNullOrWhiteSpace([string]$rulingLocations[$key])) {
                throw [InvalidDataException]::new('Ruling locations need an identifier, a declared file, and a column name.')
            }
        }
        if (@($files) -cnotcontains [string]$rulingLocations.file) {
            throw [InvalidDataException]::new('Ruling locations need an identifier, a declared file, and a column name.')
        }
    }

    # 程式與工具裡寫的裁定編號：每個「裁定 編號」都要是總表裡現行的裁定。已被取代的裁定與 Agent 定的做法也列在總表，
    # 但不是現行的裁定，程式不該掛它們的編號；這兩節的標題寫在 excludedSections。
    $rulingReferences = $null
    if ($policy.ContainsKey('rulingReferences') -and $null -ne $policy.rulingReferences) {
        $rulingReferences = $policy.rulingReferences
        $valid = $rulingReferences.ContainsKey('id') -and -not [string]::IsNullOrWhiteSpace([string]$rulingReferences.id) -and
            $rulingReferences.ContainsKey('file') -and @($files) -ccontains [string]$rulingReferences.file -and
            $rulingReferences.ContainsKey('roots') -and @($rulingReferences.roots).Count -gt 0 -and
            $rulingReferences.ContainsKey('extensions') -and @($rulingReferences.extensions).Count -gt 0
        if (-not $valid) {
            throw [InvalidDataException]::new(
                'Ruling references need an identifier, a declared file, folders to scan, and file extensions.')
        }
        Assert-RepositoryRelativePaths -Paths @($rulingReferences.roots) -Label 'Ruling reference folder'
    }

    # 名詞表列為不再使用的名稱，產品程式裡不能再出現。名稱與比對方式寫在名詞表那一節的表格，這裡只指定要掃的資料夾。
    $retiredNames = $null
    if ($policy.ContainsKey('retiredNames') -and $null -ne $policy.retiredNames) {
        $retiredNames = $policy.retiredNames
        $valid = $retiredNames.ContainsKey('id') -and -not [string]::IsNullOrWhiteSpace([string]$retiredNames.id) -and
            $retiredNames.ContainsKey('file') -and @($files) -ccontains [string]$retiredNames.file -and
            $retiredNames.ContainsKey('section') -and -not [string]::IsNullOrWhiteSpace([string]$retiredNames.section) -and
            $retiredNames.ContainsKey('roots') -and @($retiredNames.roots).Count -gt 0 -and
            $retiredNames.ContainsKey('extensions') -and @($retiredNames.extensions).Count -gt 0
        if (-not $valid) {
            throw [InvalidDataException]::new(
                'Retired names need an identifier, a declared file, a section title, folders to scan, and file extensions.')
        }
        Assert-RepositoryRelativePaths -Paths @($retiredNames.roots) -Label 'Retired name folder'
    }

    # 名詞表「資料庫表名」一節要和案件資料庫實際建立的表一致。sources 是寫建表語句的程式檔。
    $tableNames = $null
    if ($policy.ContainsKey('tableNames') -and $null -ne $policy.tableNames) {
        $tableNames = $policy.tableNames
        $valid = $tableNames.ContainsKey('id') -and -not [string]::IsNullOrWhiteSpace([string]$tableNames.id) -and
            $tableNames.ContainsKey('file') -and @($files) -ccontains [string]$tableNames.file -and
            $tableNames.ContainsKey('section') -and -not [string]::IsNullOrWhiteSpace([string]$tableNames.section) -and
            $tableNames.ContainsKey('sources') -and @($tableNames.sources).Count -gt 0
        if (-not $valid) {
            throw [InvalidDataException]::new(
                'Table names need an identifier, a declared file, a section title, and the source files that create the tables.')
        }
        Assert-RepositoryRelativePaths -Paths @($tableNames.sources) -Label 'Table name source'
    }

    $ruleIds = @($forbiddenClaims + $styleWarnings + $stylePatterns + $requiredMarkers + $sizeRules |
        ForEach-Object { [string]$_.id })
    if ($null -ne $rulingLocations) { $ruleIds += [string]$rulingLocations.id }
    if ($null -ne $rulingReferences) { $ruleIds += [string]$rulingReferences.id }
    if ($null -ne $retiredNames) { $ruleIds += [string]$retiredNames.id }
    if ($null -ne $tableNames) { $ruleIds += [string]$tableNames.id }
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
        $sizeRule = if ($sizeRules.Count -gt 0) { Find-SizeRule -Rules $sizeRules -RelativePath $relativePath } else { $null }
        if ($null -ne $sizeRule -and -not ($sizeRule.ContainsKey('exempt') -and [bool]$sizeRule.exempt)) {
            $bytes = ([IO.FileInfo]::new($fullPath)).Length
            $limitBytes = [long][Math]::Floor([double]$sizeRule.maxKiB * 1024)
            if ($bytes -gt $limitBytes) {
                $finding = [ordered]@{
                    code = 'document_too_long'
                    file = $relativePath
                    line = $null
                    ruleId = [string]$sizeRule.id
                    bytes = $bytes
                    limitBytes = $limitBytes
                }
                if ($sizeSeverity -ceq 'error') { $errors.Add($finding) } else { $warnings.Add($finding) }
            }
        }
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

    # 總表每個表格的落實位置欄，逐一核對行內程式碼寫的路徑；檔案改名或刪除時，總表要一起更新。
    $rulingLocationsChecked = 0
    if ($null -ne $rulingLocations -and $fileText.ContainsKey([string]$rulingLocations.file)) {
        $rulingsFile = [string]$rulingLocations.file
        $rulingLines = $fileText[$rulingsFile] -split "\r?\n"
        $column = -1
        $inTable = $false
        $inFence = $false
        for ($index = 0; $index -lt $rulingLines.Count; $index++) {
            $line = $rulingLines[$index]
            if ([regex]::IsMatch($line, '^\s*(```|~~~)')) { $inFence = -not $inFence; $inTable = $false; continue }
            if ($inFence -or -not $line.StartsWith('|')) { $inTable = $false; continue }
            if ([regex]::IsMatch($line, '^\|[\s:\-|]+\|\s*$')) { continue }
            $cells = @(Split-MarkdownRow -Line $line)
            if (-not $inTable) {
                $inTable = $true
                $column = [Array]::IndexOf([string[]]$cells, [string]$rulingLocations.column)
                continue
            }
            if ($column -lt 0 -or $column -ge $cells.Count) { continue }
            foreach ($match in [regex]::Matches($cells[$column], '`([^`]+)`')) {
                $target = $match.Groups[1].Value.Replace('\', '/')
                $rulingLocationsChecked++
                $targetFull = if ([IO.Path]::IsPathRooted($target)) { $target } else { [IO.Path]::GetFullPath((Join-Path $repositoryFull $target)) }
                if (-not (Test-DescendantPath -Root $repositoryFull -Candidate $targetFull) -or
                    -not (Test-Path -LiteralPath $targetFull)) {
                    $errors.Add([ordered]@{
                        code = 'ruling_location_missing'
                        file = $rulingsFile
                        line = $index + 1
                        ruleId = [string]$rulingLocations.id
                        target = $target
                    })
                }
            }
        }
    }

    # 程式與工具裡的「裁定 編號」與「ruling 編號」，逐一核對是總表裡現行的裁定。建置輸出這類資料夾寫在 skipDirectories，不掃。
    $rulingReferencesChecked = 0
    if ($null -ne $rulingReferences -and $fileText.ContainsKey([string]$rulingReferences.file)) {
        $referenceRulingsFile = [string]$rulingReferences.file
        $excludedSections = if ($rulingReferences.ContainsKey('excludedSections')) { @($rulingReferences.excludedSections) } else { @() }
        $currentRulings = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $otherRulings = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $sections = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $section = ''
        foreach ($line in ($fileText[$referenceRulingsFile] -split "\r?\n")) {
            $heading = [regex]::Match($line, '^##\s+(.+?)\s*$')
            if ($heading.Success) {
                $section = $heading.Groups[1].Value
                [void]$sections.Add($section)
                continue
            }
            $row = [regex]::Match($line, '^\|\s*(\d{4}-\d{2}-\d{2}-\d{2})\s*\|')
            if (-not $row.Success) { continue }
            if ($excludedSections -ccontains $section) { [void]$otherRulings.Add($row.Groups[1].Value) }
            else { [void]$currentRulings.Add($row.Groups[1].Value) }
        }
        foreach ($excluded in $excludedSections) {
            if (-not $sections.Contains([string]$excluded)) {
                $errors.Add([ordered]@{
                    code = 'ruling_section_missing'
                    file = $referenceRulingsFile
                    line = $null
                    ruleId = [string]$rulingReferences.id
                    target = [string]$excluded
                })
            }
        }

        $skipDirectories = if ($rulingReferences.ContainsKey('skipDirectories')) { @($rulingReferences.skipDirectories) } else { @() }
        $referencePattern = [Text.RegularExpressions.Regex]::new(
            '(?:裁定|[Rr]uling)\s*(\d{4}-\d{2}-\d{2}-\d{2})',
            [Text.RegularExpressions.RegexOptions]::CultureInvariant,
            [TimeSpan]::FromSeconds(1))
        $lenientUtf8 = [Text.UTF8Encoding]::new($false, $false)
        $scanFiles = Get-ScanFiles -Root $repositoryFull -Folders @($rulingReferences.roots) `
            -Extensions @($rulingReferences.extensions) -SkipDirectories $skipDirectories
        foreach ($path in $scanFiles) {
            $relative = [IO.Path]::GetRelativePath($repositoryFull, $path).Replace('\', '/')
            $scanLines = [IO.File]::ReadAllText($path, $lenientUtf8) -split "\r?\n"
            for ($index = 0; $index -lt $scanLines.Count; $index++) {
                foreach ($match in $referencePattern.Matches($scanLines[$index])) {
                    $rulingReferencesChecked++
                    $rulingId = $match.Groups[1].Value
                    if ($currentRulings.Contains($rulingId)) { continue }
                    $errors.Add([ordered]@{
                        code = if ($otherRulings.Contains($rulingId)) { 'ruling_reference_not_current' } else { 'ruling_reference_missing' }
                        file = $relative
                        line = $index + 1
                        ruleId = [string]$rulingReferences.id
                        target = $rulingId
                    })
                }
            }
        }
    }

    # 名詞表列為不再使用的名稱，逐一在產品程式裡找。同一種比對方式的名稱併成一個正規表示式，每個檔只讀一次；
    # 現在的名稱包含舊名稱時，出現在現在名稱裡的那一段不算。
    $retiredNamesChecked = 0
    if ($null -ne $retiredNames -and $fileText.ContainsKey([string]$retiredNames.file)) {
        $retiredFile = [string]$retiredNames.file
        $retiredRows = Get-MarkdownSectionRows -Text $fileText[$retiredFile] -Section ([string]$retiredNames.section)
        if ($null -eq $retiredRows) {
            $errors.Add([ordered]@{
                code = 'retired_name_section_missing'
                file = $retiredFile
                line = $null
                ruleId = [string]$retiredNames.id
                target = [string]$retiredNames.section
            })
        }
        else {
            $retired = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
            foreach ($row in $retiredRows) {
                $oldNames = @(if ($row.Cells.Count -ge 4) { Get-QuotedNames -Cell $row.Cells[0] })
                $mode = if ($row.Cells.Count -ge 4) { $row.Cells[3] } else { '' }
                if ($oldNames.Count -ne 1 -or @('整個詞', '字串中出現', '不自動比對') -cnotcontains $mode -or
                    $retired.ContainsKey($oldNames[0])) {
                    $errors.Add([ordered]@{
                        code = 'retired_name_row_invalid'
                        file = $retiredFile
                        line = $row.Line
                        ruleId = [string]$retiredNames.id
                        target = $row.Cells[0]
                    })
                    continue
                }
                if ($mode -ceq '不自動比對') { continue }
                $oldName = $oldNames[0]
                $retired[$oldName] = [pscustomobject]@{
                    WholeWord = $mode -ceq '整個詞'
                    Containing = @(Get-QuotedNames -Cell $row.Cells[2] | Where-Object {
                            $_.Length -gt $oldName.Length -and $_.Contains($oldName, [StringComparison]::Ordinal) })
                }
            }
            $retiredNamesChecked = $retired.Count
            $retiredPatterns = New-Object 'System.Collections.Generic.List[System.Text.RegularExpressions.Regex]'
            foreach ($wholeWord in @($true, $false)) {
                $group = @($retired.Keys | Where-Object { $retired[$_].WholeWord -eq $wholeWord } |
                    Sort-Object -Property @{ Expression = { $_.Length }; Descending = $true })
                if ($group.Count -eq 0) { continue }
                $alternation = ($group | ForEach-Object { [regex]::Escape($_) }) -join '|'
                $pattern = if ($wholeWord) { "(?<![A-Za-z0-9_])(?:$alternation)(?![A-Za-z0-9_])" } else { "(?:$alternation)" }
                $retiredPatterns.Add([Text.RegularExpressions.Regex]::new(
                        $pattern,
                        [Text.RegularExpressions.RegexOptions]::CultureInvariant,
                        [TimeSpan]::FromSeconds(5)))
            }
            $retiredSkip = if ($retiredNames.ContainsKey('skipDirectories')) { @($retiredNames.skipDirectories) } else { @() }
            $lenientUtf8 = [Text.UTF8Encoding]::new($false, $false)
            $retiredScanFiles = Get-ScanFiles -Root $repositoryFull -Folders @($retiredNames.roots) `
                -Extensions @($retiredNames.extensions) -SkipDirectories $retiredSkip
            foreach ($path in $retiredScanFiles) {
                $relative = [IO.Path]::GetRelativePath($repositoryFull, $path).Replace('\', '/')
                $sourceText = [IO.File]::ReadAllText($path, $lenientUtf8)
                foreach ($regex in $retiredPatterns) {
                    foreach ($match in $regex.Matches($sourceText)) {
                        $covered = $false
                        foreach ($longer in $retired[$match.Value].Containing) {
                            $start = [Math]::Max(0, $match.Index + $match.Length - $longer.Length)
                            $at = $sourceText.IndexOf($longer, $start, [StringComparison]::Ordinal)
                            if ($at -ge 0 -and $at -le $match.Index) { $covered = $true; break }
                        }
                        if ($covered) { continue }
                        $errors.Add([ordered]@{
                            code = 'retired_name_present'
                            file = $relative
                            line = Get-LineNumber -Text $sourceText -Index $match.Index
                            ruleId = [string]$retiredNames.id
                            target = $match.Value
                        })
                    }
                }
            }
        }
    }

    # 名詞表「資料庫表名」一節列的表，要和建表語句建立的表一致：建了沒列，或列了卻沒建，都失敗。
    $tableNamesChecked = 0
    if ($null -ne $tableNames -and $fileText.ContainsKey([string]$tableNames.file)) {
        $tableFile = [string]$tableNames.file
        $tableRows = Get-MarkdownSectionRows -Text $fileText[$tableFile] -Section ([string]$tableNames.section)
        if ($null -eq $tableRows) {
            $errors.Add([ordered]@{
                code = 'table_section_missing'
                file = $tableFile
                line = $null
                ruleId = [string]$tableNames.id
                target = [string]$tableNames.section
            })
        }
        else {
            $listedTables = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
            foreach ($row in $tableRows) {
                if ($row.Cells.Count -eq 0) { continue }
                foreach ($name in @(Get-QuotedNames -Cell $row.Cells[0])) {
                    if (-not $listedTables.ContainsKey($name)) { $listedTables[$name] = $row.Line }
                }
            }
            $createdTables = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
            $createPattern = [Text.RegularExpressions.Regex]::new(
                'CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:\{s\}\.|\[?[A-Za-z_][A-Za-z0-9_]*\]?\.)?\[?([A-Za-z_][A-Za-z0-9_]*)\]?',
                [Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [Text.RegularExpressions.RegexOptions]::CultureInvariant,
                [TimeSpan]::FromSeconds(5))
            $lenientUtf8 = [Text.UTF8Encoding]::new($false, $false)
            foreach ($source in @($tableNames.sources)) {
                $sourceRelative = ([string]$source).Replace('\', '/')
                $sourceFull = [IO.Path]::GetFullPath((Join-Path $repositoryFull $sourceRelative))
                Assert-NoReparsePoint -Root $repositoryFull -Candidate $sourceFull
                if (-not (Test-Path -LiteralPath $sourceFull -PathType Leaf)) {
                    $errors.Add([ordered]@{
                        code = 'table_source_missing'
                        file = $sourceRelative
                        line = $null
                        ruleId = [string]$tableNames.id
                        target = $sourceRelative
                    })
                    continue
                }
                $sourceText = [IO.File]::ReadAllText($sourceFull, $lenientUtf8)
                foreach ($match in $createPattern.Matches($sourceText)) {
                    $tableName = $match.Groups[1].Value
                    if ($createdTables.ContainsKey($tableName)) { continue }
                    $createdTables[$tableName] = [pscustomobject]@{
                        File = $sourceRelative
                        Line = Get-LineNumber -Text $sourceText -Index $match.Index
                    }
                }
            }
            $tableNamesChecked = $createdTables.Count
            foreach ($tableName in @($createdTables.Keys | Sort-Object)) {
                if ($listedTables.ContainsKey($tableName)) { continue }
                $errors.Add([ordered]@{
                    code = 'table_name_not_listed'
                    file = $createdTables[$tableName].File
                    line = $createdTables[$tableName].Line
                    ruleId = [string]$tableNames.id
                    target = $tableName
                })
            }
            foreach ($tableName in @($listedTables.Keys | Sort-Object)) {
                if ($createdTables.ContainsKey($tableName)) { continue }
                $errors.Add([ordered]@{
                    code = 'listed_table_missing'
                    file = $tableFile
                    line = $listedTables[$tableName]
                    ruleId = [string]$tableNames.id
                    target = $tableName
                })
            }
        }
    }

    $architectureReport = $null
    if ($policy.ContainsKey('architectureManifest')) {
        $architectureChecker = Join-Path $repositoryFull 'tools/harness/architecture/Check-Architecture.ps1'
        $architectureTests = Join-Path $repositoryFull 'tools/tests/architecture-evidence.tests.ps1'
        Assert-NoReparsePoint -Root $repositoryFull -Candidate $architectureChecker
        Assert-NoReparsePoint -Root $repositoryFull -Candidate $architectureTests
        . $architectureChecker
        . $architectureTests
        $architectureReport = Invoke-JetArchitectureCheck -RepositoryRoot $repositoryFull -ManifestPath ([string]$policy.architectureManifest)
        $architectureReport['syntheticCases'] = Test-JetArchitectureRules
        foreach ($problem in $architectureReport.errors) { $errors.Add($problem) }
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
            rulingLocationsChecked = $rulingLocationsChecked
            rulingReferencesChecked = $rulingReferencesChecked
            retiredNamesChecked = $retiredNamesChecked
            tableNamesChecked = $tableNamesChecked
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
    if ($null -ne $architectureReport) { $report['architecture'] = $architectureReport }
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
