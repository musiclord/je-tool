#requires -Version 7.4
<#
.SYNOPSIS
    以 Microsoft Word 更新 SAT Word 文件的頁碼與功能變數。
.DESCRIPTION
    Build-Documents.py 產生的頁碼需由 Word 計算。
    本腳本在背景開啟 Word，更新每份文件的目錄與功能變數、重新分頁後存檔，不顯示 Word 視窗。
    每份都強制存檔，各份文件的樣式名稱與文件摘要資訊才都由 Word 寫出，不留 python-docx 範本的預設值。
#>
[CmdletBinding()]
param(
    [string[]] $Path = @('Technical Document.docx', 'Design Principle.docx', 'Code Review/Code review rule.docx')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$sat = Join-Path $PSScriptRoot '../../../docs/SAT'
$word = New-Object -ComObject Word.Application
try {
    $word.Visible = $false
    $word.DisplayAlerts = 0
    foreach ($name in $Path) {
        $full = (Resolve-Path -LiteralPath (Join-Path $sat $name)).Path
        $doc = $word.Documents.Open($full, $false, $false)
        try {
            foreach ($toc in $doc.TablesOfContents) { [void]$toc.Update() }
            # Document.Fields 只含內文，所以逐一走訪每個文字區塊更新功能變數。
            # 頁首頁尾的頁碼由 Word 顯示或列印時依版面重算，檔案裡存的值不影響頁面上的頁碼。
            foreach ($story in $doc.StoryRanges) {
                $range = $story
                while ($null -ne $range) {
                    [void]$range.Fields.Update()
                    $range = $range.NextStoryRange
                }
            }
            $doc.Repaginate()
            # Word 認為文件沒有變動時，Save() 不會寫檔；強制存檔，各份文件才都由 Word 寫出。
            $doc.Saved = $false
            $doc.Save()
            Write-Output "updated $name"
        } finally {
            $doc.Close($false)
        }
    }
} finally {
    $word.Quit()
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($word)
}
