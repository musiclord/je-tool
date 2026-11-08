#requires -Version 7.4
<#
.SYNOPSIS
    將 tools/verify.ps1 的 Public 或 Focused 執行結果轉成 HTML 測試報告。
.DESCRIPTION
    讀取指定的執行收據、原始測試結果（TRX）與測試摘要，核對三者的數字一致，
    且收據的提交編號與 -ExpectedSha 相同後，產生一份可離線閱讀的報告。
    本工具不執行測試，也不讀取私人案件的結果。
    結束碼 0 表示報告已產生，測試是否通過以報告內容為準；輸入不符合條件時回傳 2。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ReceiptPath,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-fA-F]{40}$')] [string] $ExpectedSha,
    [Parameter(Mandatory)] [string] $OutputPath,
    [string] $RepositoryRoot = (Join-Path $PSScriptRoot '../../..')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Deny([string] $Code) { throw [IO.InvalidDataException]::new("SAT_$Code") }
function Full([string] $Path) { [IO.Path]::GetFullPath($Path) }
function IsInside([string] $Root, [string] $Path) {
    $prefix = $Root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    return $Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}
function NoLinks([string] $Path) {
    # 包含父目錄，避免 junction 或 symlink 把讀取導向其他位置。
    $cursor = $Path
    while (-not [string]::IsNullOrEmpty($cursor)) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Deny 'LINK_PATH' }
        }
        $next = [IO.Path]::GetDirectoryName($cursor)
        if ($next -eq $cursor) { break }
        $cursor = $next
    }
}
function RelativeFile([string] $Root, [string] $Relative, [string] $Bound, [string] $Leaf) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or
        $Relative.Contains(':')) { Deny 'PATH' }
    $resolved = Full (Join-Path $Root $Relative)
    if (-not (IsInside $Bound $resolved) -or [IO.Path]::GetFileName($resolved) -cne $Leaf) { Deny 'PATH' }
    NoLinks $resolved
    if (-not [IO.File]::Exists($resolved)) { Deny 'MISSING_EVIDENCE' }
    return $resolved
}
function JsonFile([string] $Path, [long] $Limit) {
    NoLinks $Path
    $length = (Get-Item -LiteralPath $Path).Length
    if ($length -le 0 -or $length -gt $Limit) { Deny 'SIZE' }
    return (Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 64)
}
function PublicData($Object) {
    if ($Object -isnot [Collections.IDictionary] -or -not $Object.Contains('privateData') -or
        $Object.privateData -isnot [Collections.IDictionary] -or
        $Object.privateData.pathInspected -isnot [bool] -or $Object.privateData.pathInspected) { Deny 'PRIVATE_DATA' }
}
function CountValue($Counters, [string] $Key) {
    if ($Counters -isnot [Collections.IDictionary] -or -not $Counters.Contains($Key)) { Deny 'COUNTERS' }
    $value = $Counters[$Key]
    if ($value -isnot [int] -and $value -isnot [long]) { Deny 'COUNTERS' }
    if ($value -lt 0 -or $value -gt 1000000) { Deny 'COUNTERS' }
    return [long]$value
}
function HtmlText($Value) { [Net.WebUtility]::HtmlEncode([string]$Value) }
function StatusText([string] $Status) {
    switch -CaseSensitive ($Status) {
        'passed' { '通過' }
        'failed' { '失敗' }
        'blocked' { '受阻' }
        'usage_error' { '輸入不合法' }
        'infrastructure_error' { '驗證工具錯誤' }
        default { Deny 'STATUS' }
    }
}
function UtcInstant($Value) {
    # ConvertFrom-Json turns ISO timestamps into DateTime values; keep them as UTC instants.
    if ($Value -is [DateTime]) { return [DateTimeOffset]::new($Value.ToUniversalTime()) }
    $styles = [Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal
    return [DateTimeOffset]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture, $styles)
}
function StepLabel([string] $Name) {
    switch -CaseSensitive ($Name) {
        'dotnet-restore' { '還原套件' }
        'dotnet-build' { '建置' }
        'public-tests' { '公開測試' }
        'public-frontend-mapping' { '前端操作對應檢查' }
        'public-frontend-preview' { '前端預覽檢查' }
        'focused-selected' { '指定測試' }
        'focused-guards' { '架構檢查' }
        default { $Name }
    }
}
function TrxCounters([string] $Path) {
    if ((Get-Item -LiteralPath $Path).Length -gt 67108864) { Deny 'SIZE' }
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 67108864
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    $counts = @{ total = 0L; executed = 0L; passed = 0L; failed = 0L; skipped = 0L }
    try {
        # 只核對結果數量，不擷取 TRX 的顯示名稱、訊息、stdout 或附件。
        while ($reader.Read()) {
            if ($reader.NodeType -eq [Xml.XmlNodeType]::Element -and
                $reader.LocalName -ceq 'UnitTestResult' -and
                $reader.NamespaceURI -ceq 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010') {
                $counts.total++
                switch -Regex ($reader.GetAttribute('outcome')) {
                    '^(Passed|Completed)$' { $counts.passed++; $counts.executed++; break }
                    '^Failed$' { $counts.failed++; $counts.executed++; break }
                    default { $counts.skipped++ }
                }
            }
        }
    } finally { $reader.Dispose() }
    return $counts
}

try {
    $repo = Full $RepositoryRoot
    NoLinks $repo
    $receiptFull = Full $ReceiptPath
    $run = [IO.Path]::GetDirectoryName($receiptFull)
    $artifacts = Full (Join-Path $repo 'artifacts/harness')
    if (-not (IsInside $artifacts $run) -or [IO.Path]::GetFileName($receiptFull) -cne 'receipt.json') { Deny 'RECEIPT_PATH' }
    $receipt = JsonFile $receiptFull 4194304
    if ($receipt.schemaVersion -ne 1 -or $receipt.command -cnotin @('Public', 'Focused')) { Deny 'COMMAND' }
    PublicData $receipt
    if ($receipt.repository.head -notmatch '^[0-9a-fA-F]{40}$' -or
        -not [string]::Equals($receipt.repository.head, $ExpectedSha, [StringComparison]::OrdinalIgnoreCase)) { Deny 'SHA_MISMATCH' }
    if ([IO.Path]::IsPathRooted([string]$receipt.runDirectory) -or ([string]$receipt.runDirectory).Contains(':') -or
        -not [string]::Equals((Full (Join-Path $repo $receipt.runDirectory)), $run, [StringComparison]::OrdinalIgnoreCase)) { Deny 'RUN_PATH' }
    if ($receipt.configuration -cnotin @('Debug', 'Release', 'AgentGuiTest')) { Deny 'CONFIGURATION' }
    if ($receipt.runId -notmatch '^[A-Za-z0-9-]{1,100}$') { Deny 'RUN_ID' }
    if ($receipt.command -ceq 'Focused' -and ([string]$receipt.filter -notmatch '^[A-Za-z0-9_.+]{1,200}$')) { Deny 'FILTER' }
    $startInstant = UtcInstant $receipt.startedUtc
    $start = $startInstant.ToString('u')
    $end = (UtcInstant $receipt.completedUtc).ToString('u')
    $mainStatus = StatusText $receipt.status
    $statusCodes = @{ passed = 0; failed = 1; blocked = 2; usage_error = 3; infrastructure_error = 4 }
    if ($receipt.exitCode -ne $statusCodes[$receipt.status] -or
        $receipt.ok -isnot [bool] -or $receipt.ok -ne ($receipt.status -ceq 'passed')) { Deny 'MAIN_STATUS' }

    $steps = @($receipt.steps)
    $expected = if ($receipt.command -ceq 'Public') {
        @('dotnet-build', 'public-tests', 'public-frontend-mapping', 'public-frontend-preview')
    } else { @('dotnet-build', 'focused-selected', 'focused-guards') }
    $allowed = @('dotnet-restore') + $expected
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $stepRows = [Collections.Generic.List[string]]::new()
    $testRows = [Collections.Generic.List[string]]::new()
    $hashRows = [Collections.Generic.List[string]]::new()
    $methodRows = [Collections.Generic.List[string]]::new()
    $anyNotPassed = $false
    foreach ($step in $steps) {
        if ($step.name -cnotin $allowed -or -not $seen.Add([string]$step.name)) { Deny 'STEP_NAME' }
        $label = StatusText $step.status
        if ($step.status -cne 'passed') { $anyNotPassed = $true }
        if ($step.status -ceq 'passed' -and
            ($step.process.hasExitCode -ne $true -or $step.process.exitCode -ne 0)) { Deny 'STEP_STATUS' }
        $kind = if ($step.name -clike 'public-frontend-*') { '前端' }
            elseif ($step.name -clike 'dotnet-*') { '建置準備' } else { '.NET 測試' }
        $stepRows.Add("<tr><td>$(HtmlText $kind)</td><td>$(HtmlText (StepLabel $step.name))</td><td>$(HtmlText $label)</td></tr>")
        if ($step.name -cin @('public-tests', 'focused-selected', 'focused-guards')) {
            if (-not $step.Contains('test') -or $null -eq $step.test) {
                if ($step.status -ceq 'passed') { Deny 'MISSING_EVIDENCE' }
                $testRows.Add("<tr><td>$(HtmlText (StepLabel $step.name))</td><td colspan='5'>未取得可核對的測試明細</td></tr>")
                continue
            }
            $summaryPath = RelativeFile $repo ([string]$step.test.summary) $run 'test-summary.json'
            $trxPath = RelativeFile $repo ([string]$step.test.trx) $run 'result.trx'
            $expectedDir = Full (Join-Path $run "test-results/$($step.name)")
            if ([IO.Path]::GetDirectoryName($summaryPath) -ine $expectedDir -or
                [IO.Path]::GetDirectoryName($trxPath) -ine $expectedDir) { Deny 'EVIDENCE_PATH' }
            $summary = JsonFile $summaryPath 67108864
            if ($summary.schemaVersion -ne 1) { Deny 'SUMMARY_SCHEMA' }
            PublicData $summary
            $hash = (Get-FileHash -LiteralPath $trxPath -Algorithm SHA256).Hash
            if ($step.test.trxSha256 -notmatch '^[0-9A-Fa-f]{64}$' -or
                $hash -ine $step.test.trxSha256 -or $hash -ine $summary.trxSha256) { Deny 'TRX_HASH' }
            $trxCounts = TrxCounters $trxPath
            $records = @($summary.records)
            $recordCounts = @{
                total = [long]$records.Count
                executed = [long]@($records | Where-Object outcome -cin @('Passed', 'Failed')).Count
                passed = [long]@($records | Where-Object outcome -ceq 'Passed').Count
                failed = [long]@($records | Where-Object outcome -ceq 'Failed').Count
                skipped = [long]@($records | Where-Object outcome -ceq 'Skipped').Count
            }
            if (@($records | Where-Object outcome -cnotin @('Passed', 'Failed', 'Skipped')).Count -gt 0) { Deny 'RECORD_STATUS' }
            foreach ($key in @('total', 'executed', 'passed', 'failed', 'skipped')) {
                $n = CountValue $summary.counters $key
                if ($n -ne (CountValue $step.test.counters $key) -or $n -ne $trxCounts[$key] -or $n -ne $recordCounts[$key]) { Deny 'COUNTERS' }
            }
            if ($step.status -ceq 'passed' -and ($recordCounts.failed -gt 0 -or $recordCounts.skipped -gt 0 -or $recordCounts.total -eq 0)) { Deny 'STEP_STATUS' }
            $c = $recordCounts
            $testRows.Add("<tr><td>$(HtmlText (StepLabel $step.name))</td><td class='num'>$($c.total)</td><td class='num'>$($c.executed)</td><td class='num'>$($c.passed)</td><td class='num'>$($c.failed)</td><td class='num'>$($c.skipped)</td></tr>")
            $hashRows.Add("<tr><td>$(HtmlText (StepLabel $step.name))</td><td class='hash'>$(HtmlText $hash)</td><td class='hash'>$(HtmlText ((Get-FileHash -LiteralPath $summaryPath -Algorithm SHA256).Hash))</td></tr>")
            # 方法層級彙總；不輸出 displayName、identity、案例參數或失敗訊息。
            $safeRecords = @($records | Where-Object { $_.fqn -cmatch '^JET\.Tests(?:\.[A-Za-z_][A-Za-z0-9_]*){2,}$' })
            foreach ($group in @($safeRecords | Group-Object fqn | Sort-Object Name)) {
                $p = @($group.Group | Where-Object outcome -ceq 'Passed').Count
                $f = @($group.Group | Where-Object outcome -ceq 'Failed').Count
                $s = @($group.Group | Where-Object outcome -ceq 'Skipped').Count
                $methodRows.Add("<tr><td>$(HtmlText (StepLabel $step.name))</td><td>$(HtmlText $group.Name)</td><td class='num'>$p</td><td class='num'>$f</td><td class='num'>$s</td></tr>")
            }
            if ($safeRecords.Count -lt $records.Count) {
                $methodRows.Add("<tr><td>$(HtmlText (StepLabel $step.name))</td><td colspan='4'>有 $($records.Count - $safeRecords.Count) 筆名稱不符合安全格式，已省略名稱；總數仍保留。</td></tr>")
            }
        }
    }
    foreach ($name in $expected) {
        if (-not $seen.Contains($name)) {
            $anyNotPassed = $true
            $stepRows.Add("<tr><td>必要步驟</td><td>$(HtmlText (StepLabel $name))</td><td>未執行或收據未記錄</td></tr>")
        }
    }
    if ($receipt.status -ceq 'passed' -and ($anyNotPassed -or $receipt.cleanup.status -cne 'passed' -or $receipt.lock.cleanup.status -cne 'passed')) { Deny 'MAIN_STATUS' }
    $filterText = if ($receipt.command -ceq 'Focused') { HtmlText $receipt.filter } else { '全部公開測試' }
    $receiptHash = (Get-FileHash -LiteralPath $receiptFull -Algorithm SHA256).Hash
    $localStart = $startInstant.ToLocalTime().ToString('yyyy-MM-dd HH:mm')
    $seconds = [int][Math]::Round([double]$receipt.durationSeconds)
    $duration = if ($seconds -ge 60) { "$([int][Math]::Floor($seconds / 60)) 分 $($seconds % 60) 秒" } else { "$seconds 秒" }
    $shortSha = $ExpectedSha.Substring(0, 7).ToLowerInvariant()
    $statusClass = if ($receipt.status -ceq 'passed') { 'pass' } else { 'fail' }
    $html = @"
<!doctype html>
<html lang="zh-Hant"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>JET 測試報告</title>
<style>
body{font-family:"Microsoft JhengHei",sans-serif;max-width:1100px;margin:32px auto;padding:0 24px;color:#1f2933;line-height:1.6}
h1{font-size:24px;margin:0 0 4px}h2{font-size:18px;margin:28px 0 10px;color:#1f3a5f}
.sub{color:#5b6773;margin:0 0 18px}
table{border-collapse:collapse;width:100%;font-size:13px;margin:6px 0}
th,td{border:1px solid #c5ced8;padding:6px 8px;text-align:left;vertical-align:top;overflow-wrap:anywhere}
th{background:#dce3ea}td.num{text-align:right}
.result{display:inline-block;padding:2px 10px;border-radius:4px;font-weight:bold}
.pass{background:#e3f1e6;color:#1d5b2c}.fail{background:#f8dcd7;color:#8d2718}
.mono{font-family:Consolas,monospace;font-size:12px}
.hash{font-family:Consolas,monospace;font-size:11px}
details{margin:14px 0}summary{cursor:pointer;font-weight:bold;color:#1f3a5f}
.sign td{height:28px}
@media print{details{display:block}details>summary{display:none}}
</style></head><body>
<h1>JET 測試報告</h1>
<p class="sub">由測試執行收據產生的摘要。原始結果與收據另存為附件。</p>
<h2>執行摘要</h2>
<table>
<tr><th style="width:22%">結果</th><td><span class="result $statusClass">$(HtmlText $mainStatus)</span></td></tr>
<tr><th>測試範圍</th><td>$filterText</td></tr>
<tr><th>程式版本</th><td>Git 提交 <span class="mono">$(HtmlText $shortSha)</span>（分支 $(HtmlText $receipt.repository.branch)）</td></tr>
<tr><th>建置組態</th><td>$(HtmlText $receipt.configuration)</td></tr>
<tr><th>執行時間</th><td>$(HtmlText $localStart)，共 $(HtmlText $duration)</td></tr>
</table>
<h2>各步驟結果</h2>
<table><thead><tr><th>類型</th><th>步驟</th><th>結果</th></tr></thead><tbody>$($stepRows -join '')</tbody></table>
<h2>測試數量</h2>
<table><thead><tr><th>測試組</th><th>總數</th><th>已執行</th><th>通過</th><th>失敗</th><th>略過</th></tr></thead><tbody>$($testRows -join '')</tbody></table>
<p>完整公開測試不含需要私人資料、SQL Server 或大量資料的測試。</p>
<details><summary>測試方法明細</summary>
<table><thead><tr><th>測試組</th><th>測試方法</th><th>通過</th><th>失敗</th><th>略過</th></tr></thead><tbody>$($methodRows -join '')</tbody></table></details>
<h2>執行與複核</h2>
<table class="sign"><tr><th style="width:22%">執行人員</th><td></td><th style="width:16%">日期</th><td style="width:22%"></td></tr>
<tr><th>複核人員</th><td></td><th>日期</th><td></td></tr>
<tr><th>執行環境</th><td colspan="3"></td></tr>
<tr><th>複核意見</th><td colspan="3"></td></tr></table>
<details><summary>追溯資料</summary>
<table><tr><th style="width:22%">完整提交編號</th><td class="mono">$(HtmlText $ExpectedSha.ToLowerInvariant())</td></tr>
<tr><th>執行編號</th><td class="mono">$(HtmlText $receipt.runId)</td></tr>
<tr><th>開始與結束時間（UTC）</th><td>$(HtmlText $start) 至 $(HtmlText $end)</td></tr>
<tr><th>執行收據 SHA-256</th><td class="mono">$(HtmlText $receiptHash)</td></tr></table>
<table><thead><tr><th>測試組</th><th>原始結果（TRX）SHA-256</th><th>測試摘要 SHA-256</th></tr></thead><tbody>$($hashRows -join '')</tbody></table>
<p>雜湊值用來核對附件中的原始結果與這份報告是否來自同一次執行。</p></details>
</body></html>
"@
    $outputFull = Full $OutputPath
    if ([IO.Path]::GetExtension($outputFull) -ine '.html' -or (IsInside $run $outputFull)) { Deny 'OUTPUT_PATH' }
    NoLinks $outputFull
    if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($outputFull))) { Deny 'OUTPUT_DIRECTORY' }
    $stream = [IO.File]::Open($outputFull, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($html)
        $stream.Write($bytes, 0, $bytes.Length)
    } finally { $stream.Dispose() }
    [ordered]@{ exported = $true; sourceStatus = $receipt.status; sha = $ExpectedSha.ToLowerInvariant(); output = $outputFull } | ConvertTo-Json -Compress
    exit 0
} catch {
    $code = if ($_.Exception.Message -cmatch '^SAT_[A-Z_]+$') { $_.Exception.Message } else { 'SAT_INVALID_INPUT_OR_OUTPUT' }
    [Console]::Error.WriteLine("無法產生報告：$code。請確認收據路徑、提交編號與輸出位置。")
    exit 2
}

