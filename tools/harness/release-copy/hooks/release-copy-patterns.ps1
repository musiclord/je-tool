# 發布副本的同步工具，以及發布副本本機的提交與推送前檢查共用的樣式。
# 同步工具裝檢查時，把這個檔和 release-copy-check.ps1 一起複製到發布副本的 .git/hooks。
# 「AI 工具的名稱」「驗證框架的名稱」「看不見的字元」和開發儲存庫「原始碼不寫開發紀錄的檢查」的同類樣式相同，
# 這裡另外擋驗證框架的檔名、開發文件的名稱與 AI 工具的署名，因為發布副本也帶說明檔與送審文件。
# 字元一律用跳脫寫法寫出代碼，這個檔本身才不帶看不見的字元。

$script:ReleaseCopyCjkOrSpace = '\s\u3000-\u303F\u4E00-\u9FFF\uFF00-\uFFEF'

$script:ReleaseCopyTextChecks = @(
    [pscustomobject]@{
        Name = 'AI 工具的名稱'
        # 瀏覽器的 userAgent、HTTP 的 User-Agent 標頭與物件屬性 agent 不算；AI 與 LLM 前後要是空白或中文才算。
        Pattern = [regex]::new(
            '(?i:claude|codex|copilot|chatgpt|openai|gemini|(?<!phil)anthropic|\bgpt-?\d)' +
            '|(?<![A-Za-z.\-])(?i:agents?)(?![A-Za-z])' +
            "|(?<![^$script:ReleaseCopyCjkOrSpace])(?:AI|LLM)(?![^$script:ReleaseCopyCjkOrSpace\-])",
            'CultureInvariant')
    }
    [pscustomobject]@{
        Name = '驗證框架的名稱'
        # 網址裡的 tools 資料夾不算。
        Pattern = [regex]::new(
            '(?i:harness|gui-driver|verify\.ps1|receipt\.json|lanes\.json)|OwnedGuiRun|AgentGui|DevelopmentRepository' +
            '|驗證框架|瀏覽器主機|預覽主機|(?<![A-Za-z0-9_./\-])tools[/\\]|\.agents[/\\]',
            'CultureInvariant')
    }
    [pscustomobject]@{
        Name = '開發紀錄的寫法'
        Pattern = [regex]::new(
            '(?:裁定|[Rr]uling)\s*\d{4}-\d{2}-\d{2}-\d{2}|(?<!\d)20\d{6}-\d{9}-[0-9a-f]{32}' +
            '|docs/(?:specs|history)/|(?i:rulings|glossary|development-status|jet-guide|business-logic-provenance)\.md' +
            '|第一次失敗|首次失敗',
            'CultureInvariant')
    }
)

# 不換行空白、零寬字元、改變文字方向的控制字元、標籤字元與其他控制字元。中文常用的全形空白看得見，不算。
$script:ReleaseCopyInvisible = [regex]::new(
    '[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F-\u009F\u00A0\u00AD\u034F\u061C\u115F\u1160\u17B4\u17B5\u180B-\u180F' +
    '\u2000-\u200F\u2028-\u202F\u205F-\u206F\u3164\uFE00-\uFE0F\uFEFF\uFFA0\uFFF9-\uFFFB]|\uDB40[\uDC00-\uDC7F\uDD00-\uDDEF]',
    'CultureInvariant')

# 發布副本不帶的路徑：AI 工具的設定與指引檔、開發儲存庫才有的資料夾、建置與測試的輸出，以及資料庫與記錄檔。
# .github 底下只擋 AI 工具的指引檔；GitHub 的 Dependabot 設定與 workflow 可以放。
$script:ReleaseCopyForbiddenPath = [regex]::new(
    '(^|/)\.(agents|claude|codex|cursor|windsurf|continue|gemini|roo|kiro|junie|amazonq|clinerules|aider[^/]*)(/|$)' +
    '|(^|/)\.github/(?:(?:instructions|prompts|agents|chatmodes|copilot)/|copilot-instructions\.md$|workflows/copilot-setup-steps\.ya?ml$)' +
    '|(^|/)(AGENTS|CLAUDE|CLAUDE\.local|GEMINI|CONVENTIONS)\.md$' +
    '|(^|/)(\.cursorrules|\.windsurfrules|\.roorules|\.mcp\.json|\.cursorignore|\.codeiumignore)$|(^|/)\.vscode/mcp\.json$' +
    '|^(tools|local|legacy|artifacts|src)/|^docs/(specs|history)/' +
    '|(^|/)(bin|obj|node_modules|TestResults)/|\.(trx|binlog|log|db|db-wal|db-shm|db-journal|pdb|user|suo)$|(^|/)receipt\.json$',
    'CultureInvariant, IgnoreCase')

# 提交訊息裡 AI 工具留下的署名：Co-Authored-By 行、連到 AI 工具工作紀錄的 Agent-Logs-Url 行、
# Generated with 字樣與機器人表情符號。
$script:ReleaseCopyAttribution = [regex]::new(
    '(?im)^\s*(?:co-authored-by|agent-logs-url)\s*:|generated (?:with|by)|\uD83E\uDD16',
    'CultureInvariant')

function Get-ReleaseCopyTextFinding {
    # 回傳一段文字裡找到的類別與行號，同一類別在同一行只回傳一次。不回傳找到的字。
    param([string] $Text, [switch] $IncludeInvisible)
    $checks = @($script:ReleaseCopyTextChecks)
    if ($IncludeInvisible) {
        $checks += [pscustomobject]@{ Name = '看不見的字元'; Pattern = $script:ReleaseCopyInvisible }
    }
    $found = @(foreach ($check in $checks) {
            foreach ($match in $check.Pattern.Matches($Text)) {
                [pscustomobject]@{ Name = $check.Name; Offset = $match.Index }
            }
        })
    if ($found.Count -eq 0) { return }

    # 有找到時才算每一行的起點，大檔案沒有問題時不必逐字走過。
    $lineStarts = [Collections.Generic.List[int]]::new()
    $lineStarts.Add(0)
    $position = $Text.IndexOf("`n")
    while ($position -ge 0) {
        $lineStarts.Add($position + 1)
        $position = $Text.IndexOf("`n", $position + 1)
    }
    $seen = [Collections.Generic.HashSet[string]]::new()
    foreach ($item in $found) {
        $index = $lineStarts.BinarySearch($item.Offset)
        if ($index -lt 0) { $index = (-bnot $index) - 1 }
        $line = $index + 1
        if ($seen.Add("$($item.Name)`t$line")) {
            [pscustomobject]@{ Name = $item.Name; Line = $line }
        }
    }
}
