#requires -Version 7.4

<#
.SYNOPSIS
    Stop hook：收尾前擋一次還沒重跑的驗證，並點出測試斷言變動與未列入候選清單的新檔。

.DESCRIPTION
    `AGENTS.md` 的收尾規則要求回報實際驗證與未能確認的項目，未執行時必須明說。這個 hook 把三件事機械化：

    1. 驗證欠帳。比較工作樹中已改動檔案的最後修改時間，與 `artifacts/harness/runs/` 中對應命令最近一次
       `passed` 收據的完成時間；收據較舊就算未重跑。有欠帳時第一次會擋住結束（exit code 2），把清單交回
       給 Claude。同一個 session 裡，同一個命令在同一個檔案時間戳下只擋一次，之後只提醒；狀態記在
       `artifacts/harness/hooks/`。Claude Code 自己也會在連續擋 8 次後放行，所以不會無限循環。
    2. 測試斷言變動。`src/JET/tests/` 有改動時，比對 HEAD 與工作樹，數出移除的 `Assert.`、移除的
       `[Fact]` 或 `[Theory]`，以及新增的 `Skip =`。只提醒，對應 `AGENTS.md`「測試」邊界。
    3. 未列入候選清單的新檔。未追蹤的檔案若不在 `docs/first-root-commit-candidate.txt`，提交後
       `ReleaseCandidate` 會以 candidate_manifest_mismatch 失敗。只提醒。

    只讀 Git 中繼資料、收據與候選清單，不讀取私人案件目錄；那些目錄由 `.gitignore` 排除，不會出現在工作樹
    清單中。任何一步失敗都靜默放行，不影響 session。這個 hook 不替任何命令背書。
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false, $false)
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8

function Get-PayloadValue {
    param($Payload, [string] $Name)

    if ($null -eq $Payload) { return $null }
    $property = $Payload.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

try {
    $root = $env:CLAUDE_PROJECT_DIR
    if ([string]::IsNullOrWhiteSpace($root) -or -not (Test-Path -LiteralPath $root -PathType Container)) {
        exit 0
    }

    # Stop hook 的輸入在 stdin：stop_hook_active 為 true 表示上一次 Stop 已經被這個 hook 擋過。
    $payload = $null
    try {
        $raw = [Console]::In.ReadToEnd()
        if (-not [string]::IsNullOrWhiteSpace($raw)) { $payload = $raw | ConvertFrom-Json }
    } catch { $payload = $null }
    $stopHookActive = [bool](Get-PayloadValue -Payload $payload -Name 'stop_hook_active')
    $sessionId = [string](Get-PayloadValue -Payload $payload -Name 'session_id')
    if ([string]::IsNullOrWhiteSpace($sessionId)) { $sessionId = 'unknown' }
    $sessionId = [regex]::Replace($sessionId, '[^A-Za-z0-9_-]', '_')

    $porcelain = @(& git -C $root status --porcelain -uall 2>$null)
    if ($LASTEXITCODE -ne 0 -or $porcelain.Count -eq 0) { exit 0 }

    $changed = [Collections.Generic.List[string]]::new()
    $untracked = [Collections.Generic.List[string]]::new()
    foreach ($line in $porcelain) {
        if ($line.Length -le 3) { continue }
        $path = $line.Substring(3).Trim('"')
        if ($path -match '\s->\s') { $path = ($path -split '\s->\s')[-1] }
        $path = $path.Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($path)) { continue }
        $changed.Add($path)
        if ($line.StartsWith('??')) { $untracked.Add($path) }
    }
    if ($changed.Count -eq 0) { exit 0 }

    $policyPath = Join-Path $root 'tools/harness/documentation-check.json'
    $managedDocs = @()
    if (Test-Path -LiteralPath $policyPath -PathType Leaf) {
        try {
            $managedDocs = @((Get-Content -LiteralPath $policyPath -Raw -Encoding utf8 | ConvertFrom-Json).files)
        } catch { $managedDocs = @() }
    }

    # ---- 1. 驗證欠帳 -------------------------------------------------------
    # 每個範圍對應到負責它的正式命令。
    $areas = [ordered]@{
        'Documentation' = @{ Label = '受管文件'; Paths = @() }
        'Contract'      = @{ Label = '驗證框架本身（tools/）'; Paths = @() }
        'Public'        = @{ Label = '產品程式與測試（src/）'; Paths = @() }
    }
    foreach ($path in $changed) {
        if ($managedDocs -contains $path) { $areas['Documentation'].Paths += $path; continue }
        if ($path.StartsWith('tools/')) { $areas['Contract'].Paths += $path; continue }
        if ($path.StartsWith('src/')) { $areas['Public'].Paths += $path; continue }
    }
    $relevant = @($areas.Keys | Where-Object { $areas[$_].Paths.Count -gt 0 })

    # 找出每個命令最近一次 passed 的收據時間。
    $lastPassed = @{}
    $runsRoot = Join-Path $root 'artifacts/harness/runs'
    if ($relevant.Count -gt 0 -and (Test-Path -LiteralPath $runsRoot -PathType Container)) {
        $recent = Get-ChildItem -LiteralPath $runsRoot -Directory -ErrorAction SilentlyContinue |
                  Sort-Object Name -Descending |
                  Select-Object -First 120
        foreach ($dir in $recent) {
            $receiptPath = Join-Path $dir.FullName 'receipt.json'
            if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) { continue }
            try {
                $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding utf8 | ConvertFrom-Json
            } catch { continue }
            if ([string]$receipt.status -cne 'passed') { continue }
            $command = [string]$receipt.command
            if ([string]::IsNullOrWhiteSpace($command) -or $lastPassed.ContainsKey($command)) { continue }
            try {
                $lastPassed[$command] = [datetime]::Parse(
                    [string]$receipt.completedUtc, $null,
                    [Globalization.DateTimeStyles]::AdjustToUniversal -bor [Globalization.DateTimeStyles]::AssumeUniversal)
            } catch { continue }
        }
    }

    # ReleaseCandidate 依序涵蓋 Contract、Documentation、Public，可以替它們背書。
    $covers = @{
        'Documentation' = @('Documentation', 'ReleaseCandidate')
        'Contract'      = @('Contract', 'ReleaseCandidate')
        'Public'        = @('Public', 'ReleaseCandidate')
    }

    $outstanding = [Collections.Generic.List[object]]::new()
    foreach ($command in $relevant) {
        $paths = @($areas[$command].Paths)
        $newest = [datetime]::MinValue
        foreach ($path in $paths) {
            $full = Join-Path $root $path
            if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { continue }
            $stamp = (Get-Item -LiteralPath $full).LastWriteTimeUtc
            if ($stamp -gt $newest) { $newest = $stamp }
        }
        if ($newest -eq [datetime]::MinValue) { continue }

        $covered = $false
        foreach ($candidate in $covers[$command]) {
            if ($lastPassed.ContainsKey($candidate) -and $lastPassed[$candidate] -gt $newest) {
                $covered = $true
                break
            }
        }
        if ($covered) { continue }

        $sample = ($paths | Select-Object -First 3) -join '、'
        if ($paths.Count -gt 3) { $sample += "（共 $($paths.Count) 個）" }
        $outstanding.Add([pscustomobject]@{
            Command = $command
            Line = "- $command ：$($areas[$command].Label)有改動但沒有更新的通過收據。改到 $sample"
            NewestTicks = [string]$newest.Ticks
        })
    }

    # ---- 2. 測試斷言變動 ---------------------------------------------------
    $driftNote = $null
    if (@($changed | Where-Object { $_.StartsWith('src/JET/tests/') }).Count -gt 0) {
        $diff = @(& git -C $root diff HEAD --unified=0 --no-color -- src/JET/tests 2>$null)
        if ($LASTEXITCODE -eq 0) {
            $assertRemoved = 0; $factRemoved = 0; $skipAdded = 0
            foreach ($line in $diff) {
                if ($line.StartsWith('---') -or $line.StartsWith('+++') -or $line.StartsWith('@@') -or
                    $line.StartsWith('diff ') -or $line.StartsWith('index ')) { continue }
                if ($line.StartsWith('-')) {
                    if ([regex]::IsMatch($line, '\bAssert\.')) { $assertRemoved++ }
                    if ([regex]::IsMatch($line, '\[\s*(Fact|Theory)\b')) { $factRemoved++ }
                }
                elseif ($line.StartsWith('+')) {
                    if ([regex]::IsMatch($line, '\bSkip\s*=')) { $skipAdded++ }
                }
            }
            if ($assertRemoved -gt 0 -or $factRemoved -gt 0 -or $skipAdded -gt 0) {
                $driftNote = "測試斷言變動（HEAD 與工作樹比對 src/JET/tests/）：移除 Assert $assertRemoved 行、" +
                    "移除 [Fact]/[Theory] $factRemoved 個、新增 Skip $skipAdded 個。AGENTS.md「測試」不允許為了讓測試" +
                    "通過而放寬既有斷言；若有正當理由，在回覆裡寫明，並保留第一次失敗的證據。"
            }
        }
    }

    # ---- 3. 未列入候選清單的新檔 -------------------------------------------
    $manifestNote = $null
    if ($untracked.Count -gt 0) {
        $manifestPath = Join-Path $root 'docs/first-root-commit-candidate.txt'
        if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
            $listed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($entry in (Get-Content -LiteralPath $manifestPath -Encoding utf8)) {
                $trimmed = ([string]$entry).Trim()
                if ($trimmed) { [void]$listed.Add($trimmed) }
            }
            $missing = @($untracked | Where-Object { -not $listed.Contains($_) })
            if ($missing.Count -gt 0) {
                $sample = ($missing | Select-Object -First 5) -join '、'
                if ($missing.Count -gt 5) { $sample += "（共 $($missing.Count) 個）" }
                $manifestNote = "未列入候選清單的新檔：$sample。要一起提交就依 ordinal 排序加進 " +
                    "docs/first-root-commit-candidate.txt，否則提交後 ReleaseCandidate 會以 candidate_manifest_mismatch 失敗；" +
                    "不該提交的暫存檔則刪掉。"
            }
        }
    }

    if ($outstanding.Count -eq 0 -and $null -eq $driftNote -and $null -eq $manifestNote) { exit 0 }

    # ---- 擋一次的狀態 ------------------------------------------------------
    $stateDirectory = Join-Path $root 'artifacts/harness/hooks'
    $statePath = Join-Path $stateDirectory "verification-debt-$sessionId.json"
    $blockedState = @{}
    if (Test-Path -LiteralPath $statePath -PathType Leaf) {
        try {
            $loaded = Get-Content -LiteralPath $statePath -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable
            if ($loaded -is [Collections.IDictionary] -and $loaded.Contains('blocked') -and
                $loaded['blocked'] -is [Collections.IDictionary]) {
                $blockedState = $loaded['blocked']
            }
        } catch { $blockedState = @{} }
    }
    $newBlocks = @($outstanding | Where-Object {
            -not $blockedState.Contains($_.Command) -or [string]$blockedState[$_.Command] -ne $_.NewestTicks })
    $shouldBlock = ($newBlocks.Count -gt 0) -and -not $stopHookActive

    $sections = [Collections.Generic.List[string]]::new()
    if ($outstanding.Count -gt 0) {
        $debtLines = ($outstanding | ForEach-Object { $_.Line }) -join "`n"
        $debtBody = @"
以下改動還沒有對應的新通過收據，回報時要照 AGENTS.md 的收尾規則明說「未執行」而不是略過：

$debtLines

執行方式：``pwsh -NoProfile -File tools/verify.ps1 -Command <命令>``。
沒有使用者明確指令時，最多整理到可提交狀態，不進行暫存、提交或推送。
"@
        if ($shouldBlock) {
            $debtBody += "`n這次結束被擋住一次。現在不適合執行的話，在回覆裡逐項寫明「未執行」與原因再結束；同一狀態不會再擋。"
        }
        $sections.Add($debtBody)
    }
    if ($null -ne $driftNote) { $sections.Add($driftNote) }
    if ($null -ne $manifestNote) { $sections.Add($manifestNote) }
    $body = $sections -join "`n`n"

    if ($shouldBlock) {
        foreach ($item in $outstanding) { $blockedState[$item.Command] = $item.NewestTicks }
        try {
            [void][IO.Directory]::CreateDirectory($stateDirectory)
            [IO.File]::WriteAllText(
                $statePath,
                (([ordered]@{ blocked = $blockedState }) | ConvertTo-Json -Depth 4) + "`n",
                $utf8)
        } catch { }
        [Console]::Error.WriteLine($body)
        exit 2
    }

    $result = [ordered]@{
        additionalContext = $body
        systemMessage     = "JET：收尾提醒 $($sections.Count) 項（驗證欠帳 $($outstanding.Count) 個範圍）。"
    }
    [Console]::Out.WriteLine(($result | ConvertTo-Json -Depth 4 -Compress))
    exit 0
}
catch {
    exit 0
}
