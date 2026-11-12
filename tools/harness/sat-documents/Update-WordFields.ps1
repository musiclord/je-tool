#requires -Version 7.4
<#
.SYNOPSIS
    以 Microsoft Word 更新 SAT Word 文件的目錄與頁碼，再把 Word 存檔時寫入的個人資訊換掉。
.DESCRIPTION
    Build-Documents.py 產生的目錄與頁碼需由 Word 計算。
    本腳本在背景開啟 Word，更新每份文件的目錄與功能變數、重新分頁後存檔，不顯示 Word 視窗。
    每份都強制存檔，各份文件的樣式名稱與文件摘要資訊才都由 Word 寫出，不留 python-docx 範本的預設值。

    Word 存檔時會把這台電腦的 Office 使用者名稱寫進「上次儲存者」。Word 結束後，本腳本改寫每份文件的文件屬性：
    作者與上次儲存者固定為 JET，公司、管理者與超連結基底清空。最後檢查每份文件內部的 XML，
    找到 Windows 使用者名稱、Office 使用者名稱、電腦名稱或本機路徑時報錯；錯誤訊息只寫類別與所在的零件，不寫出名稱。
.PARAMETER Path
    要處理的文件，寫相對於 Root 的路徑。
.PARAMETER Root
    文件所在的資料夾，預設是 docs/SAT。Build-Documents.py 用 --output 輸出到別的資料夾時，這裡給同一個資料夾。
.EXAMPLE
    pwsh -NoProfile -File tools/harness/sat-documents/Update-WordFields.ps1
.EXAMPLE
    pwsh -NoProfile -File tools/harness/sat-documents/Update-WordFields.ps1 -Root D:\temp\sat-preview
#>
[CmdletBinding()]
param(
    [string[]] $Path = @('Technical Document.docx', 'Design Principle.docx', 'Code Review/Code review rule.docx'),
    [string] $Root = (Join-Path $PSScriptRoot '../../../docs/SAT')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$propertyOwner = 'JET'
$coreNamespace = 'http://schemas.openxmlformats.org/package/2006/metadata/core-properties'
$dublinCoreNamespace = 'http://purl.org/dc/elements/1.1/'
$extendedNamespace = 'http://schemas.openxmlformats.org/officeDocument/2006/extended-properties'

function Read-PackagePart {
    # 讀出 docx 裡的一個零件；沒有這個零件時回傳 $null。
    param([string] $File, [string] $Name)
    $archive = [IO.Compression.ZipFile]::OpenRead($File)
    try {
        $entry = $archive.GetEntry($Name)
        if ($null -eq $entry) { return $null }
        $buffer = [IO.MemoryStream]::new()
        $stream = $entry.Open()
        try { $stream.CopyTo($buffer) } finally { $stream.Dispose() }
        return , $buffer.ToArray()
    } finally {
        $archive.Dispose()
    }
}

function ConvertTo-XmlDocument {
    param([byte[]] $Bytes)
    $xml = [xml]::new()
    $xml.PreserveWhitespace = $true
    $xml.Load([IO.MemoryStream]::new($Bytes))
    return $xml
}

function ConvertFrom-XmlDocument {
    param([xml] $Xml)
    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Encoding = [Text.UTF8Encoding]::new($false)
    $buffer = [IO.MemoryStream]::new()
    $writer = [Xml.XmlWriter]::Create($buffer, $settings)
    try { $Xml.Save($writer) } finally { $writer.Dispose() }
    return , $buffer.ToArray()
}

function Get-CoreNamespaceManager {
    param([xml] $Xml)
    $manager = [Xml.XmlNamespaceManager]::new($Xml.NameTable)
    $manager.AddNamespace('cp', $coreNamespace)
    $manager.AddNamespace('dc', $dublinCoreNamespace)
    $manager.AddNamespace('ep', $extendedNamespace)
    return , $manager
}

function Set-PackagePart {
    # 重寫整個壓縮檔，零件的順序不變，只換掉 $Replacement 列出的零件。
    param([string] $File, [hashtable] $Replacement)
    $temporary = "$File.rewrite"
    $source = [IO.Compression.ZipFile]::OpenRead($File)
    try {
        $target = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($entry in $source.Entries) {
                $copy = $target.CreateEntry($entry.FullName, [IO.Compression.CompressionLevel]::Optimal)
                $copy.LastWriteTime = $entry.LastWriteTime
                $output = $copy.Open()
                try {
                    if ($Replacement.ContainsKey($entry.FullName)) {
                        [byte[]] $bytes = $Replacement[$entry.FullName]
                        $output.Write($bytes, 0, $bytes.Length)
                    } else {
                        $inputStream = $entry.Open()
                        try { $inputStream.CopyTo($output) } finally { $inputStream.Dispose() }
                    }
                } finally {
                    $output.Dispose()
                }
            }
        } finally {
            $target.Dispose()
        }
    } finally {
        $source.Dispose()
    }
    Move-Item -LiteralPath $temporary -Destination $File -Force
}

function Set-DocumentProperty {
    # 把作者與上次儲存者固定為 JET，清空公司、管理者與超連結基底。回傳 Word 原本寫入的上次儲存者。
    param([string] $File)
    $replacement = @{}

    $core = ConvertTo-XmlDocument (Read-PackagePart $File 'docProps/core.xml')
    $names = Get-CoreNamespaceManager $core
    $savedBy = ''
    foreach ($property in @(
            @{ Query = 'dc:creator'; Name = 'dc:creator'; Namespace = $dublinCoreNamespace },
            @{ Query = 'cp:lastModifiedBy'; Name = 'cp:lastModifiedBy'; Namespace = $coreNamespace })) {
        $node = $core.DocumentElement.SelectSingleNode($property.Query, $names)
        if ($null -eq $node) {
            $node = $core.CreateElement($property.Name, $property.Namespace)
            [void]$core.DocumentElement.AppendChild($node)
        } elseif ($property.Query -eq 'cp:lastModifiedBy') {
            $savedBy = $node.InnerText
        }
        $node.InnerText = $propertyOwner
    }
    $replacement['docProps/core.xml'] = ConvertFrom-XmlDocument $core

    $appBytes = Read-PackagePart $File 'docProps/app.xml'
    if ($null -ne $appBytes) {
        $app = ConvertTo-XmlDocument $appBytes
        $names = Get-CoreNamespaceManager $app
        foreach ($query in 'ep:Company', 'ep:Manager', 'ep:HyperlinkBase') {
            $node = $app.DocumentElement.SelectSingleNode($query, $names)
            if ($null -ne $node) { $node.InnerText = '' }
        }
        $replacement['docProps/app.xml'] = ConvertFrom-XmlDocument $app
    }

    Set-PackagePart $File $replacement
    return $savedBy
}

function Assert-NoPersonalInformation {
    # 檢查文件屬性與每個 XML 零件。$Terms 的每一項是 @{ Label = 類別; Value = 要找的字 }。
    param([string] $File, [string] $Name, [object[]] $Terms)
    $problems = [Collections.Generic.List[string]]::new()

    $core = ConvertTo-XmlDocument (Read-PackagePart $File 'docProps/core.xml')
    $names = Get-CoreNamespaceManager $core
    foreach ($query in 'dc:creator', 'cp:lastModifiedBy') {
        $node = $core.DocumentElement.SelectSingleNode($query, $names)
        if ($null -eq $node -or $node.InnerText -cne $propertyOwner) {
            $problems.Add("docProps/core.xml 的 $query 不是 $propertyOwner")
        }
    }
    $appBytes = Read-PackagePart $File 'docProps/app.xml'
    if ($null -ne $appBytes) {
        $app = ConvertTo-XmlDocument $appBytes
        $names = Get-CoreNamespaceManager $app
        foreach ($query in 'ep:Company', 'ep:Manager', 'ep:HyperlinkBase') {
            $node = $app.DocumentElement.SelectSingleNode($query, $names)
            if ($null -ne $node -and $node.InnerText -ne '') {
                $problems.Add("docProps/app.xml 的 $($query.Substring(3)) 不是空的")
            }
        }
    }
    $customBytes = Read-PackagePart $File 'docProps/custom.xml'
    if ($null -ne $customBytes) {
        $custom = ConvertTo-XmlDocument $customBytes
        $propertyNames = @($custom.DocumentElement.ChildNodes |
                Where-Object { $_.NodeType -eq [Xml.XmlNodeType]::Element } |
                ForEach-Object { $_.GetAttribute('name') })
        if ($propertyNames.Count -gt 0) {
            $problems.Add("docProps/custom.xml 有自訂屬性：$($propertyNames -join '、')")
        }
    }

    $archive = [IO.Compression.ZipFile]::OpenRead($File)
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -notmatch '\.(xml|rels)$') { continue }
            $reader = [IO.StreamReader]::new($entry.Open(), [Text.Encoding]::UTF8)
            try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
            foreach ($term in $Terms) {
                if ($text.IndexOf($term.Value, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    $problems.Add("$($entry.FullName) 裡有$($term.Label)")
                }
            }
            if ($text -match '[A-Za-z]:\\Users\\' -or $text -match '(?i)file://') {
                $problems.Add("$($entry.FullName) 裡有本機路徑")
            }
        }
    } finally {
        $archive.Dispose()
    }

    if ($problems.Count -gt 0) {
        throw "$Name 的文件屬性或內部 XML 仍有個人資訊：$(($problems | Select-Object -Unique) -join '；')"
    }
}

function Add-SearchTerm {
    # 太短的名稱與 JET 本身不列入，避免比對到文件的正常內容。
    param([Collections.Generic.List[object]] $Terms, [string] $Label, [string] $Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return }
    $Value = $Value.Trim()
    if ($Value.Length -lt 2 -or $Value -eq $propertyOwner) { return }
    foreach ($form in @($Value, [Security.SecurityElement]::Escape($Value)) | Select-Object -Unique) {
        if (-not ($Terms | Where-Object { $_.Value -eq $form })) {
            $Terms.Add(@{ Label = $Label; Value = $form })
        }
    }
}

$files = @(foreach ($name in $Path) {
        @{ Name = $name; Full = (Resolve-Path -LiteralPath (Join-Path $Root $name)).Path }
    })
$terms = [Collections.Generic.List[object]]::new()
Add-SearchTerm $terms 'Windows 使用者名稱' $env:USERNAME
Add-SearchTerm $terms '電腦名稱' $env:COMPUTERNAME
Add-SearchTerm $terms '本機路徑' $env:USERPROFILE

$word = New-Object -ComObject Word.Application
try {
    $word.Visible = $false
    $word.DisplayAlerts = 0
    Add-SearchTerm $terms 'Office 使用者名稱' $word.UserName
    foreach ($file in $files) {
        $doc = $word.Documents.Open($file.Full, $false, $false)
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
            Write-Output "updated $($file.Name)"
        } finally {
            $doc.Close($false)
            [void][Runtime.InteropServices.Marshal]::ReleaseComObject($doc)
        }
    }
} finally {
    $word.Quit()
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($word)
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}

# Word 已經關閉檔案。先全部改寫，再用改寫前讀到的名稱檢查每一份。
foreach ($file in $files) {
    Add-SearchTerm $terms 'Office 使用者名稱' (Set-DocumentProperty $file.Full)
}
foreach ($file in $files) {
    Assert-NoPersonalInformation $file.Full $file.Name $terms.ToArray()
    Write-Output "checked $($file.Name)"
}
