using namespace System.IO

# 由 JetHarness.psm1 點號載入。判讀子步驟的結果：讀取測試結果檔、核對略過政策、整理失敗訊息、遮蔽測試證據裡的敏感值，以及辨認套件來源無法連線。

function Test-JetNuGetSourceUnavailable {
    param(
        [Parameter(Mandatory = $true)] [string[]] $Paths
    )

    foreach ($path in $Paths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            continue
        }

        $content = [IO.File]::ReadAllText($path)
        if ([regex]::IsMatch($content, '(?<![A-Z0-9])NU1301(?![A-Z0-9])', [Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Get-JetTextSha256 {
    param([Parameter(Mandatory = $true)] [AllowEmptyString()] [string] $Value)

    $bytes = $script:Utf8.GetBytes($Value)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
}

function Normalize-JetSkipReason {
    param([AllowNull()] [string] $Reason)

    if ([string]::IsNullOrWhiteSpace($Reason)) {
        return ''
    }

    $normalized = [Text.RegularExpressions.Regex]::Replace($Reason.Trim(), '\s+', ' ')
    foreach ($pattern in @(
            '^(?i:skip(?: reason)?)\s*:\s*',
            '^(?i:test skipped(?: because)?)\s*:\s*')) {
        $normalized = [Text.RegularExpressions.Regex]::Replace($normalized, $pattern, '')
    }
    return $normalized.Trim()
}

function Protect-JetSensitiveTestEvidence {
    param(
        [Parameter(Mandatory = $true)] [string] $ResultsDirectory,
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [string[]] $SensitiveValues
    )

    if ($SensitiveValues.Count -eq 0) {
        return [ordered]@{ applied = $false; files = 0 }
    }

    $files = New-Object 'System.Collections.Generic.List[IO.FileInfo]'
    foreach ($item in @(Get-ChildItem -LiteralPath $ResultsDirectory -Recurse -Force | Sort-Object FullName)) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw [InvalidDataException]::new('Sensitive test evidence cannot contain a reparse point.')
        }
        if ($item -is [IO.FileInfo]) {
            $files.Add($item)
        }
    }

    foreach ($file in $files) {
        if ($file.Length -gt 67108864) {
            throw [InvalidDataException]::new('Sensitive test evidence exceeds the 64 MiB sanitation limit.')
        }
        if (@('.trx', '.xml', '.json', '.log', '.txt') -cnotcontains $file.Extension.ToLowerInvariant()) {
            throw [InvalidDataException]::new('Sensitive test evidence contains an unsupported attachment type.')
        }

        $reader = [IO.StreamReader]::new($file.FullName, [Text.Encoding]::UTF8, $true)
        try {
            $content = $reader.ReadToEnd()
            $encoding = $reader.CurrentEncoding
        }
        finally {
            $reader.Dispose()
        }
        foreach ($sensitiveValue in $SensitiveValues) {
            if (-not [string]::IsNullOrWhiteSpace($sensitiveValue)) {
                $content = $content.Replace(
                    $sensitiveValue,
                    '[sensitive]',
                    [StringComparison]::OrdinalIgnoreCase)
            }
        }

        $temporaryPath = "$($file.FullName).sanitize-$([Guid]::NewGuid().ToString('N'))"
        try {
            [IO.File]::WriteAllText($temporaryPath, $content, $encoding)
            [IO.File]::Move($temporaryPath, $file.FullName, $true)
        }
        finally {
            if ([IO.File]::Exists($temporaryPath)) {
                [IO.File]::Delete($temporaryPath)
            }
        }
    }

    return [ordered]@{ applied = $true; files = $files.Count }
}

function Read-JetSkipPolicy {
    param(
        [Parameter(Mandatory = $true)] [string] $FileName,
        [Parameter(Mandatory = $true)] [string] $PolicyName
    )

    if ($FileName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]+\.json$') {
        throw [InvalidDataException]::new('Skip-policy file name is invalid.')
    }
    $path = Join-Path $PSScriptRoot $FileName
    $policy = Get-Content -LiteralPath $path -Raw -Encoding utf8 |
        ConvertFrom-Json -AsHashtable -Depth 20

    if ([int]$policy.schemaVersion -ne 1 -or -not $policy.ContainsKey('capabilityGroups')) {
        throw [InvalidDataException]::new("$PolicyName skip policy has an unsupported schema.")
    }

    $groupNames = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    $memberNames = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($group in @($policy.capabilityGroups)) {
        $name = [string]$group.name
        if ([string]::IsNullOrWhiteSpace($name) -or -not $groupNames.Add($name)) {
            throw [InvalidDataException]::new("$PolicyName skip-policy capability names must be non-empty and unique.")
        }
        if (-not [bool]$group.uniformOutcome) {
            throw [InvalidDataException]::new("$PolicyName capability '$name' must require a uniform outcome.")
        }
        if ([string]$group.skipReason.normalization -cne 'trimCollapseWhitespaceV1' -or
            [string]$group.skipReason.normalizedSha256 -notmatch '^[0-9A-F]{64}$') {
            throw [InvalidDataException]::new("$PolicyName capability '$name' has an invalid skip-reason policy.")
        }
        $members = @($group.members)
        if ($members.Count -eq 0) {
            throw [InvalidDataException]::new("$PolicyName capability '$name' has no test members.")
        }
        foreach ($member in $members) {
            $memberName = [string]$member
            if ($memberName -notmatch '^[A-Za-z_][A-Za-z0-9_.+]+$' -or -not $memberNames.Add($memberName)) {
                throw [InvalidDataException]::new("$PolicyName skip-policy members must be valid and globally unique.")
            }
        }
    }

    return $policy
}

function Format-JetFailureMessage {
    param(
        [AllowNull()] [string] $Message,
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot
    )

    # 失敗訊息只保留一行、最多 240 字，並把儲存庫根目錄與使用者目錄換成固定標記。
    # TRX 在進到這裡之前已由 Protect-JetSensitiveTestEvidence 遮蔽本次明示的敏感值。
    if ([string]::IsNullOrWhiteSpace($Message)) {
        return $null
    }
    $text = [Text.RegularExpressions.Regex]::Replace($Message.Trim(), '\s+', ' ')
    $replacements = @(
        @([IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/'), '[repository]'),
        @([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile), '[user-profile]'))
    foreach ($pair in $replacements) {
        $value = [string]$pair[0]
        if ([string]::IsNullOrWhiteSpace($value)) {
            continue
        }
        $text = $text.Replace($value, [string]$pair[1], [StringComparison]::OrdinalIgnoreCase)
        $text = $text.Replace($value.Replace('\', '/'), [string]$pair[1], [StringComparison]::OrdinalIgnoreCase)
    }
    if ($text.Length -gt 240) {
        $text = $text.Substring(0, 240) + '...'
    }
    return $text
}

function Read-JetTrxResult {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $RunDirectory,
        [Parameter(Mandatory = $true)] [string] $TrxPath,
        [Parameter(Mandatory = $true)] [string] $SummaryPath,
        [switch] $WithholdFailureMessages
    )

    if (-not (Test-JetDescendantPath -Root $RunDirectory -Candidate $TrxPath) -or
        -not (Test-JetDescendantPath -Root $RunDirectory -Candidate $SummaryPath)) {
        throw [InvalidDataException]::new('Test evidence must remain inside its run directory.')
    }
    if (-not (Test-Path -LiteralPath $TrxPath -PathType Leaf)) {
        throw [FileNotFoundException]::new('The test process did not produce its TRX report.')
    }
    $trxInfo = Get-Item -LiteralPath $TrxPath
    if ($trxInfo.Length -le 0 -or $trxInfo.Length -gt 67108864) {
        throw [InvalidDataException]::new('TRX size is empty or exceeds the 64 MiB safety limit.')
    }

    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 67108864
    $document = [Xml.XmlDocument]::new()
    $document.XmlResolver = $null
    $reader = $null
    try {
        $reader = [Xml.XmlReader]::Create($TrxPath, $settings)
        $document.Load($reader)
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
    }

    $namespace = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespace.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $definitions = @{}
    foreach ($unitTest in @($document.SelectNodes('//t:TestDefinitions/t:UnitTest', $namespace))) {
        $id = [string]$unitTest.Attributes['id']?.Value
        $method = $unitTest.SelectSingleNode('t:TestMethod', $namespace)
        if (-not [string]::IsNullOrWhiteSpace($id) -and $null -ne $method) {
            $definitions[$id] = [pscustomobject]@{
                ClassName = [string]$method.Attributes['className']?.Value
                MethodName = [string]$method.Attributes['name']?.Value
            }
        }
    }

    $rawRecords = New-Object 'System.Collections.Generic.List[object]'
    foreach ($result in @($document.SelectNodes('//t:Results/t:UnitTestResult', $namespace))) {
        $displayName = [string]$result.Attributes['testName']?.Value
        $outcomeValue = [string]$result.Attributes['outcome']?.Value
        $testId = [string]$result.Attributes['testId']?.Value
        $definition = if ($definitions.ContainsKey($testId)) { $definitions[$testId] } else { $null }
        $className = if ($null -ne $definition) { [string]$definition.ClassName } else { '' }
        $methodName = if ($null -ne $definition) { [string]$definition.MethodName } else { '' }
        if ([string]::IsNullOrWhiteSpace($methodName)) {
            $methodName = $displayName
        }
        $fqn = if ([string]::IsNullOrWhiteSpace($className)) { $methodName } else { "$className.$methodName" }
        $outcome = switch -Regex ($outcomeValue) {
            '^(Passed|Completed)$' { 'Passed'; break }
            '^Failed$' { 'Failed'; break }
            default { 'Skipped' }
        }

        $skipReasonHash = $null
        if ($outcome -ceq 'Skipped') {
            $message = [string]$result.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $namespace)?.InnerText
            if ([string]::IsNullOrWhiteSpace($message)) {
                $message = [string]$result.SelectSingleNode('t:Output/t:StdOut', $namespace)?.InnerText
            }
            if ([string]::IsNullOrWhiteSpace($message)) {
                $messages = @($result.SelectNodes('t:Output/t:TextMessages/t:TextMessage', $namespace) |
                    ForEach-Object InnerText)
                $message = [string]($messages -join ' ')
            }
            $skipReasonHash = Get-JetTextSha256 -Value (Normalize-JetSkipReason -Reason $message)
        }

        # 失敗訊息不進 summary 與 records，只用來組出收據裡的第一個失敗；私人案件路線會整個保留不寫。
        $failureMessage = $null
        if ($outcome -ceq 'Failed') {
            $failureMessage = [string]$result.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $namespace)?.InnerText
        }

        $rawRecords.Add([pscustomobject]@{
            Fqn = $fqn
            DisplayName = $displayName
            Outcome = $outcome
            SkipReasonSha256 = $skipReasonHash
            FailureMessage = $failureMessage
        })
    }

    $records = New-Object 'System.Collections.Generic.List[object]'
    $failureMessages = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($group in @($rawRecords | Group-Object { "$($_.Fqn)`u{001f}$($_.DisplayName)" } | Sort-Object Name)) {
        $ordinal = 0
        foreach ($item in @($group.Group | Sort-Object Outcome, Fqn, DisplayName)) {
            $ordinal++
            $identity = "$($item.Fqn)`u{001f}$($item.DisplayName)`u{001f}$ordinal"
            if ($item.Outcome -ceq 'Failed' -and -not [string]::IsNullOrWhiteSpace([string]$item.FailureMessage)) {
                $failureMessages[$identity] = [string]$item.FailureMessage
            }
            $records.Add([pscustomobject]@{
                identity = $identity
                fqn = $item.Fqn
                displayName = $item.DisplayName
                ordinal = $ordinal
                outcome = $item.Outcome
                skipReasonSha256 = $item.SkipReasonSha256
            })
        }
    }

    $recordArray = $records.ToArray()
    $passed = @($recordArray | Where-Object outcome -CEQ 'Passed').Count
    $failed = @($recordArray | Where-Object outcome -CEQ 'Failed').Count
    $skipped = @($recordArray | Where-Object outcome -CEQ 'Skipped').Count
    $outcomeLines = @($recordArray | ForEach-Object { "$($_.identity)`t$($_.outcome)" } | Sort-Object)
    $summary = [ordered]@{
        schemaVersion = 1
        counters = [ordered]@{
            total = $recordArray.Count
            executed = $passed + $failed
            passed = $passed
            failed = $failed
            skipped = $skipped
        }
        inventorySha256 = Get-JetTextSha256 -Value ((@($recordArray.identity | Sort-Object) -join "`n") + "`n")
        outcomeSha256 = Get-JetTextSha256 -Value (($outcomeLines -join "`n") + "`n")
        trxSha256 = (Get-FileHash -LiteralPath $TrxPath -Algorithm SHA256).Hash.ToUpperInvariant()
        records = $recordArray
        privateData = [ordered]@{ pathInspected = $false }
    }
    Write-JetAtomicJson -Path $SummaryPath -Value $summary

    # 收據直接寫出依名稱排序的第一個失敗測試與它的訊息，讓讀收據的人或 agent 不必再翻 TRX。
    $firstFailure = $null
    $firstFailedRecord = @($recordArray | Where-Object outcome -CEQ 'Failed' | Select-Object -First 1)
    if ($firstFailedRecord.Count -gt 0) {
        $firstIdentity = [string]$firstFailedRecord[0].identity
        $firstMessage = if ($WithholdFailureMessages -or -not $failureMessages.ContainsKey($firstIdentity)) {
            $null
        }
        else {
            Format-JetFailureMessage -Message $failureMessages[$firstIdentity] -RepositoryRoot $RepositoryRoot
        }
        $firstFailure = [ordered]@{
            fqn = [string]$firstFailedRecord[0].fqn
            message = $firstMessage
            messageWithheld = [bool]$WithholdFailureMessages
        }
    }

    return [pscustomobject]@{
        Summary = $summary
        Records = $recordArray
        FirstFailure = $firstFailure
        Receipt = [ordered]@{
            counters = $summary.counters
            inventorySha256 = $summary.inventorySha256
            outcomeSha256 = $summary.outcomeSha256
            trxSha256 = $summary.trxSha256
            trx = Get-JetRelativePath -RepositoryRoot $RepositoryRoot -Path $TrxPath
            summary = Get-JetRelativePath -RepositoryRoot $RepositoryRoot -Path $SummaryPath
            firstFailure = $firstFailure
            failedTests = @($recordArray | Where-Object outcome -CEQ 'Failed' | Select-Object -First 20 -ExpandProperty fqn)
            skippedTests = @($recordArray | Where-Object outcome -CEQ 'Skipped' | Select-Object -First 20 | ForEach-Object {
                    [ordered]@{ fqn = $_.fqn; reasonSha256 = $_.skipReasonSha256 }
                })
        }
    }
}

function Test-JetSkipPolicy {
    param(
        [Parameter(Mandatory = $true)] [object[]] $Records,
        [Parameter(Mandatory = $true)] $Policy
    )

    $allowedMembers = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($group in @($Policy.capabilityGroups)) {
        foreach ($member in @($group.members)) {
            [void]$allowedMembers.Add([string]$member)
        }
    }

    $unexpectedSkips = @($Records |
        Where-Object { $_.outcome -ceq 'Skipped' -and -not $allowedMembers.Contains([string]$_.fqn) } |
        Select-Object -ExpandProperty fqn -Unique)
    $capabilities = New-Object 'System.Collections.Generic.List[object]'
    $policyErrors = New-Object 'System.Collections.Generic.List[string]'
    if ($unexpectedSkips.Count -gt 0) {
        $policyErrors.Add('unexpected_skip')
    }

    foreach ($group in @($Policy.capabilityGroups)) {
        $memberRecords = New-Object 'System.Collections.Generic.List[object]'
        foreach ($member in @($group.members)) {
            $matches = @($Records | Where-Object fqn -CEQ ([string]$member))
            if ($matches.Count -ne 1) {
                $policyErrors.Add("capability_member_count:$($group.name)")
            }
            foreach ($match in $matches) {
                $memberRecords.Add($match)
            }
        }

        $outcomes = @($memberRecords | Select-Object -ExpandProperty outcome -Unique)
        $state = if ($memberRecords.Count -ne @($group.members).Count) {
            'invalid'
        }
        elseif ($outcomes.Count -eq 1 -and $outcomes[0] -ceq 'Passed') {
            'available'
        }
        elseif ($outcomes.Count -eq 1 -and $outcomes[0] -ceq 'Skipped') {
            $reasonMismatches = @($memberRecords | Where-Object {
                    [string]$_.skipReasonSha256 -cne [string]$group.skipReason.normalizedSha256
                })
            if ($reasonMismatches.Count -eq 0) { 'unavailable' } else { 'invalid' }
        }
        else {
            'invalid'
        }

        if ($state -ceq 'invalid') {
            $policyErrors.Add("capability_outcome:$($group.name)")
        }
        $capabilities.Add([ordered]@{
            name = [string]$group.name
            state = $state
            members = @($group.members).Count
        })
    }

    return [pscustomobject]@{
        Status = if ($policyErrors.Count -eq 0) { 'passed' } else { 'failed' }
        Reason = if ($policyErrors.Count -eq 0) { $null } else { [string]$policyErrors[0] }
        Errors = $policyErrors.ToArray()
        UnexpectedSkips = $unexpectedSkips
        Capabilities = $capabilities.ToArray()
    }
}
