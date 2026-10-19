using namespace System.IO

# 由 JetHarness.psm1 點號載入。執行 git 的共用函式與候選版路線：測試斷言變動的量測、Git 索引指紋、從 Git 清單建立快照，以及在快照裡執行子步驟。

function Test-JetOrdinalSequenceEqual {
    param(
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [string[]] $Left,
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [string[]] $Right
    )

    if ($Left.Count -ne $Right.Count) {
        return $false
    }
    for ($index = 0; $index -lt $Left.Count; $index++) {
        if (-not [string]::Equals($Left[$index], $Right[$index], [StringComparison]::Ordinal)) {
            return $false
        }
    }
    return $true
}

function Invoke-JetGitLines {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [string[]] $Arguments
    )

    $git = Get-Command git -CommandType Application -ErrorAction Stop |
        Select-Object -First 1 -ExpandProperty Source
    $safeRoot = [IO.Path]::GetFullPath($RepositoryRoot).Replace('\', '/')
    $nativeArguments = @(
        '-c', "safe.directory=$safeRoot",
        '-c', 'core.excludesFile=',
        '-c', 'core.quotepath=false',
        '-C', ([IO.Path]::GetFullPath($RepositoryRoot))
    ) + $Arguments
    $output = @(& $git @nativeArguments 2>$null)
    $exitCode = $LASTEXITCODE
    return [pscustomobject]@{
        ExitCode = $exitCode
        Lines = @($output | ForEach-Object { [string]$_ })
    }
}

function Get-JetTestAssertionDrift {
    param([Parameter(Mandatory = $true)] [string] $RepositoryRoot)

    # 比對 HEAD 與工作樹在 src/JET/tests 下的差異，回答「這次改動有沒有拿掉斷言、刪掉測試或加上 Skip」。
    # 這是警示不是判定：數字大於 0 只表示要說明原因，不會讓命令失敗。ReleaseCandidate 快照沒有 HEAD，
    # 或環境沒有 git 時，記為未量測。
    try {
        $diff = Invoke-JetGitLines -RepositoryRoot $RepositoryRoot -Arguments @(
            'diff', 'HEAD', '--unified=0', '--no-color', '--', 'src/JET/tests')
    }
    catch {
        return [ordered]@{ measured = $false; reason = 'git_unavailable' }
    }
    if ($diff.ExitCode -ne 0) {
        return [ordered]@{ measured = $false; reason = 'git_diff_unavailable' }
    }

    $assertionsRemoved = 0
    $assertionsAdded = 0
    $factsOrTheoriesRemoved = 0
    $skipsAdded = 0
    $files = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($line in $diff.Lines) {
        if ($line.StartsWith('+++ b/', [StringComparison]::Ordinal)) {
            [void]$files.Add($line.Substring(6))
            continue
        }
        if ($line.StartsWith('---', [StringComparison]::Ordinal) -or
            $line.StartsWith('+++', [StringComparison]::Ordinal) -or
            $line.StartsWith('@@', [StringComparison]::Ordinal) -or
            $line.StartsWith('diff ', [StringComparison]::Ordinal) -or
            $line.StartsWith('index ', [StringComparison]::Ordinal)) {
            continue
        }
        if ($line.StartsWith('-', [StringComparison]::Ordinal)) {
            if ([regex]::IsMatch($line, '\bAssert\.')) { $assertionsRemoved++ }
            if ([regex]::IsMatch($line, '\[\s*(Fact|Theory)\b')) { $factsOrTheoriesRemoved++ }
        }
        elseif ($line.StartsWith('+', [StringComparison]::Ordinal)) {
            if ([regex]::IsMatch($line, '\bAssert\.')) { $assertionsAdded++ }
            if ([regex]::IsMatch($line, '\bSkip\s*=')) { $skipsAdded++ }
        }
    }

    return [ordered]@{
        measured = $true
        baseline = 'HEAD'
        files = @($files)
        assertionsRemoved = $assertionsRemoved
        assertionsAdded = $assertionsAdded
        factsOrTheoriesRemoved = $factsOrTheoriesRemoved
        skipsAdded = $skipsAdded
        needsExplanation = ($assertionsRemoved -gt 0 -or $factsOrTheoriesRemoved -gt 0 -or $skipsAdded -gt 0)
    }
}

function Get-JetSourceIndexFingerprint {
    param([Parameter(Mandatory = $true)] [string] $RepositoryRoot)

    $stagedEntries = Invoke-JetGitLines `
        -RepositoryRoot $RepositoryRoot `
        -Arguments @('ls-files', '--stage', '--')
    if ($stagedEntries.ExitCode -ne 0) {
        throw [InvalidOperationException]::new('Source Git index inventory failed.')
    }

    $aggregate = [Security.Cryptography.IncrementalHash]::CreateHash(
        [Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        foreach ($entry in [string[]]$stagedEntries.Lines) {
            $aggregate.AppendData($script:Utf8.GetBytes("$entry`n"))
        }
        $sha256 = [Convert]::ToHexString($aggregate.GetHashAndReset()).ToLowerInvariant()
    }
    finally {
        $aggregate.Dispose()
    }

    return [pscustomobject]@{
        EntryCount = $stagedEntries.Lines.Count
        Sha256 = $sha256
    }
}

function New-JetReleaseCandidateSnapshot {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $RunId,
        [Parameter(Mandatory = $true)] $Registry
    )

    $startedUtc = [DateTime]::UtcNow
    $failureStatus = 'failed'
    $failureReason = 'candidate_inventory_invalid'
    $workspaceRoot = $null
    $snapshotRoot = $null
    $sourceIndexBefore = $null
    $sourceIndexAfter = $null
    $sourceIndexMutated = $null
    try {
        # 候選內容直接取自 Git：已有提交時是已追蹤的檔案，第一次根提交前是尚未被忽略的檔案。
        # 不再維護手寫的候選清單；私人資料由下面的禁止路徑類別與 .gitignore 擋住。
        $failureStatus = 'infrastructure_error'
        $failureReason = 'candidate_git_inventory_failed'
        $sourceIndexBefore = Get-JetSourceIndexFingerprint -RepositoryRoot $RepositoryRoot

        $head = Invoke-JetGitLines -RepositoryRoot $RepositoryRoot -Arguments @('rev-parse', '--verify', 'HEAD')
        $sourceMode = if ($head.ExitCode -eq 0) { 'committed-head' } else { 'first-root-candidate' }
        $tracked = Invoke-JetGitLines -RepositoryRoot $RepositoryRoot -Arguments @('ls-files', '--cached', '--')
        if ($tracked.ExitCode -ne 0) {
            throw [InvalidOperationException]::new('Tracked inventory failed.')
        }

        if ($sourceMode -ceq 'committed-head') {
            $status = Invoke-JetGitLines `
                -RepositoryRoot $RepositoryRoot `
                -Arguments @('status', '--porcelain=v1', '--untracked-files=all', '--')
            if ($status.ExitCode -ne 0) {
                throw [InvalidOperationException]::new('Git status failed.')
            }
            if ($status.Lines.Count -ne 0) {
                $failureReason = 'candidate_source_dirty'
                throw [InvalidDataException]::new('Committed source is not clean.')
            }
            $candidatePaths = [string[]]$tracked.Lines
        }
        else {
            if ($tracked.Lines.Count -ne 0) {
                $failureReason = 'candidate_source_index_not_empty'
                throw [InvalidDataException]::new('Initial source index must remain untouched.')
            }
            $untracked = Invoke-JetGitLines `
                -RepositoryRoot $RepositoryRoot `
                -Arguments @('ls-files', '--others', '--exclude-standard', '--')
            if ($untracked.ExitCode -ne 0) {
                throw [InvalidOperationException]::new('Untracked inventory failed.')
            }
            $candidatePaths = [string[]]$untracked.Lines
        }
        [Array]::Sort($candidatePaths, [StringComparer]::Ordinal)

        $failureStatus = 'failed'
        $failureReason = 'candidate_inventory_invalid'
        if ($candidatePaths.Count -eq 0) {
            throw [InvalidDataException]::new('Candidate inventory is empty.')
        }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($relativePath in $candidatePaths) {
            if ([string]::IsNullOrWhiteSpace($relativePath) -or
                $relativePath -cne $relativePath.Trim() -or
                $relativePath.Contains('\', [StringComparison]::Ordinal) -or
                $relativePath -match '[\x00-\x1F\x7F]' -or
                [IO.Path]::IsPathFullyQualified($relativePath) -or
                $relativePath.StartsWith('./', [StringComparison]::Ordinal) -or
                $relativePath -match '(^|/)\.\.(/|$)' -or
                -not $seen.Add($relativePath)) {
                throw [InvalidDataException]::new('Candidate inventory contains an invalid path.')
            }

            $normalized = $relativePath.ToLowerInvariant()
            if ($normalized -eq '.git' -or
                $normalized.StartsWith('.git/', [StringComparison]::Ordinal) -or
                $normalized.StartsWith('artifacts/', [StringComparison]::Ordinal)) {
                throw [InvalidDataException]::new('Candidate inventory includes a forbidden path class.')
            }
            if ($normalized.StartsWith('data/test-case/', [StringComparison]::Ordinal) -or
                $normalized.StartsWith('data/temporary-test-case/', [StringComparison]::Ordinal) -or
                $normalized.StartsWith('data/legacy-parity-work/', [StringComparison]::Ordinal) -or
                $normalized.StartsWith('data/private/', [StringComparison]::Ordinal) -or
                $normalized.StartsWith('data/real-case/', [StringComparison]::Ordinal) -or
                $normalized.StartsWith('data/customer-data/', [StringComparison]::Ordinal)) {
                $failureReason = 'candidate_private_path'
                throw [InvalidDataException]::new('Candidate inventory includes a private data path.')
            }

            $fullPath = [IO.Path]::GetFullPath($relativePath, $RepositoryRoot)
            if (-not (Test-JetDescendantPath -Root $RepositoryRoot -Candidate $fullPath) -or
                -not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
                throw [InvalidDataException]::new('Candidate inventory references an unavailable file.')
            }
            Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $fullPath
        }
        $manifestLines = $candidatePaths
        $inventorySource = if ($sourceMode -ceq 'committed-head') { 'git-tracked' } else { 'git-unignored' }

        $workspaceRelativeRoot = [string]$Registry.releaseCandidateSettings.snapshotWorkspaceRoot
        if ([IO.Path]::IsPathRooted($workspaceRelativeRoot)) {
            throw [InvalidDataException]::new('Candidate workspace root must remain repository-relative.')
        }
        $harnessArtifactsRoot = [IO.Path]::GetFullPath('artifacts/harness', $RepositoryRoot)
        $workspaceBase = [IO.Path]::GetFullPath($workspaceRelativeRoot, $RepositoryRoot)
        if (-not (Test-JetDescendantPath -Root $harnessArtifactsRoot -Candidate $workspaceBase)) {
            throw [InvalidDataException]::new('Candidate workspace root escaped harness artifacts.')
        }
        Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $workspaceBase
        [void][IO.Directory]::CreateDirectory($workspaceBase)
        Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $workspaceBase

        $workspaceToken = $RunId.Substring($RunId.Length - 12)
        if ($workspaceToken -cnotmatch '^[0-9a-f]{12}$') {
            throw [InvalidDataException]::new('Candidate workspace token is invalid.')
        }
        $workspaceRoot = Join-Path $workspaceBase $workspaceToken
        if (Test-Path -LiteralPath $workspaceRoot) {
            $failureStatus = 'infrastructure_error'
            $failureReason = 'candidate_snapshot_collision'
            throw [IOException]::new('Candidate workspace already exists.')
        }
        [void][IO.Directory]::CreateDirectory($workspaceRoot)
        $workspaceOwner = [ordered]@{
            schemaVersion = 1
            kind = 'jet-harness-release-candidate-workspace'
            runId = $RunId
            processId = $PID
            createdUtc = [DateTime]::UtcNow.ToString('O')
        }
        Write-JetAtomicJson -Path (Join-Path $workspaceRoot $script:OwnerMarkerName) -Value $workspaceOwner
        $snapshotRoot = Join-Path $workspaceRoot 's'
        [void][IO.Directory]::CreateDirectory($snapshotRoot)

        $aggregate = [Security.Cryptography.IncrementalHash]::CreateHash(
            [Security.Cryptography.HashAlgorithmName]::SHA256)
        $totalBytes = [long]0
        try {
            foreach ($relativePath in $manifestLines) {
                $sourcePath = [IO.Path]::GetFullPath($relativePath, $RepositoryRoot)
                $sourceItem = Get-Item -LiteralPath $sourcePath -Force
                if ($sourceItem.Length -gt 536870912) {
                    $failureReason = 'candidate_file_too_large'
                    throw [InvalidDataException]::new('Candidate file exceeds the interactive copy limit.')
                }
                $destinationPath = [IO.Path]::GetFullPath($relativePath, $snapshotRoot)
                if (-not (Test-JetDescendantPath -Root $snapshotRoot -Candidate $destinationPath)) {
                    throw [InvalidDataException]::new('Candidate copy escaped the snapshot root.')
                }
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destinationPath))
                [IO.File]::Copy($sourcePath, $destinationPath, $false)

                $sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
                $destinationHash = (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash
                if ($sourceHash -cne $destinationHash) {
                    $failureStatus = 'infrastructure_error'
                    $failureReason = 'candidate_snapshot_copy_mismatch'
                    throw [IOException]::new('Candidate copy hash mismatch.')
                }
                $totalBytes += [long]$sourceItem.Length
                $aggregate.AppendData($script:Utf8.GetBytes(
                    "$relativePath`t$($sourceItem.Length)`t$sourceHash`n"))
            }
            $aggregateSha256 = [Convert]::ToHexString($aggregate.GetHashAndReset()).ToLowerInvariant()
        }
        finally {
            $aggregate.Dispose()
        }

        $failureStatus = 'infrastructure_error'
        $failureReason = 'candidate_snapshot_git_failed'
        $initialize = Invoke-JetGitLines `
            -RepositoryRoot $snapshotRoot `
            -Arguments @('init', '--quiet', '--initial-branch=main')
        if ($initialize.ExitCode -ne 0) {
            throw [InvalidOperationException]::new('Candidate snapshot Git initialization failed.')
        }
        $index = Invoke-JetGitLines `
            -RepositoryRoot $snapshotRoot `
            -Arguments @('add', '--intent-to-add', '--all', '--')
        if ($index.ExitCode -ne 0) {
            throw [InvalidOperationException]::new('Candidate snapshot index creation failed.')
        }
        $snapshotTracked = Invoke-JetGitLines `
            -RepositoryRoot $snapshotRoot `
            -Arguments @('ls-files', '--cached', '--')
        $snapshotPaths = [string[]]$snapshotTracked.Lines
        [Array]::Sort($snapshotPaths, [StringComparer]::Ordinal)
        if ($snapshotTracked.ExitCode -ne 0 -or
            -not (Test-JetOrdinalSequenceEqual -Left $manifestLines -Right $snapshotPaths)) {
            throw [InvalidOperationException]::new('Candidate snapshot index does not match the inventory.')
        }

        $sourceIndexAfter = Get-JetSourceIndexFingerprint -RepositoryRoot $RepositoryRoot
        $sourceIndexMutated = $sourceIndexBefore.EntryCount -ne $sourceIndexAfter.EntryCount -or
            $sourceIndexBefore.Sha256 -cne $sourceIndexAfter.Sha256
        if ($sourceIndexMutated) {
            $failureReason = 'candidate_source_index_changed'
            throw [InvalidDataException]::new('Source Git index entries changed during candidate snapshot creation.')
        }

        $completedUtc = [DateTime]::UtcNow
        $step = New-JetSyntheticStep `
            -Name 'release-candidate-snapshot' `
            -Status 'passed' `
            -Reason $null `
            -Evidence ([ordered]@{
                sourceMode = $sourceMode
                inventory = $inventorySource
                fileCount = $manifestLines.Count
                bytes = $totalBytes
                aggregateSha256 = $aggregateSha256
                sourceIndexMeasured = $true
                sourceIndexComparison = 'cached-stage-entries'
                sourceIndexEntryCountBefore = $sourceIndexBefore.EntryCount
                sourceIndexEntryCountAfter = $sourceIndexAfter.EntryCount
                sourceIndexSha256Before = $sourceIndexBefore.Sha256
                sourceIndexSha256After = $sourceIndexAfter.Sha256
                sourceIndexMutated = $sourceIndexMutated
                privateDataIncluded = $false
                snapshotRetained = $true
            })
        $step.startedUtc = $startedUtc.ToString('O')
        $step.completedUtc = $completedUtc.ToString('O')
        $step.durationSeconds = [Math]::Round(($completedUtc - $startedUtc).TotalSeconds, 3)
        return [pscustomobject]@{
            Step = $step
            SnapshotRoot = $snapshotRoot
            WorkspaceRoot = $workspaceRoot
        }
    }
    catch {
        if ($null -ne $sourceIndexBefore -and $null -eq $sourceIndexAfter) {
            try {
                $sourceIndexAfter = Get-JetSourceIndexFingerprint -RepositoryRoot $RepositoryRoot
                $sourceIndexMutated = $sourceIndexBefore.EntryCount -ne $sourceIndexAfter.EntryCount -or
                    $sourceIndexBefore.Sha256 -cne $sourceIndexAfter.Sha256
            }
            catch {
                $sourceIndexAfter = $null
                $sourceIndexMutated = $null
            }
        }
        $completedUtc = [DateTime]::UtcNow
        $step = New-JetSyntheticStep `
            -Name 'release-candidate-snapshot' `
            -Status $failureStatus `
            -Reason $failureReason `
            -Evidence ([ordered]@{
                errorType = $_.Exception.GetType().Name
                sourceIndexMeasured = $null -ne $sourceIndexBefore -and $null -ne $sourceIndexAfter
                sourceIndexComparison = 'cached-stage-entries'
                sourceIndexEntryCountBefore = if ($null -ne $sourceIndexBefore) { $sourceIndexBefore.EntryCount } else { $null }
                sourceIndexEntryCountAfter = if ($null -ne $sourceIndexAfter) { $sourceIndexAfter.EntryCount } else { $null }
                sourceIndexSha256Before = if ($null -ne $sourceIndexBefore) { $sourceIndexBefore.Sha256 } else { $null }
                sourceIndexSha256After = if ($null -ne $sourceIndexAfter) { $sourceIndexAfter.Sha256 } else { $null }
                sourceIndexMutated = $sourceIndexMutated
                privateDataIncluded = $false
                snapshotRetained = $null -ne $workspaceRoot -and (Test-Path -LiteralPath $workspaceRoot)
            })
        $step.startedUtc = $startedUtc.ToString('O')
        $step.completedUtc = $completedUtc.ToString('O')
        $step.durationSeconds = [Math]::Round(($completedUtc - $startedUtc).TotalSeconds, 3)
        return [pscustomobject]@{
            Step = $step
            SnapshotRoot = $null
            WorkspaceRoot = $workspaceRoot
        }
    }
}

function Invoke-JetReleaseCandidateChildStep {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $SnapshotRoot,
        [Parameter(Mandatory = $true)] [string] $RunDirectory,
        [Parameter(Mandatory = $true)] $Definition,
        [Parameter(Mandatory = $true)] $Registry,
        [Parameter(Mandatory = $true)] [int] $TimeoutSeconds
    )

    $name = "release-candidate-$([string]$Definition.name)"
    $pwsh = Get-Command pwsh -CommandType Application -ErrorAction Stop |
        Select-Object -First 1 -ExpandProperty Source
    $arguments = [Collections.Generic.List[string]]::new()
    foreach ($value in @(
            '-NoProfile',
            '-File', (Join-Path $SnapshotRoot 'tools/verify.ps1'),
            '-Command', ([string]$Definition.command),
            '-Configuration', ([string]$Definition.configuration),
            '-TimeoutSeconds', ([string]$TimeoutSeconds),
            '-EvidenceRoot', 'artifacts/harness/runs')) {
        $arguments.Add($value)
    }
    if ([bool]$Definition.noRestore) {
        $arguments.Add('-NoRestore')
    }

    [string[]]$environmentVariablesToRemove = @($Registry.testSettings.environmentVariablesToRemove)
    [string[]]$sensitiveValues = [string[]]::new(0)
    $displayCommand = @(
        'pwsh', '-NoProfile', '-File', '<candidate>/tools/verify.ps1',
        '-Command', ([string]$Definition.command),
        '-Configuration', ([string]$Definition.configuration),
        '-TimeoutSeconds', ([string]$TimeoutSeconds)
    )
    if ([bool]$Definition.noRestore) {
        $displayCommand += '-NoRestore'
    }

    $step = Invoke-JetChildStep `
        -RepositoryRoot $RepositoryRoot `
        -RunDirectory $RunDirectory `
        -Name $name `
        -FileName $pwsh `
        -Arguments $arguments.ToArray() `
        -DisplayCommand $displayCommand `
        -MaximumCapturedBytes ([int]$Registry.limits.maximumCapturedBytesPerStream) `
        -TimeoutSeconds $TimeoutSeconds `
        -WorkingDirectory $SnapshotRoot `
        -EnvironmentVariablesToRemove $environmentVariablesToRemove `
        -SensitiveValuesToRedact $sensitiveValues

    if ([bool]$step.process.timedOut -or [bool]$step.process.cancelled) {
        $step.status = 'blocked'
        $step.classification.reason = 'release_candidate_child_timeout'
        return $step
    }

    try {
        if ([bool]$step.stdout.truncated) {
            throw [InvalidDataException]::new('ReleaseCandidate child envelope was truncated.')
        }
        $stdoutPath = [IO.Path]::GetFullPath([string]$step.stdout.path, $RepositoryRoot)
        $jsonLine = @(
            [IO.File]::ReadAllLines($stdoutPath, $script:Utf8) |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        ) | Select-Object -Last 1
        if ([string]::IsNullOrWhiteSpace($jsonLine)) {
            throw [InvalidDataException]::new('ReleaseCandidate child envelope is missing.')
        }
        $envelope = $jsonLine | ConvertFrom-Json -AsHashtable -Depth 20
        if ([int]$envelope.schemaVersion -ne $script:ReceiptSchemaVersion -or
            [string]::IsNullOrWhiteSpace([string]$envelope.runId) -or
            [string]::IsNullOrWhiteSpace([string]$envelope.receipt)) {
            throw [InvalidDataException]::new('ReleaseCandidate child envelope is invalid.')
        }

        $receiptRelativePath = [string]$envelope.receipt
        if ([IO.Path]::IsPathFullyQualified($receiptRelativePath) -or
            $receiptRelativePath -match '(^|/)\.\.(/|$)' -or
            $receiptRelativePath.Contains('\', [StringComparison]::Ordinal)) {
            throw [InvalidDataException]::new('ReleaseCandidate child receipt path is invalid.')
        }
        $receiptPath = [IO.Path]::GetFullPath($receiptRelativePath, $SnapshotRoot)
        $allowedReceiptRoot = [IO.Path]::GetFullPath('artifacts/harness/runs', $SnapshotRoot)
        if (-not (Test-JetDescendantPath -Root $allowedReceiptRoot -Candidate $receiptPath) -or
            -not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
            throw [InvalidDataException]::new('ReleaseCandidate child receipt escaped its evidence root.')
        }
        Assert-JetNoExistingReparsePoint -RepositoryRoot $SnapshotRoot -Candidate $receiptPath
        if ((Get-Item -LiteralPath $receiptPath -Force).Length -gt 16777216) {
            throw [InvalidDataException]::new('ReleaseCandidate child receipt is too large.')
        }
        $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding utf8 |
            ConvertFrom-Json -AsHashtable -Depth 30
        if ([int]$receipt.schemaVersion -ne $script:ReceiptSchemaVersion -or
            [string]$receipt.runId -cne [string]$envelope.runId -or
            [string]$receipt.command -cne [string]$Definition.command -or
            [string]$receipt.configuration -cne [string]$Definition.configuration -or
            [int]$receipt.exitCode -ne [int]$envelope.exitCode -or
            [int]$receipt.exitCode -ne [int]$step.process.exitCode -or
            [string]$receipt.status -notin @('passed', 'failed', 'blocked', 'infrastructure_error') -or
            [bool]$receipt.privateData.pathInspected -or
            [bool]$receipt.privateData.pathsRecorded) {
            throw [InvalidDataException]::new('ReleaseCandidate child receipt contract is invalid.')
        }
        $lockRequired = [bool]$Registry.commands[[string]$Definition.command].requiresExclusiveLock
        $cleanupComplete = [string]$receipt.cleanup.status -ceq 'passed' -and
            (-not $lockRequired -or [string]$receipt.lock.cleanup.status -ceq 'passed')
        if (-not $cleanupComplete) {
            throw [InvalidDataException]::new('ReleaseCandidate child cleanup is incomplete.')
        }

        $copiedReceiptPath = Join-Path $RunDirectory "$name.receipt.json"
        Write-JetAtomicJson -Path $copiedReceiptPath -Value $receipt
        $reason = if ($null -ne $receipt.firstRed -and
            -not [string]::IsNullOrWhiteSpace([string]$receipt.firstRed.reason)) {
            [string]$receipt.firstRed.reason
        }
        elseif ($null -ne $receipt.error -and
            -not [string]::IsNullOrWhiteSpace([string]$receipt.error.code)) {
            [string]$receipt.error.code
        }
        else {
            $null
        }
        $step.status = [string]$receipt.status
        $step.classification.reason = $reason
        $step['evidence'] = [ordered]@{
            command = [string]$receipt.command
            runId = [string]$receipt.runId
            receipt = Get-JetRelativePath -RepositoryRoot $RepositoryRoot -Path $copiedReceiptPath
            cleanupComplete = $cleanupComplete
            privatePathInspected = $false
        }
        return $step
    }
    catch {
        $step.status = 'infrastructure_error'
        $step.classification.reason = 'release_candidate_child_receipt_invalid'
        $step['evidence'] = [ordered]@{ errorType = $_.Exception.GetType().Name }
        return $step
    }
}
