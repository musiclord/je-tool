#requires -Version 7.4
<#
.SYNOPSIS
    從這個儲存庫產生發布副本，掃描、建置與測試都通過後，鏡像到發布副本的資料夾。
.DESCRIPTION
    發布副本是交給 GD 測試環境與公司 GitHub 的程式庫，資料夾預設是和這個儲存庫並排的 je-tool-clean。
    工具照 manifest.json 在 artifacts/release-copy/copy 組成發布副本，依序做下列事情；任何一步沒通過就停下，
    不動發布副本的資料夾。

    1. 組成：Source 是 Head 時取這個儲存庫目前提交的版本；是 WorkingTree 時取工作樹，包括還沒提交的修改與還沒加入 Git 的新檔。
    2. 拿掉送審活頁簿記下的存檔資料夾路徑。Excel 每次存檔都會寫回這一段，這是唯一改動檔案內容的地方。
    3. 掃描：發布副本不帶的路徑；AI 工具與驗證框架的名稱、開發紀錄的寫法與看不見的字元；這台電腦的使用者名稱、
       電腦名稱、使用者資料夾、Git 姓名與信箱、GitHub 帳號與 Office 使用者名稱；Office、PNG 與 PDF 的檔案屬性；
       說明檔的相對連結。舊版範本照 manifest.json 的檔案雜湊列為例外。結果只寫類別與位置，不寫出找到的名稱。
    4. 建置方案，執行 C# 測試（排除 Scale）與前端測試。
    5. 加上 Mirror 時鏡像到發布副本：只動 Git 看得到的檔案，不刪 Git 忽略的檔案，也不碰 .git 的內容；
       同時把發布副本本機的提交身分設成和這個儲存庫相同、補上本機的忽略清單，並裝上提交與推送前的檢查。
    6. 加上 CommitMessageFile 時在發布副本提交，加上 Tag 時加附註標籤，加上 Push 時推送到 origin。
       提交只在 Source 是 Head 時允許，提交後在 local/release-copy/commits.md 記下兩邊提交的對應。
       內容和上次提交相同時不提交，照樣加標籤與推送，方便推送失敗後重跑。

    整段執行期間拿 tools/verify.ps1 的共享鎖。鎖正被別的驗證使用時立刻停下，加上 WaitSeconds 可以等它結束。
.PARAMETER Source
    Head（預設）或 WorkingTree。
.PARAMETER Destination
    發布副本的資料夾。
.PARAMETER Mirror
    建置與測試通過後鏡像到發布副本。
.PARAMETER SkipTests
    只組成與掃描，不建置也不測試；不能和 Mirror 一起用。
.PARAMETER CommitMessageFile
    鏡像後在發布副本提交，提交訊息讀這個 UTF-8 檔。要搭配 Mirror 與 Source Head。
.PARAMETER Tag
    提交後加上的版本標籤，例如 v1.0.0.0。要搭配 CommitMessageFile。
.PARAMETER Push
    提交後把 main 推送到 origin，有 Tag 時一起推送標籤。要搭配 CommitMessageFile。
.PARAMETER WaitSeconds
    共享鎖正被別的驗證使用時，最多等幾秒。預設 0 是不等；上限和 tools/verify.ps1 相同。
.EXAMPLE
    pwsh -NoProfile -File tools/harness/release-copy/JetReleaseCopy.ps1 -Source WorkingTree -SkipTests
.EXAMPLE
    pwsh -NoProfile -File tools/harness/release-copy/JetReleaseCopy.ps1 -Mirror -CommitMessageFile local/release-message.txt -Push
#>
[CmdletBinding()]
param(
    [ValidateSet('Head', 'WorkingTree')] [string] $Source = 'Head',
    [string] $Destination,
    [switch] $Mirror,
    [switch] $SkipTests,
    [string] $CommitMessageFile,
    [string] $Tag,
    [switch] $Push,
    [int] $WaitSeconds = 0
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Text.UTF8Encoding]::new($false)
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
. (Join-Path $PSScriptRoot 'hooks/release-copy-patterns.ps1')

$utf8 = [Text.UTF8Encoding]::new($false)

function Invoke-Native {
    # 執行外部程式並回傳標準輸出（UTF-8）。結束碼不是 0 時丟出錯誤，訊息附上標準錯誤。
    # RemoveEnvironment 列的環境變數不傳給這個程式。
    param(
        [string] $FilePath,
        [string[]] $ArgumentList,
        [string] $WorkingDirectory = (Get-Location).Path,
        [switch] $AllowFailure,
        [string[]] $RemoveEnvironment = @()
    )
    $info = [Diagnostics.ProcessStartInfo]::new($FilePath)
    foreach ($argument in $ArgumentList) { $info.ArgumentList.Add($argument) }
    foreach ($name in $RemoveEnvironment) { [void]$info.Environment.Remove($name) }
    $info.WorkingDirectory = $WorkingDirectory
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = $utf8
    $info.StandardErrorEncoding = $utf8
    $info.UseShellExecute = $false
    $process = [Diagnostics.Process]::Start($info)
    $errorTask = $process.StandardError.ReadToEndAsync()
    $output = $process.StandardOutput.ReadToEnd()
    $process.WaitForExit()
    $errorText = $errorTask.Result
    if ($process.ExitCode -ne 0 -and -not $AllowFailure) {
        throw "$FilePath $($ArgumentList -join ' ') 失敗（結束碼 $($process.ExitCode)）：$errorText"
    }
    [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output; Error = $errorText }
}

function Invoke-Git {
    param([string] $Repository, [string[]] $Arguments, [switch] $AllowFailure)
    Invoke-Native 'git' (@('-C', $Repository, '-c', 'core.quotepath=off') + $Arguments) -AllowFailure:$AllowFailure
}

function Split-NulList {
    param([string] $Text)
    @($Text -split "`0" | Where-Object { $_ -ne '' })
}

function Invoke-Logged {
    # 執行建置或測試，完整輸出寫進記錄檔；失敗時印出記錄檔最後 40 行。
    param([string] $Label, [string] $FilePath, [string[]] $ArgumentList, [string] $WorkingDirectory, [string] $LogFile,
        [string[]] $RemoveEnvironment = @())
    Write-Host "$Label…"
    $result = Invoke-Native $FilePath $ArgumentList -WorkingDirectory $WorkingDirectory -AllowFailure -RemoveEnvironment $RemoveEnvironment
    [IO.File]::WriteAllText($LogFile, $result.Output + [Environment]::NewLine + $result.Error, $utf8)
    if ($result.ExitCode -ne 0) {
        Get-Content -LiteralPath $LogFile -Tail 40 | ForEach-Object { Write-Host "  $_" }
        throw "$Label 沒有通過（結束碼 $($result.ExitCode)），完整輸出在 $LogFile。"
    }
    Write-Host "$Label：通過"
}

function Get-Sha256 {
    param([string] $Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function ConvertTo-ReleasePath {
    param([string] $Path)
    $Path.Replace('\', '/')
}

# ---- 參數與路徑 ----

if ($SkipTests -and $Mirror) { throw '鏡像前一定要建置與測試，SkipTests 不能和 Mirror 一起用。' }
if ($CommitMessageFile -and (-not $Mirror -or $Source -ne 'Head')) {
    throw '提交要搭配 Mirror，而且 Source 要是 Head：發布副本的提交只從這個儲存庫已提交的版本產生。'
}
if (($Tag -or $Push) -and -not $CommitMessageFile) { throw 'Tag 與 Push 要搭配 CommitMessageFile。' }

$toolRoot = $PSScriptRoot
$repoRoot = [IO.Path]::GetFullPath((Invoke-Git $toolRoot @('rev-parse', '--show-toplevel')).Output.Trim())
if (-not $Destination) { $Destination = Join-Path (Split-Path $repoRoot -Parent) 'je-tool-clean' }
$Destination = [IO.Path]::GetFullPath($Destination)
$manifest = Get-Content -LiteralPath (Join-Path $toolRoot 'manifest.json') -Raw -Encoding utf8 | ConvertFrom-Json
$lanes = Get-Content -LiteralPath (Join-Path $toolRoot '../lanes.json') -Raw -Encoding utf8 | ConvertFrom-Json
$maximumWait = [int]$lanes.limits.maximumWaitSeconds
if ($WaitSeconds -lt 0 -or $WaitSeconds -gt $maximumWait) { throw "WaitSeconds 要在 0 到 $maximumWait 秒之間。" }

# 建置與測試要跑幾分鐘，發布副本的資料夾與提交訊息先檢查，有問題就不必等。
if ($Mirror) {
    $destinationTop = (Invoke-Git $Destination @('rev-parse', '--show-toplevel') -AllowFailure).Output.Trim()
    if (-not $destinationTop -or [IO.Path]::GetFullPath($destinationTop) -ne $Destination) {
        throw "$Destination 不是 Git 儲存庫的根目錄。"
    }
    $destinationPrefix = $Destination.TrimEnd('\') + '\'
    $repoPrefix = $repoRoot.TrimEnd('\') + '\'
    if ($destinationPrefix.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        $repoPrefix.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw '發布副本的資料夾不能和這個儲存庫重疊。'
    }
    $branch = (Invoke-Git $Destination @('symbolic-ref', '--short', 'HEAD') -AllowFailure).Output.Trim()
    if ($branch -ne 'main') { throw "發布副本目前不在 main 分支（現在是「$branch」），切回 main 再同步。" }
}
if ($CommitMessageFile) {
    $messagePath = [IO.Path]::GetFullPath($CommitMessageFile)
    if (-not (Test-Path -LiteralPath $messagePath -PathType Leaf)) { throw "找不到提交訊息檔：$messagePath" }
    $check = Invoke-Native 'pwsh' @('-NoProfile', '-File', (Join-Path $toolRoot 'hooks/release-copy-check.ps1'), '-Mode', 'commit-msg', '-MessageFile', $messagePath) -AllowFailure
    if ($check.ExitCode -ne 0) { throw "提交訊息沒有通過檢查：$($check.Error.Trim())" }
}
if ($Push -and (Invoke-Git $Destination @('remote', 'get-url', 'origin') -AllowFailure).ExitCode -ne 0) {
    throw '發布副本沒有設定 origin，推送不了。先在發布副本加上個人 GitHub 的 repo 作為 origin。'
}

# ---- 共享鎖 ----
# 整段執行期間拿 tools/verify.ps1 的共享鎖：建置與測試不會和別的驗證同時跑而互搶 CPU 與記憶體，
# 兩次同步也不會同時改寫 artifacts/release-copy，把只組成一半的副本鏡像出去。
# 鎖的做法留在驗證框架的模組裡，這裡在模組裡面呼叫同一組函式，不另寫一份。
$harness = Import-Module (Join-Path $toolRoot '../JetHarness.psm1') -PassThru -Force
$runId = "release-copy-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$lock = & $harness {
    param($RepositoryRoot, $RunId, $WaitSeconds)
    Enter-JetExclusiveLock -RepositoryRoot $RepositoryRoot -RunId $RunId -Command 'JetReleaseCopy' -WaitSeconds $WaitSeconds
} $repoRoot $runId $WaitSeconds
if (-not $lock.Acquired) {
    $holder = if ($lock.Holder -and $lock.Holder['command']) { "（$($lock.Holder['command'])）" } else { '' }
    throw "共享鎖正由另一個驗證使用$holder。等它結束再重跑，或加上 -WaitSeconds 指定最多等幾秒。"
}

try {
    $workRoot = Join-Path $repoRoot 'artifacts/release-copy'
    $sourceRoot = Join-Path $workRoot 'source'
    $copyRoot = Join-Path $workRoot 'copy'
    $logRoot = Join-Path $workRoot 'logs'
    foreach ($folder in @($sourceRoot, $copyRoot, $logRoot, (Join-Path $workRoot 'test-results'))) {
        if (-not ([IO.Path]::GetFullPath($folder)).StartsWith([IO.Path]::GetFullPath($workRoot), [StringComparison]::OrdinalIgnoreCase)) {
            throw "工作資料夾不在 artifacts/release-copy 底下：$folder"
        }
        if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
        [void][IO.Directory]::CreateDirectory($folder)
    }

    $sourceCommit = (Invoke-Git $repoRoot @('rev-parse', 'HEAD')).Output.Trim()
    $dirtyCount = @((Invoke-Git $repoRoot @('status', '--porcelain')).Output -split "`n" | Where-Object { $_.Trim() }).Count
    if ($Source -eq 'Head') {
        Write-Host "來源：這個儲存庫的提交 $($sourceCommit.Substring(0, 12))$(if ($dirtyCount) { "（工作樹另有 $dirtyCount 項未提交的修改，這次不帶）" })"
    } else {
        Write-Host "來源：這個儲存庫的工作樹（以提交 $($sourceCommit.Substring(0, 12)) 為底，另有 $dirtyCount 項未提交的修改）"
    }

    # ---- 1. 組成 ----

    $fromPaths = @($manifest.copies | ForEach-Object { $_.from }) + @($manifest.documentProperties)
    if ($Source -eq 'Head') {
        $archive = Join-Path $workRoot 'source.zip'
        [void](Invoke-Git $repoRoot (@('archive', '--format=zip', '-0', '-o', $archive, 'HEAD', '--') + $fromPaths))
        [IO.Compression.ZipFile]::ExtractToDirectory($archive, $sourceRoot)
        Remove-Item -LiteralPath $archive -Force
    } else {
        $listed = Split-NulList (Invoke-Git $repoRoot (@('ls-files', '-z', '--cached', '--others', '--exclude-standard', '--') + $fromPaths)).Output
        foreach ($relative in ($listed | Select-Object -Unique)) {
            $from = Join-Path $repoRoot $relative
            if (-not (Test-Path -LiteralPath $from -PathType Leaf)) { continue }
            $to = Join-Path $sourceRoot $relative
            [void][IO.Directory]::CreateDirectory((Split-Path $to -Parent))
            Copy-Item -LiteralPath $from -Destination $to
        }
    }

    $release = [ordered]@{}
    foreach ($entry in $manifest.copies) {
        $from = Join-Path $sourceRoot $entry.from
        if (Test-Path -LiteralPath $from -PathType Leaf) {
            $pairs = @(, @($from, $entry.to))
        } elseif (Test-Path -LiteralPath $from -PathType Container) {
            $pairs = @(Get-ChildItem -LiteralPath $from -Recurse -File -Force | ForEach-Object {
                    $inner = ConvertTo-ReleasePath ([IO.Path]::GetRelativePath($from, $_.FullName))
                    , @($_.FullName, $(if ($entry.to) { "$($entry.to)/$inner" } else { $inner }))
                })
        } else {
            throw "manifest.json 的 $($entry.from) 不在來源裡。"
        }
        foreach ($pair in $pairs) {
            $target = ConvertTo-ReleasePath $pair[1]
            if ($release.Contains($target)) { throw "兩個來源都要放到發布副本的 $target。" }
            $release[$target] = $pair[0]
        }
    }
    foreach ($target in $release.Keys) {
        $to = Join-Path $copyRoot $target
        [void][IO.Directory]::CreateDirectory((Split-Path $to -Parent))
        Copy-Item -LiteralPath $release[$target] -Destination $to
    }
    Write-Host "組成發布副本：$($release.Count) 個檔"

    # ---- 2. 拿掉送審活頁簿記下的存檔資料夾路徑 ----

    function Remove-SavedFolderPath {
        # 拿掉 xl/workbook.xml 裡記下存檔資料夾的那一段，其他零件照原樣與原順序寫回。回傳有沒有改動。
        param([string] $Path)
        $zip = [IO.Compression.ZipFile]::OpenRead($Path)
        try {
            $entry = $zip.GetEntry('xl/workbook.xml')
            if ($null -eq $entry) { return $false }
            $reader = [IO.StreamReader]::new($entry.Open(), $utf8)
            try { $xml = $reader.ReadToEnd() } finally { $reader.Dispose() }
        } finally { $zip.Dispose() }
        if ($xml -notmatch 'absPath') { return $false }
        $cleaned = [regex]::Replace($xml,
            '<mc:AlternateContent\b(?:(?!</mc:AlternateContent>).)*?x15ac:absPath\b(?:(?!</mc:AlternateContent>).)*?</mc:AlternateContent>',
            '', 'Singleline')
        if ($cleaned -match 'absPath') { throw "$Path 記下存檔資料夾的那一段不在預期的位置，沒有拿掉。" }

        $temporary = "$Path.tmp"
        $original = [IO.Compression.ZipFile]::OpenRead($Path)
        try {
            $stream = [IO.File]::Create($temporary)
            try {
                $rebuilt = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
                try {
                    foreach ($item in $original.Entries) {
                        $copy = $rebuilt.CreateEntry($item.FullName, [IO.Compression.CompressionLevel]::Optimal)
                        $copy.LastWriteTime = $item.LastWriteTime
                        $output = $copy.Open()
                        try {
                            if ($item.FullName -eq 'xl/workbook.xml') {
                                $bytes = $utf8.GetBytes($cleaned)
                                $output.Write($bytes, 0, $bytes.Length)
                            } else {
                                $inputStream = $item.Open()
                                try { $inputStream.CopyTo($output) } finally { $inputStream.Dispose() }
                            }
                        } finally { $output.Dispose() }
                    }
                } finally { $rebuilt.Dispose() }
            } finally { $stream.Dispose() }
        } finally { $original.Dispose() }
        Move-Item -LiteralPath $temporary -Destination $Path -Force
        return $true
    }

    $cleanedWorkbooks = 0
    foreach ($target in @($release.Keys)) {
        if ($target -notmatch '\.xlsx$') { continue }
        if (-not ($manifest.removeSavedFolderPath | Where-Object { $target.StartsWith($_, [StringComparison]::Ordinal) })) { continue }
        if (Remove-SavedFolderPath (Join-Path $copyRoot $target)) { $cleanedWorkbooks++ }
    }
    Write-Host "拿掉存檔資料夾路徑：$cleanedWorkbooks 份活頁簿"

    # ---- 3. 掃描 ----

    $findings = [Collections.Generic.List[string]]::new()
    function Add-Finding {
        param([string] $Category, [string] $Where)
        $findings.Add("$Category：$Where")
    }

    # 這台電腦與使用者的識別資訊。太短的值與 JET 本身不列入，避免比對到一般內容。
    $identityTerms = [Collections.Generic.List[object]]::new()
    function Add-IdentityTerm {
        param([string] $Label, [string] $Value)
        if ([string]::IsNullOrWhiteSpace($Value)) { return }
        $Value = $Value.Trim()
        if ($Value.Length -lt 3 -or $Value -eq 'JET') { return }
        foreach ($form in @($Value, [Security.SecurityElement]::Escape($Value)) | Select-Object -Unique) {
            $pattern = [regex]::new('(?<![A-Za-z0-9])' + [regex]::Escape($form) + '(?![A-Za-z0-9])', 'IgnoreCase, CultureInvariant')
            $identityTerms.Add([pscustomobject]@{ Label = $Label; Pattern = $pattern })
        }
    }
    Add-IdentityTerm 'Windows 使用者名稱' $env:USERNAME
    Add-IdentityTerm '電腦名稱' $env:COMPUTERNAME
    Add-IdentityTerm '使用者資料夾' $env:USERPROFILE
    Add-IdentityTerm '使用者資料夾' ($env:USERPROFILE -replace '\\', '/')
    if ($env:USERPROFILE -match '^([A-Za-z]):\\(.*)$') {
        Add-IdentityTerm '使用者資料夾' ('/' + $Matches[1].ToLowerInvariant() + '/' + ($Matches[2] -replace '\\', '/'))
    }
    Add-IdentityTerm 'Git 姓名' (Invoke-Git $repoRoot @('config', 'user.name') -AllowFailure).Output
    Add-IdentityTerm 'Git 信箱' (Invoke-Git $repoRoot @('config', 'user.email') -AllowFailure).Output
    foreach ($repository in @($repoRoot, $Destination)) {
        if (-not (Test-Path -LiteralPath (Join-Path $repository '.git'))) { continue }
        foreach ($url in ((Invoke-Git $repository @('remote', '-v') -AllowFailure).Output -split "`n")) {
            if ($url -match 'github\.com[:/]+([^/\s]+)/') { Add-IdentityTerm 'GitHub 帳號' $Matches[1] }
        }
    }
    try {
        Add-IdentityTerm 'Office 使用者名稱' (Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Office\Common\UserInfo' -Name UserName -ErrorAction Stop).UserName
    } catch {
        Write-Verbose '這台電腦沒有 Office 使用者名稱的設定。'
    }

    $recorder = Get-Content -LiteralPath (Join-Path $sourceRoot $manifest.documentProperties) -Raw -Encoding utf8 | ConvertFrom-Json
    $recordedValues = @($recorder.creator, $recorder.lastModifiedBy | Where-Object { $_ } | ForEach-Object { $_, [Security.SecurityElement]::Escape($_) } | Select-Object -Unique)
    $legacyHashes = @{}
    foreach ($item in $manifest.legacyWorkbooks.files) { $legacyHashes[$item.path] = $item.sha256.ToLowerInvariant() }

    function Test-Identity {
        # 送審文件的記錄人員是使用者指定的值，先拿掉再找。
        param([string] $Text, [string] $Where)
        foreach ($value in $recordedValues) { $Text = $Text.Replace($value, '') }
        foreach ($term in $identityTerms) {
            if ($term.Pattern.IsMatch($Text)) { Add-Finding $term.Label $Where }
        }
    }

    function Test-Text {
        param([string] $Text, [string] $Where, [switch] $IncludeInvisible)
        foreach ($finding in Get-ReleaseCopyTextFinding -Text $Text -IncludeInvisible:$IncludeInvisible) {
            Add-Finding $finding.Name "$Where 第 $($finding.Line) 行"
        }
        Test-Identity $Text $Where
    }

    $binaryExtensions = @('.xlsx', '.xlsm', '.docx', '.pptx', '.png', '.jpg', '.jpeg', '.gif', '.bmp', '.ico', '.ttf', '.otf',
        '.woff', '.woff2', '.zip', '.pdf', '.exe', '.dll', '.bin')

    function Read-ZipParts {
        param([string] $Path)
        $zip = [IO.Compression.ZipFile]::OpenRead($Path)
        try {
            foreach ($entry in $zip.Entries) {
                $buffer = [IO.MemoryStream]::new()
                $stream = $entry.Open()
                try { $stream.CopyTo($buffer) } finally { $stream.Dispose() }
                [pscustomobject]@{ Name = $entry.FullName; Bytes = $buffer.ToArray() }
            }
        } finally { $zip.Dispose() }
    }

    function Test-OfficeFile {
        param([string] $Path, [string] $Target)
        $legacy = $legacyHashes.ContainsKey($Target)
        if ($legacy -and (Get-Sha256 $Path) -ne $legacyHashes[$Target]) {
            Add-Finding '舊版範本的檔案雜湊和 manifest.json 不同，內容改過要重新確認' $Target
            $legacy = $false
        }
        $sat = $Target.StartsWith($manifest.satDocuments, [StringComparison]::Ordinal)
        $parts = @(Read-ZipParts $Path)
        $names = @($parts | ForEach-Object { $_.Name })
        foreach ($part in $parts) {
            if ($part.Name -notmatch '\.(xml|rels)$') { continue }
            $text = $utf8.GetString($part.Bytes)
            $where = "$Target 的 $($part.Name)"
            if ($part.Name -eq 'docProps/core.xml') {
                foreach ($field in @(@{ Tag = 'dc:creator'; Expected = [string]$recorder.creator }, @{ Tag = 'cp:lastModifiedBy'; Expected = [string]$recorder.lastModifiedBy })) {
                    $match = [regex]::Match($text, "<$($field.Tag)>([^<]*)</$($field.Tag)>")
                    $value = if ($match.Success) { [Net.WebUtility]::HtmlDecode($match.Groups[1].Value) } else { '' }
                    if (-not $legacy -and $value -ne '' -and -not ($sat -and $value -ceq $field.Expected)) {
                        Add-Finding "文件屬性的 $($field.Tag) 不是送審設定的值，也不是空的" $Target
                    }
                }
            }
            if (-not $legacy) {
                if ($part.Name -eq 'docProps/app.xml') {
                    foreach ($tag in 'Company', 'Manager', 'HyperlinkBase') {
                        if ($text -match "<$tag>[^<]+</$tag>") { Add-Finding "文件屬性的 $tag 不是空的" $Target }
                    }
                }
                if ($part.Name -eq 'docProps/custom.xml' -and $text -match '<property\b') { Add-Finding '有自訂文件屬性' $Target }
                if ($part.Name -eq 'xl/workbook.xml' -and $text -match 'absPath') { Add-Finding '活頁簿記著存檔資料夾路徑' $Target }
                if ($part.Name -match '\.rels$' -and $text -match 'TargetMode="External"' -and
                    $text -match 'Target="(?:file:|[A-Za-z]:\\|\\\\)') {
                    Add-Finding '有指向本機或網路磁碟的連結' $Target
                }
                if ($text -match '[A-Za-z]:\\Users\\') { Add-Finding '有本機的使用者資料夾路徑' $where }
            }
            Test-Text $text $where
        }
        if (-not $legacy) {
            if ($names | Where-Object { $_ -match '(^|/)printerSettings/' }) { Add-Finding '有印表機設定' $Target }
            if ($names | Where-Object { $_ -match '^xl/externalLinks/' }) { Add-Finding '有外部活頁簿連結' $Target }
            if ($names | Where-Object { $_ -match '(^|/)persons/|(comments|threadedComments)\d*\.xml$|^word/comments' }) {
                Add-Finding '有註解或註解者名單' $Target
            }
        }
    }

    function Test-PngFile {
        param([string] $Path, [string] $Target)
        $bytes = [IO.File]::ReadAllBytes($Path)
        $position = 8
        while ($position + 8 -le $bytes.Length) {
            $length = ([int]$bytes[$position] -shl 24) -bor ([int]$bytes[$position + 1] -shl 16) -bor ([int]$bytes[$position + 2] -shl 8) -bor [int]$bytes[$position + 3]
            $kind = [Text.Encoding]::ASCII.GetString($bytes, $position + 4, 4)
            if ($kind -in 'tEXt', 'zTXt', 'iTXt', 'eXIf') { Add-Finding "PNG 帶有 $kind 文字或拍攝資訊" $Target }
            if ($kind -eq 'IEND' -or $length -lt 0) { break }
            $position += 12 + $length
        }
    }

    function Test-PdfFile {
        param([string] $Path, [string] $Target)
        $text = [Text.Encoding]::Latin1.GetString([IO.File]::ReadAllBytes($Path))
        if ($text -match '/Author\s*\((?!\))' -or $text -match '<dc:creator>') { Add-Finding 'PDF 有作者屬性' $Target }
        Test-Text $text $Target
    }

    function Test-MarkdownLinks {
        param([string] $Text, [string] $Target)
        $folder = Split-Path $Target -Parent
        foreach ($match in [regex]::Matches($Text, '\]\((?:<(?<link>[^>]+)>|(?<link>[^)\s]+))\)')) {
            $link = $match.Groups['link'].Value
            if ($link -match '^(?:[a-z][a-z0-9+.-]*:|#)') { continue }
            $link = [Uri]::UnescapeDataString(($link -split '#')[0])
            if (-not $link) { continue }
            $resolved = [IO.Path]::GetFullPath((Join-Path (Join-Path $copyRoot $folder) $link))
            $inside = $resolved.StartsWith([IO.Path]::GetFullPath($copyRoot), [StringComparison]::OrdinalIgnoreCase)
            if (-not $inside -or -not (Test-Path -LiteralPath $resolved)) { Add-Finding '說明檔的連結指不到發布副本裡的檔案' "$Target 的 $link" }
        }
    }

    foreach ($target in $release.Keys) {
        $path = Join-Path $copyRoot $target
        if ($script:ReleaseCopyForbiddenPath.IsMatch($target)) { Add-Finding '發布副本不帶的路徑' $target }
        foreach ($finding in Get-ReleaseCopyTextFinding -Text $target) { Add-Finding "路徑有$($finding.Name)" $target }
        Test-Identity $target "路徑 $target"

        $extension = [IO.Path]::GetExtension($target).ToLowerInvariant()
        if ($extension -in '.xlsx', '.xlsm', '.docx', '.pptx') { Test-OfficeFile $path $target; continue }
        if ($extension -eq '.png') { Test-PngFile $path $target; continue }
        if ($extension -eq '.pdf') { Test-PdfFile $path $target; continue }
        if ($extension -in $binaryExtensions) { continue }

        $bytes = [IO.File]::ReadAllBytes($path)
        if ([Array]::IndexOf($bytes, [byte]0, 0, [Math]::Min($bytes.Length, 8000)) -ge 0) {
            Add-Finding '看起來是二進位檔，但副檔名不在已知的清單' $target
            continue
        }
        $text = $utf8.GetString($bytes)
        if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) { $text = $text.Substring(1) }
        Test-Text $text $target -IncludeInvisible
        if ($extension -eq '.md') { Test-MarkdownLinks $text $target }
    }

    if ($findings.Count -gt 0) {
        Write-Host "掃描沒有通過，共 $($findings.Count) 項："
        $findings | Select-Object -Unique | ForEach-Object { Write-Host "  $_" }
        throw '掃描沒有通過，發布副本的資料夾沒有改動。'
    }
    Write-Host '掃描：通過'

    # ---- 4. 建置與測試 ----

    if (-not $SkipTests) {
        # 測試的排除條件、順序種子與要移除的環境變數，和這個儲存庫平常的完整測試讀同一份設定。
        # 移除環境變數是為了不讓測試讀到這台電腦實際的案件資料夾，也不在另一個桌面開視窗。
        $testSettings = $lanes.testSettings
        $isolated = @($testSettings.environmentVariablesToRemove)
        $testArguments = [Collections.Generic.List[string]]::new()
        foreach ($excludedProfile in @($testSettings.excludedProfiles)) {
            $testArguments.AddRange([string[]]@('--filter-not-trait', "TestProfile=$excludedProfile"))
        }
        $testArguments.AddRange([string[]]@('--seed', [string]$testSettings.seed, '--results-directory', (Join-Path $workRoot 'test-results')))

        # 和驗證框架的建置一樣加 --disable-build-servers，編譯伺服器不會在建置結束後留在背景。
        Invoke-Logged '建置方案' 'dotnet' @('build', 'JET/JET.slnx', '--configuration', 'Debug', '--disable-build-servers') $copyRoot (Join-Path $logRoot 'build.log')
        $testHost = Join-Path $copyRoot 'JET/tests/JET.Tests/bin/Debug/net10.0-windows/JET.Tests.exe'
        Invoke-Logged 'C# 測試' $testHost $testArguments.ToArray() $copyRoot (Join-Path $logRoot 'backend-tests.log') -RemoveEnvironment $isolated
        Invoke-Logged '前端測試' 'node' @('--test', '--test-concurrency=4', 'JET/tests/frontend/') $copyRoot (Join-Path $logRoot 'frontend-tests.log') -RemoveEnvironment $isolated
    }

    if (-not $Mirror) {
        Write-Host "沒有加 Mirror，發布副本的資料夾沒有改動。組成的副本在 $copyRoot。"
        return
    }

    # ---- 5. 鏡像 ----
    # 發布副本是儲存庫的根目錄、和這個儲存庫不重疊、在 main 分支，開頭已經確認過。

    # 提交身分和這個儲存庫提交時用的相同。
    $identityName = (Invoke-Git $repoRoot @('config', 'user.name')).Output.Trim()
    $identityEmail = (Invoke-Git $repoRoot @('config', 'user.email')).Output.Trim()
    foreach ($setting in @(@('user.name', $identityName), @('user.email', $identityEmail))) {
        $current = (Invoke-Git $Destination @('config', '--local', '--get', $setting[0]) -AllowFailure).Output.Trim()
        if ($current -cne $setting[1]) { [void](Invoke-Git $Destination @('config', '--local', $setting[0], $setting[1])) }
    }

    # 本機的忽略清單：在發布副本用 AI 工具時產生的設定檔不會被加入 Git。
    $excludePath = [IO.Path]::GetFullPath((Invoke-Git $Destination @('rev-parse', '--git-path', 'info/exclude')).Output.Trim(), $Destination)
    $excludeLines = if (Test-Path -LiteralPath $excludePath) { @(Get-Content -LiteralPath $excludePath -Encoding utf8) } else { @() }
    $missing = @($manifest.localExclude | Where-Object { $_ -notin $excludeLines })
    if ($missing.Count -gt 0) {
        [void][IO.Directory]::CreateDirectory((Split-Path $excludePath -Parent))
        $added = @('', '# 這台電腦上的 AI 工具與本機紀錄，不加入發布副本') + $missing
        [IO.File]::AppendAllText($excludePath, (($added -join "`n") + "`n"), $utf8)
    }

    # 提交與推送前的檢查。
    $hooksPath = [IO.Path]::GetFullPath((Invoke-Git $Destination @('rev-parse', '--git-path', 'hooks')).Output.Trim(), $Destination)
    if (-not ($hooksPath + '\').StartsWith((Join-Path $Destination '.git') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "發布副本的 hook 資料夾不在它自己的 .git 裡（core.hooksPath 另有設定），沒有裝上檢查：$hooksPath"
    }
    [void][IO.Directory]::CreateDirectory($hooksPath)
    foreach ($name in 'commit-msg', 'pre-commit', 'pre-push', 'release-copy-check.ps1', 'release-copy-patterns.ps1') {
        $content = [IO.File]::ReadAllText((Join-Path $toolRoot "hooks/$name"), $utf8).Replace("`r`n", "`n")
        [IO.File]::WriteAllText((Join-Path $hooksPath $name), $content, $utf8)
    }

    # 只動 Git 看得到的檔案：先刪掉這次沒有的檔，再寫入新增或改過的檔。
    # 發布副本還沒有任何提交時清單是空的，一律包成陣列再用。
    $visible = @(Split-NulList (Invoke-Git $Destination @('ls-files', '-z', '--cached', '--others', '--exclude-standard')).Output)
    $tracked = [Collections.Generic.HashSet[string]]::new([string[]]@(Split-NulList (Invoke-Git $Destination @('ls-files', '-z')).Output), [StringComparer]::Ordinal)
    $removed = [Collections.Generic.List[string]]::new()
    foreach ($relative in ($visible | Select-Object -Unique)) {
        if ($release.Contains($relative)) { continue }
        $path = Join-Path $Destination $relative
        if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
        $removed.Add($relative)
        $parent = Split-Path $path -Parent
        while ($parent -and $parent.Length -gt $Destination.Length -and (Test-Path -LiteralPath $parent) -and
            -not (Get-ChildItem -LiteralPath $parent -Force | Select-Object -First 1)) {
            Remove-Item -LiteralPath $parent -Force
            $parent = Split-Path $parent -Parent
        }
    }
    $addedCount = 0
    $updatedCount = 0
    foreach ($target in $release.Keys) {
        $from = Join-Path $copyRoot $target
        $to = Join-Path $Destination $target
        if (Test-Path -LiteralPath $to -PathType Leaf) {
            if ((Get-Item -LiteralPath $to).Length -eq (Get-Item -LiteralPath $from).Length -and (Get-Sha256 $to) -eq (Get-Sha256 $from)) { continue }
            $updatedCount++
        } else {
            $addedCount++
        }
        [void][IO.Directory]::CreateDirectory((Split-Path $to -Parent))
        Copy-Item -LiteralPath $from -Destination $to -Force
    }
    $changes = @((Invoke-Git $Destination @('status', '--porcelain', '--untracked-files=all')).Output -split "`n" | Where-Object { $_.Trim() }).Count
    Write-Host "鏡像到發布副本：新增 $addedCount 個、更新 $updatedCount 個、刪除 $($removed.Count) 個檔；Git 看到 $changes 項變動"

    if (-not $CommitMessageFile) {
        Write-Host '沒有加 CommitMessageFile，發布副本沒有提交。'
        return
    }

    # ---- 6. 提交、標籤與推送 ----
    # 提交訊息開頭已經檢查過；提交時發布副本的 commit-msg 檢查會再看一次。

    if ($changes -gt 0) {
        $pathspec = Join-Path $workRoot 'pathspec.txt'
        $staging = @($release.Keys) + @($removed | Where-Object { $tracked.Contains($_) })
        [IO.File]::WriteAllText($pathspec, ($staging -join "`0"), $utf8)
        # 路徑一律當成字面處理，檔名裡的 [ 與 * 不會被當成萬用字元。
        [void](Invoke-Git $Destination @('--literal-pathspecs', 'add', "--pathspec-from-file=$pathspec", '--pathspec-file-nul'))
        $index = @(Split-NulList (Invoke-Git $Destination @('ls-files', '-z')).Output)
        $indexSet = [Collections.Generic.HashSet[string]]::new([string[]]$index, [StringComparer]::OrdinalIgnoreCase)
        $unexpected = @($index | Where-Object { -not $release.Contains($_) })
        $absent = @($release.Keys | Where-Object { -not $indexSet.Contains($_) })
        if ($unexpected.Count -or $absent.Count) {
            throw "暫存的檔案和這次的發布副本不同：多了 $($unexpected.Count) 個、少了 $($absent.Count) 個。提交前先查看發布副本的 git status。"
        }
        [void](Invoke-Git $Destination @('commit', '-F', $messagePath))
        $destinationCommit = (Invoke-Git $Destination @('rev-parse', 'HEAD')).Output.Trim()
        Write-Host "發布副本提交：$($destinationCommit.Substring(0, 12))"
    } else {
        # 例如上次推送失敗後重跑：內容和上次提交相同，不再提交，後面照樣加標籤與推送。
        $destinationCommit = (Invoke-Git $Destination @('rev-parse', 'HEAD')).Output.Trim()
        Write-Host "發布副本和上次的提交 $($destinationCommit.Substring(0, 12)) 相同，這次不提交。"
    }

    if ($Tag) {
        $tagged = Invoke-Git $Destination @('rev-parse', '-q', '--verify', "refs/tags/$Tag^{commit}") -AllowFailure
        if ($tagged.ExitCode -ne 0) {
            [void](Invoke-Git $Destination @('tag', '-a', $Tag, '-m', $Tag))
            Write-Host "加上標籤：$Tag"
        } elseif ($tagged.Output.Trim() -ne $destinationCommit) {
            throw "標籤 $Tag 已經用在發布副本的另一個提交上。換一個標籤名稱，或先確認舊標籤要不要保留。"
        } else {
            Write-Host "標籤 $Tag 已經在這個提交上，沿用。"
        }
    }

    if ($changes -gt 0) {
        $recordPath = Join-Path $repoRoot 'local/release-copy/commits.md'
        if (-not (Test-Path -LiteralPath $recordPath)) {
            [void][IO.Directory]::CreateDirectory((Split-Path $recordPath -Parent))
            [IO.File]::WriteAllText($recordPath, "# 發布副本的提交對應`n`n| 時間 | 這個儲存庫的提交 | 發布副本的提交 | 標籤 |`n|:---|:---|:---|:---|`n", $utf8)
        }
        [IO.File]::AppendAllText($recordPath, "| $((Get-Date).ToString('yyyy-MM-dd HH:mm')) | $sourceCommit | $destinationCommit | $Tag |`n", $utf8)
    }

    if ($Push) {
        [void](Invoke-Git $Destination @('push', 'origin', 'main'))
        if ($Tag) { [void](Invoke-Git $Destination @('push', 'origin', "refs/tags/$Tag")) }
        Write-Host '推送：完成'
    }
}
finally {
    $release = & $harness { param($Lock, $RunId) Exit-JetExclusiveLock -Lock $Lock -RunId $RunId } $lock $runId
    if ($release.status -ne 'passed') {
        Write-Warning "共享鎖沒有正常釋放（$($release.reason)）。之後的驗證如果一直顯示被鎖住，先確認這次的 pwsh 已經結束。"
    }
}
