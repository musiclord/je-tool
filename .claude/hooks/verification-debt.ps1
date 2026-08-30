#requires -Version 7.4

<#
.SYNOPSIS
    Stop hook：收尾時列出改了但還沒重跑的驗證。

.DESCRIPTION
    `AGENTS.md` 的收尾規則要求回報實際驗證與未能確認的項目，並且未執行 build、測試、GUI、Excel 或真實
    資料驗收時必須明說。這個 hook 把「哪些改動還沒有對應的通過收據」這件事機械化，避免靠記憶判斷。

    判定方式：比較工作樹中已改動檔案的最後修改時間，與 `artifacts/harness/runs/` 中對應命令最近一次
    `passed` 收據的完成時間。收據較舊就算未重跑。

    這是提醒，不是關卡；它不會阻止 session 結束，也不會替任何命令背書。只讀 Git 中繼資料與收據，
    不讀取私人案件目錄——那些目錄由 `.gitignore` 排除，不會出現在工作樹清單中。
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false, $false)
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8

try {
    $root = $env:CLAUDE_PROJECT_DIR
    if ([string]::IsNullOrWhiteSpace($root) -or -not (Test-Path -LiteralPath $root -PathType Container)) {
        exit 0
    }

    $porcelain = @(& git -C $root status --porcelain 2>$null)
    if ($LASTEXITCODE -ne 0 -or $porcelain.Count -eq 0) { exit 0 }

    $changed = foreach ($line in $porcelain) {
        if ($line.Length -le 3) { continue }
        $path = $line.Substring(3).Trim('"')
        if ($path -match '\s->\s') { $path = ($path -split '\s->\s')[-1] }
        $path.Replace('\', '/')
    }
    $changed = @($changed | Where-Object { $_ })
    if ($changed.Count -eq 0) { exit 0 }

    $policyPath = Join-Path $root 'tools/harness/documentation-check.json'
    $managedDocs = @()
    if (Test-Path -LiteralPath $policyPath -PathType Leaf) {
        try {
            $managedDocs = @((Get-Content -LiteralPath $policyPath -Raw -Encoding utf8 | ConvertFrom-Json).files)
        } catch { $managedDocs = @() }
    }

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
    if ($relevant.Count -eq 0) { exit 0 }

    # 找出每個命令最近一次 passed 的收據時間。
    $lastPassed = @{}
    $runsRoot = Join-Path $root 'artifacts/harness/runs'
    if (Test-Path -LiteralPath $runsRoot -PathType Container) {
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
        if ($covered) { continue }

        $sample = ($paths | Select-Object -First 3) -join '、'
        if ($paths.Count -gt 3) { $sample += "（共 $($paths.Count) 個）" }
        $outstanding.Add("- $command ：$($areas[$command].Label)有改動但沒有更新的通過收據。改到 $sample")
    }

    if ($outstanding.Count -eq 0) { exit 0 }

    $body = @"
以下改動還沒有對應的新通過收據，回報時要照 AGENTS.md 的收尾規則明說「未執行」而不是略過：

$($outstanding -join "`n")

執行方式：``pwsh -NoProfile -File tools/verify.ps1 -Command <命令>``。
沒有使用者明確指令時，最多整理到可提交狀態，不進行暫存、提交或推送。
"@

    $result = [ordered]@{
        additionalContext = $body
        systemMessage     = "JET：有 $($outstanding.Count) 個範圍改動後尚未重跑對應驗證。"
    }
    [Console]::Out.WriteLine(($result | ConvertTo-Json -Depth 4 -Compress))
    exit 0
}
catch {
    exit 0
}
