#requires -Version 7.4

<#
.SYNOPSIS
    Stop hook：收尾時提醒還沒重跑的驗證與測試斷言變動。只提醒，不擋住結束。

.DESCRIPTION
    `AGENTS.md` 的收尾規則要求回報實際驗證與未能確認的項目。這個 hook 把兩件事機械化，結果以一行提醒交給
    使用者，讓人判斷 agent 的收尾回報有沒有漏：

    1. 驗證欠帳。比較工作樹中已改動檔案的最後修改時間，與 `artifacts/harness/runs/` 中對應命令最近一次
       `passed` 收據的完成時間；收據較舊就算未重跑。
    2. 測試斷言變動。`src/JET/tests/` 有改動時，比對 HEAD 與工作樹，數出移除的 `Assert.`、移除的
       `[Fact]` 或 `[Theory]`，以及新增的 `Skip =`。對應 `AGENTS.md`「測試」邊界。

    2026-10-02 起依使用者裁定不再擋住結束：先前「第一次擋一次」的做法以 session 為單位記狀態，
    長時間工作每改一批就被擋，新開的 session（包含唯讀的）結束時也會再被擋一次，摩擦大於幫助。
    現在兩個平台都只收到 `systemMessage`。這個 hook 只讀 Git 中繼資料與收據，不讀取私人案件目錄，
    不替任何命令背書；任何一步失敗都靜默結束。
#>

. (Join-Path $PSScriptRoot 'hook-common.ps1')

try {
    $payload = Read-JetHookPayload
    $root = Resolve-JetHookRoot -Payload $payload
    if ($null -eq $root) { exit 0 }

    $porcelain = @(& git -C $root status --porcelain -uall 2>$null)
    if ($LASTEXITCODE -ne 0 -or $porcelain.Count -eq 0) { exit 0 }

    $changed = [Collections.Generic.List[string]]::new()
    foreach ($line in $porcelain) {
        if ($line.Length -le 3) { continue }
        $path = $line.Substring(3).Trim('"')
        if ($path -match '\s->\s') { $path = ($path -split '\s->\s')[-1] }
        $path = $path.Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($path)) { continue }
        $changed.Add($path)
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
            # 只跑單一情境或單一篩選的診斷收據不算該命令通過。
            $partialProperty = $receipt.PSObject.Properties['partial']
            if ($null -ne $partialProperty -and [bool]$partialProperty.Value) { continue }
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

    $outstanding = [Collections.Generic.List[string]]::new()
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
        if (-not $covered) { $outstanding.Add($command) }
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
                $driftNote = "測試斷言相對 HEAD 有變動：移除 Assert $assertRemoved 行、移除 [Fact] 或 [Theory] $factRemoved 個、新增 Skip $skipAdded 個，回報時要寫明原因"
            }
        }
    }

    if ($outstanding.Count -eq 0 -and $null -eq $driftNote) { exit 0 }

    $parts = [Collections.Generic.List[string]]::new()
    if ($outstanding.Count -gt 0) {
        $parts.Add("改過的範圍還沒有新的通過收據：$($outstanding -join '、')（回報時要寫明未執行與原因）")
    }
    if ($null -ne $driftNote) { $parts.Add($driftNote) }
    $result = [ordered]@{ systemMessage = "JET 收尾提醒：$($parts -join '；')。" }
    [Console]::Out.WriteLine(($result | ConvertTo-Json -Depth 4 -Compress))
    exit 0
}
catch {
    exit 0
}
