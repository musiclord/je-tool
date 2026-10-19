using namespace System.IO

# 由 JetHarness.psm1 點號載入。管理 artifacts/harness：刪除自己建立的目錄、收據與改壞檢查資料的保留期限、清除候選版工作區，以及共享鎖。

function Remove-JetOwnedDirectory {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $RunDirectory,
        [Parameter(Mandatory = $true)] [string] $OwnedDirectory,
        [Parameter(Mandatory = $true)] [string] $RunId
    )

    if (-not (Test-Path -LiteralPath $OwnedDirectory)) {
        return [ordered]@{ status = 'passed'; removed = $false; reason = 'already_absent' }
    }

    $ownedFull = [IO.Path]::GetFullPath($OwnedDirectory)
    $runFull = [IO.Path]::GetFullPath($RunDirectory)
    if (-not (Test-JetDescendantPath -Root $runFull -Candidate $ownedFull)) {
        return [ordered]@{ status = 'failed'; removed = $false; reason = 'outside_run_root' }
    }

    try {
        Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $ownedFull
        $markerPath = Join-Path $ownedFull $script:OwnerMarkerName
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
            return [ordered]@{ status = 'failed'; removed = $false; reason = 'owner_marker_missing' }
        }
        $marker = Get-Content -LiteralPath $markerPath -Raw -Encoding utf8 | ConvertFrom-Json
        if ($marker.kind -cne 'jet-harness-scratch' -or $marker.runId -cne $RunId) {
            return [ordered]@{ status = 'failed'; removed = $false; reason = 'owner_marker_mismatch' }
        }
        Clear-JetOwnedReadOnlyAttributes -Root $ownedFull
        [IO.Directory]::Delete($ownedFull, $true)
        return [ordered]@{ status = 'passed'; removed = $true; reason = $null }
    }
    catch {
        return [ordered]@{
            status = 'failed'
            removed = $false
            reason = 'owned_cleanup_exception'
            errorType = $_.Exception.GetType().Name
        }
    }
}

# 收據保留期限（使用者 2026-10-14 選「連收據保留期限一起做」；當時本機有 3,226 次執行、2.9 GB，沒有上限）。
# 同一種檢查（命令，加上是否只跑單一情境或篩選）只完整保留最新的幾次執行；更舊的執行只留 receipt.json、
# 各步驟的 *.receipt.json 與擁有者標記，測試結果明細、記錄檔、截圖與暫存都刪掉。收據摘要仍在，
# 文件與測試註解引用的舊收據仍讀得到。只處理已寫好 receipt.json 的執行，進行中的執行不會被碰到。
$script:ReceiptRetentionKeepPerGroup = 10

function Invoke-JetReceiptRetention {
    param(
        [Parameter(Mandatory = $true)] [string] $EvidenceRoot,
        [int] $KeepPerGroup = $script:ReceiptRetentionKeepPerGroup
    )

    $result = [ordered]@{ prunedRuns = 0; skippedRuns = 0 }
    $rootFull = [IO.Path]::GetFullPath($EvidenceRoot)
    if (-not [IO.Directory]::Exists($rootFull) -or
        ([IO.File]::GetAttributes($rootFull) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        return $result
    }

    # 每次驗證結束都會跑一次，所以用 .NET 呼叫列目錄與比對檔名，不用 PowerShell 管線與 cmdlet。
    $kept = @{}
    $names = [Collections.Generic.List[string]]::new()
    foreach ($path in [IO.Directory]::GetDirectories($rootFull)) {
        $name = [IO.Path]::GetFileName($path)
        if ($name -cmatch '^\d{8}-\d{9}-[a-f0-9]{32}$') { $names.Add($name) }
    }
    $names.Sort([StringComparer]::Ordinal)
    $names.Reverse()
    foreach ($name in $names) {
        $directory = [IO.Path]::Combine($rootFull, $name)
        try {
            if (([IO.File]::GetAttributes($directory) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                $result.skippedRuns++
                continue
            }
            $receiptPath = [IO.Path]::Combine($directory, 'receipt.json')
            if (-not [IO.File]::Exists($receiptPath)) { continue }
            # 已經整理過的執行只剩保留的檔案，不再讀它的收據，也不佔同組的名額；它們一定比同組保留的執行舊。
            $removable = [Collections.Generic.List[string]]::new()
            foreach ($entry in [IO.Directory]::GetFileSystemEntries($directory)) {
                $entryName = [IO.Path]::GetFileName($entry)
                $retained = [IO.File]::Exists($entry) -and ($entryName -ceq 'receipt.json' -or
                    $entryName -ceq $script:OwnerMarkerName -or
                    $entryName.EndsWith('.receipt.json', [StringComparison]::Ordinal))
                if (-not $retained) { $removable.Add($entry) }
            }
            if ($removable.Count -eq 0) { continue }

            # 只需要最上層的 command 與 partial，兩者都寫在 steps 等巢狀內容之前，所以取第一個符合的值，
            # 不必解析整份收據。較早的收據沒有 partial 欄位，當成完整執行。
            $receiptText = [IO.File]::ReadAllText($receiptPath)
            $commandMatch = [regex]::Match($receiptText, '"command"\s*:\s*"([^"]*)"')
            $partialMatch = [regex]::Match($receiptText, '"partial"\s*:\s*(true|false)')
            $group = '{0}|{1}' -f $commandMatch.Groups[1].Value,
                ($partialMatch.Success -and $partialMatch.Groups[1].Value -ceq 'true')
            $count = if ($kept.ContainsKey($group)) { [int]$kept[$group] } else { 0 }
            if ($count -lt $KeepPerGroup) {
                $kept[$group] = $count + 1
                continue
            }

            $markerPath = Join-Path $directory $script:OwnerMarkerName
            if (-not [IO.File]::Exists($markerPath)) {
                $result.skippedRuns++
                continue
            }
            $marker = [IO.File]::ReadAllText($markerPath) | ConvertFrom-Json
            if ($marker.kind -cne 'jet-harness-run' -or $marker.runId -cne $name) {
                $result.skippedRuns++
                continue
            }

            # 底下有連結點時整個執行跳過，不跟著連結刪到別處。
            Clear-JetOwnedReadOnlyAttributes -Root $directory
            foreach ($entry in $removable) {
                if ([IO.Directory]::Exists($entry)) { [IO.Directory]::Delete($entry, $true) }
                else { [IO.File]::Delete($entry) }
            }
            $result.prunedRuns++
        }
        catch {
            $result.skippedRuns++
        }
    }
    return $result
}

# 改壞檢查的保留期限（使用者 2026-10-15 在待決事項勾選頁選照建議 S8：「三個命令都保留；替 Mutation 的快取加上保留上限，
# 做法比照收據的保留期限。」）。當時 artifacts/harness/mutation/ 約 7 GB，沒有上限。
# r/ 底下每次執行一個資料夾，完整的約 1 GB，大部分是工作樹副本。同一個範圍（GlProjectionGuard、MoneyScaling）只完整保留
# 最新的幾次；更舊的只留最上層的摘要檔，result.json 已逐項記下每個變異的結果，工作樹副本、暫存與報告資料夾都刪掉。
# t/ 底下每個 Stryker 修補版本各有一份工具來源，只留最新一次執行（包括中斷的）用的那份。c/http 是 NuGet 的下載快取，套件已解壓在
# c/packages，整個刪掉；c/packages 保留，讓還原不必連網，只在套件版本改變時增加。
# 改壞檢查也要拿共享鎖，所以只在拿著鎖時呼叫，不會碰到進行中的執行；還沒寫 pipeline.json 的中斷執行也不碰。
# 這段程式不放在 mutation/JetMutation.psm1：那個檔的雜湊是 t/ 工具版本的一部分，改了會讓下次執行重新下載工具來源。
$script:MutationRetentionKeepPerScope = 2

function Test-JetPlainDirectory {
    param([Parameter(Mandatory = $true)] [string] $Path)
    return [IO.Directory]::Exists($Path) -and
        ([IO.File]::GetAttributes($Path) -band [IO.FileAttributes]::ReparsePoint) -eq 0
}

function Invoke-JetMutationRetention {
    param(
        [Parameter(Mandatory = $true)] [string] $MutationRoot,
        [int] $KeepPerScope = $script:MutationRetentionKeepPerScope
    )

    $result = [ordered]@{ prunedRuns = 0; prunedToolSources = 0; removedHttpCache = $false; skipped = 0 }
    $rootFull = [IO.Path]::GetFullPath($MutationRoot)
    if (-not (Test-JetPlainDirectory $rootFull)) { return $result }

    $runs = [Collections.Generic.List[object]]::new()
    $runsRoot = [IO.Path]::Combine($rootFull, 'r')
    if (Test-JetPlainDirectory $runsRoot) {
        foreach ($directory in [IO.Directory]::GetDirectories($runsRoot)) {
            try {
                if ([IO.Path]::GetFileName($directory) -cnotmatch '^[a-f0-9]{12}$' -or -not (Test-JetPlainDirectory $directory)) {
                    $result.skipped++
                    continue
                }
                $ownerPath = [IO.Path]::Combine($directory, 'owner.json')
                if (-not [IO.File]::Exists($ownerPath)) { continue }
                # createdUtc 是固定寬度的 ISO 8601，字串順序就是時間順序。用規則運算式取值，
                # 避免 ConvertFrom-Json 把它轉成依地區格式輸出的日期。
                $created = [regex]::Match([IO.File]::ReadAllText($ownerPath), '"createdUtc"\s*:\s*"([^"]+)"')
                if (-not $created.Success) {
                    $result.skipped++
                    continue
                }
                # 執行結束才寫 pipeline.json，scope 是它的第一個欄位。沒有 pipeline.json 的中斷執行只用來決定工具來源，不整理。
                $scopeName = $null
                $pipelinePath = [IO.Path]::Combine($directory, 'pipeline.json')
                if ([IO.File]::Exists($pipelinePath)) {
                    $scope = [regex]::Match([IO.File]::ReadAllText($pipelinePath), '"scope"\s*:\s*\{\s*"name"\s*:\s*"([^"]+)"')
                    if (-not $scope.Success) {
                        $result.skipped++
                        continue
                    }
                    $scopeName = $scope.Groups[1].Value
                }
                $runs.Add([pscustomobject]@{ Directory = $directory; Created = $created.Groups[1].Value; Scope = $scopeName })
            }
            catch {
                $result.skipped++
            }
        }
    }
    $runs.Sort([Comparison[object]] { param($left, $right) [string]::CompareOrdinal($right.Created, $left.Created) })

    $kept = @{}
    $toolKey = $null
    foreach ($run in $runs) {
        # 工具版本檔讀不出來時只跳過它，這次執行照樣計入保留名額。
        if ($null -eq $toolKey) {
            try {
                $identityPath = [IO.Path]::Combine($run.Directory, 'tool-identity.json')
                if ([IO.File]::Exists($identityPath)) {
                    $identity = [IO.File]::ReadAllText($identityPath) | ConvertFrom-Json
                    # 和 JetMutation.psm1 的 Initialize-JetMutationToolSource 用同一種名稱。
                    $toolKey = ([string]$identity.pin.revision).Substring(0, 8) + '-' + ([string]$identity.patch.sha256).Substring(0, 8)
                }
            }
            catch {
                $result.skipped++
            }
        }
        try {
            if ($null -eq $run.Scope) { continue }
            $count = if ($kept.ContainsKey($run.Scope)) { [int]$kept[$run.Scope] } else { 0 }
            $kept[$run.Scope] = $count + 1
            if ($count -lt $KeepPerScope) { continue }

            $removable = [IO.Directory]::GetDirectories($run.Directory)
            if ($removable.Length -eq 0) { continue }
            # 底下有連結點時整個執行跳過，不跟著連結刪到別處。
            Clear-JetOwnedReadOnlyAttributes -Root $run.Directory
            foreach ($entry in $removable) { [IO.Directory]::Delete($entry, $true) }
            $result.prunedRuns++
        }
        catch {
            $result.skipped++
        }
    }

    $toolsRoot = [IO.Path]::Combine($rootFull, 't')
    if ($null -ne $toolKey -and (Test-JetPlainDirectory $toolsRoot)) {
        foreach ($directory in [IO.Directory]::GetDirectories($toolsRoot)) {
            try {
                $name = [IO.Path]::GetFileName($directory)
                if ($name -cnotmatch '^[a-f0-9]{8}-[a-f0-9]{8}$' -or $name -ceq $toolKey) { continue }
                if (-not (Test-JetPlainDirectory $directory)) {
                    $result.skipped++
                    continue
                }
                Clear-JetOwnedReadOnlyAttributes -Root $directory
                [IO.Directory]::Delete($directory, $true)
                $result.prunedToolSources++
            }
            catch {
                $result.skipped++
            }
        }
    }

    try {
        $cacheRoot = [IO.Path]::Combine($rootFull, 'c')
        $httpCache = [IO.Path]::Combine($cacheRoot, 'http')
        if ((Test-JetPlainDirectory $cacheRoot) -and (Test-JetPlainDirectory $httpCache)) {
            Clear-JetOwnedReadOnlyAttributes -Root $httpCache
            [IO.Directory]::Delete($httpCache, $true)
            $result.removedHttpCache = $true
        }
    }
    catch {
        $result.skipped++
    }
    return $result
}

function Remove-JetReleaseCandidateWorkspace {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $WorkspaceRoot,
        [Parameter(Mandatory = $true)] [string] $RunId,
        [Parameter(Mandatory = $true)] $Registry
    )

    if (-not (Test-Path -LiteralPath $WorkspaceRoot)) {
        return [ordered]@{ status = 'passed'; removed = $false; reason = 'already_absent' }
    }

    $repositoryFull = [IO.Path]::GetFullPath($RepositoryRoot)
    $workspaceBase = [IO.Path]::GetFullPath(
        [string]$Registry.releaseCandidateSettings.snapshotWorkspaceRoot,
        $repositoryFull)
    $workspaceFull = [IO.Path]::GetFullPath($WorkspaceRoot)
    $workspaceParent = [IO.Path]::GetDirectoryName($workspaceFull)
    if (-not [string]::Equals(
            $workspaceParent,
            $workspaceBase,
            [StringComparison]::OrdinalIgnoreCase)) {
        return [ordered]@{ status = 'failed'; removed = $false; reason = 'outside_release_candidate_root' }
    }

    try {
        Assert-JetNoExistingReparsePoint -RepositoryRoot $repositoryFull -Candidate $workspaceFull
        $markerPath = Join-Path $workspaceFull $script:OwnerMarkerName
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
            return [ordered]@{ status = 'failed'; removed = $false; reason = 'owner_marker_missing' }
        }
        $marker = Get-Content -LiteralPath $markerPath -Raw -Encoding utf8 | ConvertFrom-Json
        if ($marker.kind -cne 'jet-harness-release-candidate-workspace' -or
            $marker.runId -cne $RunId) {
            return [ordered]@{ status = 'failed'; removed = $false; reason = 'owner_marker_mismatch' }
        }
        Clear-JetOwnedReadOnlyAttributes -Root $workspaceFull
        [IO.Directory]::Delete($workspaceFull, $true)
        return [ordered]@{ status = 'passed'; removed = $true; reason = $null }
    }
    catch {
        return [ordered]@{
            status = 'failed'
            removed = $false
            reason = 'owned_cleanup_exception'
            errorType = $_.Exception.GetType().Name
        }
    }
}

function Enter-JetExclusiveLock {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $RunId,
        [Parameter(Mandatory = $true)] [string] $Command,
        [Parameter(Mandatory = $true)] [int] $WaitSeconds
    )

    $controlRoot = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'artifacts/harness/control'))
    Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $controlRoot
    [void][IO.Directory]::CreateDirectory($controlRoot)
    Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $controlRoot

    $lockPath = Join-Path $controlRoot 'exclusive.lock'
    $holderPath = Join-Path $controlRoot 'holder.json'
    $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
    $stream = $null

    do {
        try {
            $stream = [IO.FileStream]::new(
                $lockPath,
                [IO.FileMode]::OpenOrCreate,
                [IO.FileAccess]::ReadWrite,
                [IO.FileShare]::None)
            break
        }
        catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) {
                $holder = $null
                if (Test-Path -LiteralPath $holderPath -PathType Leaf) {
                    try {
                        $candidate = Get-Content -LiteralPath $holderPath -Raw -Encoding utf8 | ConvertFrom-Json
                        $holder = [ordered]@{
                            runId = $candidate.runId
                            processId = $candidate.processId
                            command = $candidate.command
                            acquiredUtc = $candidate.acquiredUtc
                        }
                    }
                    catch {
                        $holder = [ordered]@{ state = 'unreadable' }
                    }
                }
                return [pscustomobject]@{
                    Acquired = $false
                    Stream = $null
                    HolderPath = $holderPath
                    Holder = $holder
                }
            }
            Start-Sleep -Milliseconds 100
        }
    } while ($true)

    try {
        $holder = [ordered]@{
            schemaVersion = 1
            runId = $RunId
            processId = $PID
            command = $Command
            acquiredUtc = [DateTime]::UtcNow.ToString('O')
        }
        Write-JetAtomicJson -Path $holderPath -Value $holder
        return [pscustomobject]@{
            Acquired = $true
            Stream = $stream
            HolderPath = $holderPath
            Holder = $holder
        }
    }
    catch {
        $stream.Dispose()
        throw
    }
}

function Exit-JetExclusiveLock {
    param(
        [Parameter(Mandatory = $true)] $Lock,
        [Parameter(Mandatory = $true)] [string] $RunId
    )

    $result = [ordered]@{
        status = 'passed'
        released = $false
        holderRemoved = $false
        reason = $null
    }

    try {
        if (-not (Test-Path -LiteralPath $Lock.HolderPath -PathType Leaf)) {
            $result.status = 'failed'
            $result.reason = 'holder_missing'
        }
        else {
            $holder = Get-Content -LiteralPath $Lock.HolderPath -Raw -Encoding utf8 | ConvertFrom-Json
            if ($holder.runId -cne $RunId) {
                $result.status = 'failed'
                $result.reason = 'holder_mismatch'
            }
            else {
                [IO.File]::Delete($Lock.HolderPath)
                $result.holderRemoved = $true
            }
        }
    }
    catch {
        $result.status = 'failed'
        $result.reason = 'holder_cleanup_exception'
        $result.errorType = $_.Exception.GetType().Name
    }
    finally {
        try {
            $Lock.Stream.Dispose()
            $result.released = $true
        }
        catch {
            $result.status = 'failed'
            $result.reason = 'lock_release_exception'
            $result.errorType = $_.Exception.GetType().Name
        }
    }

    return $result
}
