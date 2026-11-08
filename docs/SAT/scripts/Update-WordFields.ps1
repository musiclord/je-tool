#requires -Version 7.4
<#
.SYNOPSIS
    以 Microsoft Word 更新 SAT Word 文件的頁碼與功能變數。
.DESCRIPTION
    Build-Documents.py 產生的頁碼需由 Word 計算。
    本腳本在背景開啟 Word，更新每份文件的目錄與功能變數後存檔，不顯示 Word 視窗。
#>
[CmdletBinding()]
param(
    [string[]] $Path = @('Code Review Rules.docx', 'Design Principles.docx', 'Technical Document.docx', 'Framework.docx', '安全檢查摘要.docx')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$sat = Split-Path -Parent $PSScriptRoot
$word = New-Object -ComObject Word.Application
try {
    $word.Visible = $false
    $word.DisplayAlerts = 0
    foreach ($name in $Path) {
        $full = (Resolve-Path -LiteralPath (Join-Path $sat $name)).Path
        $doc = $word.Documents.Open($full, $false, $false)
        try {
            foreach ($toc in $doc.TablesOfContents) { $toc.Update() }
            [void]$doc.Fields.Update()
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
