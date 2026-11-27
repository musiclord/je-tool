#requires -Version 7.4
<#
.SYNOPSIS
    發布副本本機的提交與推送前檢查。
.DESCRIPTION
    同步工具把這個檔、release-copy-patterns.ps1 與三個呼叫它的檢查裝進發布副本的 .git/hooks。
    commit-msg：提交訊息不能有 AI 工具的署名、AI 工具與驗證框架的名稱、開發紀錄的寫法與看不見的字元。
    pre-commit：作者與提交者要是發布副本本機設定的姓名與信箱，暫存的檔案不能有發布副本不帶的路徑。
    pre-push：要推送的每個提交照上面兩項再檢查一次；附註標籤的標記者也要是本機設定的身分。
    沒通過時列出原因並以結束碼 1 結束。訊息不寫出姓名與信箱。
#>
param(
    [Parameter(Mandatory)] [ValidateSet('commit-msg', 'pre-commit', 'pre-push')] [string] $Mode,
    [string] $MessageFile,
    [string] $Remote = 'origin'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
. (Join-Path $PSScriptRoot 'release-copy-patterns.ps1')

$problems = [Collections.Generic.List[string]]::new()

function Invoke-GitText {
    # 執行 git 並回傳標準輸出；路徑與訊息一律用 UTF-8 讀。
    param([string[]] $Arguments, [switch] $AllowFailure)
    $info = [Diagnostics.ProcessStartInfo]::new('git')
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $info.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    $info.UseShellExecute = $false
    $process = [Diagnostics.Process]::Start($info)
    $errorTask = $process.StandardError.ReadToEndAsync()
    $output = $process.StandardOutput.ReadToEnd()
    $process.WaitForExit()
    $errorText = $errorTask.Result
    if ($process.ExitCode -ne 0 -and -not $AllowFailure) {
        throw "git $($Arguments -join ' ') 失敗（結束碼 $($process.ExitCode)）：$errorText"
    }
    return $output
}

function Get-LocalIdentity {
    $name = (Invoke-GitText @('config', '--local', '--get', 'user.name') -AllowFailure).Trim()
    $email = (Invoke-GitText @('config', '--local', '--get', 'user.email') -AllowFailure).Trim()
    if (-not $name -or -not $email) {
        $problems.Add('發布副本本機沒有設定提交身分。先用同步工具鏡像一次，工具會設定成和開發儲存庫相同的姓名與信箱。')
        return $null
    }
    [pscustomobject]@{ Name = $name; Email = $email }
}

function Test-Identity {
    param([string] $Label, [string] $Name, [string] $Email, [object] $Expected)
    if ($Name -cne $Expected.Name -or $Email -ine $Expected.Email) {
        $problems.Add("$Label 和發布副本本機設定的提交身分不同。")
    }
}

function Test-IdentLine {
    # git var 與標籤內容的身分寫成「姓名 <信箱> 時間 時區」。
    param([string] $Label, [string] $Ident, [object] $Expected)
    $match = [regex]::Match($Ident, '^(?<name>.*?) <(?<email>[^>]*)>')
    if (-not $match.Success) {
        $problems.Add("$Label 讀不出姓名與信箱。")
        return
    }
    Test-Identity $Label $match.Groups['name'].Value $match.Groups['email'].Value $Expected
}

function Test-MessageText {
    param([string] $Label, [string] $Text)
    if ($script:ReleaseCopyAttribution.IsMatch($Text)) {
        $problems.Add("$Label 有 AI 工具的署名。")
    }
    foreach ($finding in Get-ReleaseCopyTextFinding -Text $Text -IncludeInvisible) {
        $problems.Add("$Label 第 $($finding.Line) 行有$($finding.Name)。")
    }
}

function Test-PathList {
    param([string] $Label, [string[]] $Paths)
    foreach ($path in $Paths) {
        if ($script:ReleaseCopyForbiddenPath.IsMatch($path)) {
            $problems.Add("$Label 有發布副本不帶的路徑：$path")
        }
        foreach ($finding in Get-ReleaseCopyTextFinding -Text $path) {
            $problems.Add("$Label 的路徑有$($finding.Name)：$path")
        }
    }
}

function Split-NulList {
    param([string] $Text)
    @($Text -split "`0" | Where-Object { $_ -ne '' })
}

switch ($Mode) {
    'commit-msg' {
        $text = [IO.File]::ReadAllText($MessageFile, [Text.UTF8Encoding]::new($false))
        Test-MessageText '提交訊息' $text
    }
    'pre-commit' {
        $expected = Get-LocalIdentity
        if ($null -ne $expected) {
            Test-IdentLine '提交作者' (Invoke-GitText @('var', 'GIT_AUTHOR_IDENT')).Trim() $expected
            Test-IdentLine '提交者' (Invoke-GitText @('var', 'GIT_COMMITTER_IDENT')).Trim() $expected
        }
        $staged = Split-NulList (Invoke-GitText @('diff', '--cached', '--name-only', '-z', '--diff-filter=ACMR'))
        Test-PathList '暫存的檔案' $staged
    }
    'pre-push' {
        $expected = Get-LocalIdentity
        $zero = '0' * 40
        $checked = [Collections.Generic.HashSet[string]]::new()
        foreach ($line in ([Console]::In.ReadToEnd() -split "`n")) {
            $parts = $line.Trim() -split ' '
            if ($parts.Count -lt 4 -or $parts[1] -eq $zero) { continue }
            $object = $parts[1]
            if ((Invoke-GitText @('cat-file', '-t', $object)).Trim() -eq 'tag' -and $null -ne $expected) {
                $tagger = ((Invoke-GitText @('cat-file', '-p', $object)) -split "`n" |
                        Where-Object { $_ -like 'tagger *' } | Select-Object -First 1)
                if ($tagger) { Test-IdentLine "標籤 $($parts[0]) 的標記者" $tagger.Substring(7) $expected }
            }
            $tip = (Invoke-GitText @('rev-parse', "$object^{commit}")).Trim()
            $commits = @((Invoke-GitText @('rev-list', $tip, '--not', "--remotes=$Remote")) -split "`n" |
                    ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
            foreach ($commit in $commits) {
                if (-not $checked.Add($commit)) { continue }
                $short = $commit.Substring(0, 12)
                $fields = (Invoke-GitText @('log', '-1', '--format=%an%x00%ae%x00%cn%x00%ce', $commit)).TrimEnd("`r", "`n") -split "`0"
                if ($null -ne $expected) {
                    Test-Identity "提交 $short 的作者" $fields[0] $fields[1] $expected
                    Test-Identity "提交 $short 的提交者" $fields[2] $fields[3] $expected
                }
                Test-MessageText "提交 $short 的訊息" (Invoke-GitText @('log', '-1', '--format=%B', $commit))
                Test-PathList "提交 $short" (Split-NulList (Invoke-GitText @('ls-tree', '-r', '-z', '--name-only', $commit)))
            }
        }
    }
}

if ($problems.Count -gt 0) {
    [Console]::Error.WriteLine("發布副本的檢查沒有通過（$Mode）：")
    foreach ($problem in ($problems | Select-Object -Unique)) { [Console]::Error.WriteLine("- $problem") }
    exit 1
}
exit 0
