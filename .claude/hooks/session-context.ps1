#requires -Version 7.4

<#
.SYNOPSIS
    SessionStart hook：把儲存庫的實際狀態放進新 session 的開場脈絡。

.DESCRIPTION
    `AGENTS.md` 要求接手時直接查 Git，不能只依長期文件判斷 branch、HEAD 與工作樹。這個 hook 把那次
    查詢自動化，並附上最近一次各命令的驗證收據，讓新 session 一開始就知道現況與上一個續接點。

    只讀取 Git 中繼資料與 `artifacts/harness/runs/` 的收據。不讀取任何私人案件目錄，也不輸出檔案內容。
    任何一步失敗都靜默略過，不讓 hook 影響 session 啟動。
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false, $false)
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8

function Write-HookResult {
    param([string] $Context)

    $payload = [ordered]@{ additionalContext = $Context }
    [Console]::Out.WriteLine(($payload | ConvertTo-Json -Depth 4 -Compress))
    exit 0
}

try {
    $root = $env:CLAUDE_PROJECT_DIR
    if ([string]::IsNullOrWhiteSpace($root) -or -not (Test-Path -LiteralPath $root -PathType Container)) {
        exit 0
    }

    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('## JET 儲存庫現況（由 SessionStart hook 於本次 session 實際查得）')
    $lines.Add('')

    # ---- Git 狀態 ----------------------------------------------------------
    $branch = (& git -C $root rev-parse --abbrev-ref HEAD 2>$null)
    $head = (& git -C $root rev-parse --short HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branch)) {
        Write-HookResult -Context '無法讀取 Git 狀態，接手前請自行以 git 指令核對 branch、HEAD 與工作樹。'
    }

    $porcelain = @(& git -C $root status --porcelain 2>$null)
    $modified = @($porcelain | Where-Object { $_ -notmatch '^\?\?' }).Count
    $untracked = @($porcelain | Where-Object { $_ -match '^\?\?' }).Count

    $upstream = (& git -C $root rev-parse --abbrev-ref '@{upstream}' 2>$null)
    $sync = '沒有追蹤的遠端分支'
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($upstream)) {
        $counts = (& git -C $root rev-list --left-right --count "$upstream...HEAD" 2>$null)
        if ($LASTEXITCODE -eq 0 -and $counts -match '^(\d+)\s+(\d+)$') {
            $behind = [int]$Matches[1]
            $ahead = [int]$Matches[2]
            $sync = if ($behind -eq 0 -and $ahead -eq 0) { "與 $upstream 同步" }
                    else { "相對 $upstream 領先 $ahead、落後 $behind" }
        }
    }

    $treeState = if ($modified -eq 0 -and $untracked -eq 0) {
        '乾淨'
    } else {
        "$modified 個已追蹤檔案有改動、$untracked 個未追蹤項目"
    }

    $lines.Add("- Branch ``$branch``，HEAD ``$head``，$sync。")
    $lines.Add("- 工作樹：$treeState。")

    # ---- 最近一次各命令的驗證收據 ------------------------------------------
    $runsRoot = Join-Path $root 'artifacts/harness/runs'
    if (Test-Path -LiteralPath $runsRoot -PathType Container) {
        $latest = [ordered]@{}
        $recent = Get-ChildItem -LiteralPath $runsRoot -Directory -ErrorAction SilentlyContinue |
                  Sort-Object Name -Descending |
                  Select-Object -First 80
        foreach ($dir in $recent) {
            $receiptPath = Join-Path $dir.FullName 'receipt.json'
            if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) { continue }
            try {
                $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding utf8 | ConvertFrom-Json
            } catch { continue }
            $command = [string]$receipt.command
            if ([string]::IsNullOrWhiteSpace($command) -or $latest.Contains($command)) { continue }
            $completed = $null
            try {
                $completed = ([datetime]$receipt.completedUtc).ToUniversalTime().ToString('yyyy-MM-dd')
            } catch { $completed = $null }
            $latest[$command] = [pscustomobject]@{
                Status    = [string]$receipt.status
                Completed = $completed
                Head      = [string]$receipt.repository.head
            }
        }

        if ($latest.Count -gt 0) {
            $lines.Add('- 最近一次各命令的收據（只描述當次執行，候選內容變動後不能沿用）：')
            foreach ($command in $latest.Keys) {
                $entry = $latest[$command]
                $when = if ($entry.Completed) { $entry.Completed } else { '時間不明' }
                $sameHead = if ($entry.Head -and $entry.Head -eq (& git -C $root rev-parse HEAD 2>$null)) {
                    '同一 HEAD'
                } else {
                    '不同 HEAD'
                }
                $lines.Add("  - $command : $($entry.Status)（$when，$sameHead）")
            }
        }
    } else {
        $lines.Add('- `artifacts/harness/runs/` 不存在：這台機器還沒有任何本機驗證收據。')
    }

    $lines.Add('')
    $lines.Add('接續大型開發前，先讀 `docs/development-status.md` 的「目前大型計畫」與「已知但延後的事項」，')
    $lines.Add('再依 `docs/development-workflow.md` 核對上一個續接點。上面的收據是本機執行證據，不是專案記憶；')
    $lines.Add('`artifacts/` 由 Git 忽略，換機或 fresh clone 後不會存在。')

    Write-HookResult -Context ($lines -join "`n")
}
catch {
    exit 0
}
