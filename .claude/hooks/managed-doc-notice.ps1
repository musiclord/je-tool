#requires -Version 7.4

<#
.SYNOPSIS
    PostToolUse hook：改到受管文件時，提醒還沒完成的文件收尾步驟。

.DESCRIPTION
    `AGENTS.md` 與 `docs/README.md` 要求修改現行文件後執行 `tools/verify.ps1 -Command Documentation`，
    再把改動的段落完整讀一次。自動檢查只擋缺漏與已確認錯誤的現況，取代不了人工閱讀。

    這個 hook 只在被編輯的檔案列在 `tools/harness/documentation-check.json` 時才出聲，其他檔案完全安靜。
    它只提醒，不阻擋；判斷仍由人與 agent 負責。
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false, $false)
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8

try {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }
    $payload = $raw | ConvertFrom-Json

    $root = $env:CLAUDE_PROJECT_DIR
    if ([string]::IsNullOrWhiteSpace($root)) { exit 0 }

    $filePath = [string]$payload.tool_input.file_path
    if ([string]::IsNullOrWhiteSpace($filePath)) { exit 0 }

    $rootFull = (Resolve-Path -LiteralPath $root).Path.TrimEnd('\', '/')
    try {
        $fileFull = (Resolve-Path -LiteralPath $filePath -ErrorAction Stop).Path
    } catch { exit 0 }

    if (-not $fileFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { exit 0 }
    $relative = $fileFull.Substring($rootFull.Length).TrimStart('\', '/').Replace('\', '/')

    $policyPath = Join-Path $root 'tools/harness/documentation-check.json'
    if (-not (Test-Path -LiteralPath $policyPath -PathType Leaf)) { exit 0 }
    $policy = Get-Content -LiteralPath $policyPath -Raw -Encoding utf8 | ConvertFrom-Json
    $managed = @($policy.files)

    if ($managed -notcontains $relative) { exit 0 }

    $context = @"
剛改動的 ``$relative`` 是 Documentation 檢查的受管文件。收尾前逐項確認：

1. 事實保真：本次寫入的每個檔名、數字、日期、查核結論與裁定，都對照過本次實際讀到的原文。引用歷史
   紀錄時原文名稱逐字照抄，沒有換成「等義」的現行名稱；有新舊對應時已另外寫明。找不到出處的聲明
   已刪掉或標成待確認——「查不到」是合法答案，不以合理推測填空。
2. 執行 ``pwsh -NoProfile -File tools/verify.ps1 -Command Documentation``。
3. 把本次改動的段落完整讀一次。命令只抓缺檔、必要指向缺漏與已確認過時的事實；風格疑點只是 warning，
   通過不代表句子自然或資訊有依據。
4. 確認改寫沒有動到數字、條件、例外、不確定性、正規名稱、schema、UI 行為或使用者裁定，也沒有創造
   新名詞或把暫時性描述升格成正式規範。
"@

    $result = [ordered]@{ additionalContext = $context }
    [Console]::Out.WriteLine(($result | ConvertTo-Json -Depth 4 -Compress))
    exit 0
}
catch {
    exit 0
}
