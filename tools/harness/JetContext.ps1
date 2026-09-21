using namespace System.IO

# Read-only orientation. Missing evidence is reported, not turned into a development gate.
function Read-JetContextText {
    param([string] $RepositoryRoot, [string] $RelativePath, [int] $MaximumBytes = 2097152)
    $full = [Path]::GetFullPath((Join-Path $RepositoryRoot $RelativePath))
    Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $full
    $file = Get-Item -LiteralPath $full -ErrorAction Stop
    if ($file.Length -gt $MaximumBytes) { throw [InvalidDataException]::new('Context source exceeds the read budget.') }
    return [File]::ReadAllText($full, [Text.Encoding]::UTF8)
}

function Get-JetContextSection {
    param([string] $Text, [string] $Heading)
    $match = [regex]::Match($Text, '(?ms)^## ' + [regex]::Escape($Heading) + '\r?\n(?<body>.*?)(?=^## |\z)')
    if (-not $match.Success) { return '' }
    return $match.Groups['body'].Value.Trim()
}

function Get-JetContextBrief {
    param([string] $Text)
    $match = [regex]::Match($Text, '(?s)<!-- jet-context:start -->(?<body>.*?)<!-- jet-context:end -->')
    if (-not $match.Success) {
        $match = [regex]::Match($Text, '(?ms)^## [^\r\n]+\r?\n(?<body>.*?)(?=^## |\z)')
    }
    $body = if ($match.Success) { $match.Groups['body'].Value.Trim() } else { $Text.Trim() }
    $truncated = $body.Length -gt 7000
    if ($truncated) { $body = $body.Substring(0, 7000) }
    return [ordered]@{
        text = $body
        truncated = $truncated
        startLine = if ($match.Success) { 1 + ([regex]::Matches($Text.Substring(0, $match.Groups['body'].Index), '\n')).Count } else { 1 }
    }
}

function Get-JetContext {
    param([string] $RepositoryRoot)
    $notices = [Collections.Generic.List[string]]::new()
    $plan = $null
    try {
        $statusText = Read-JetContextText $RepositoryRoot 'docs/development-status.md'
        $active = Get-JetContextSection $statusText '目前大型計畫'
        $links = @([regex]::Matches($active, '\]\((?<path>specs/[^)#\r\n]+\.md)(?:#[^)]*)?\)') |
            ForEach-Object { $_.Groups['path'].Value } | Sort-Object -Unique)
        if ($links.Count -eq 1) {
            $relative = 'docs/' + $links[0]
            $specRoot = [Path]::GetFullPath((Join-Path $RepositoryRoot 'docs/specs'))
            $full = [Path]::GetFullPath((Join-Path $RepositoryRoot $relative))
            if (-not (Test-JetDescendantPath -Root $specRoot -Candidate $full)) {
                throw [InvalidDataException]::new('Active plan link is outside docs/specs.')
            }
            $brief = Get-JetContextBrief (Read-JetContextText $RepositoryRoot $relative)
            $plan = [ordered]@{ path = $relative; startLine = $brief.startLine; text = $brief.text; truncated = $brief.truncated }
        }
        elseif ($links.Count -gt 1) { $notices.Add('目前大型計畫有多個不同連結，請依本次任務判斷；工具未自行選擇。') }
        else { $notices.Add('開發現況沒有指出現行計畫，可從本次任務與專案方向直接開始。') }
    }
    catch { $notices.Add('現行計畫摘要未能讀取；可直接查閱 docs/development-status.md 和本次任務。') }

    $workspace = [ordered]@{ branch = $null; head = $null; available = $false; scope = 'tracked and untracked code, docs and tools; excludes data'; counts = @{}; paths = @(); truncated = $false }
    try {
        $branch = Invoke-JetGitLines $RepositoryRoot @('branch', '--show-current')
        $head = Invoke-JetGitLines $RepositoryRoot @('rev-parse', '--verify', 'HEAD')
        $paths = @('AGENTS.md', 'CLAUDE.md', '.github/copilot-instructions.md', '.claude', 'docs', 'tools', 'src', '.agents')
        $changed = Invoke-JetGitLines $RepositoryRoot (@('--no-optional-locks', 'status', '--porcelain=v1', '--untracked-files=no', '--') + $paths)
        $untracked = Invoke-JetGitLines $RepositoryRoot (@('ls-files', '--others', '--exclude-standard', '--') + $paths)
        if ($changed.ExitCode -ne 0 -or $untracked.ExitCode -ne 0) { throw [InvalidOperationException]::new('Git status unavailable.') }
        $workspace.available = $true
        if ($branch.ExitCode -eq 0) { $workspace.branch = $branch.Lines -join '' }
        if ($head.ExitCode -eq 0) { $workspace.head = $head.Lines -join '' }
        $items = @(
            foreach ($line in $changed.Lines) {
                if ($line.Length -ge 4) { [ordered]@{ state = $line.Substring(0, 2); path = $line.Substring(3) } }
            }
            foreach ($line in $untracked.Lines) { [ordered]@{ state = '??'; path = $line } }
        )
        foreach ($item in $items) {
            $area = switch -Regex ($item.path) {
                '^docs/' { 'docs'; break }
                '^tools/' { 'harness'; break }
                '^src/.*/tests/' { 'tests'; break }
                '^src/' { 'product'; break }
                default { 'agent-guidance' }
            }
            if (-not $workspace.counts.ContainsKey($area)) { $workspace.counts[$area] = 0 }
            $workspace.counts[$area]++
        }
        $workspace.paths = @($items | Select-Object -First 40)
        $workspace.truncated = $items.Count -gt 40
    }
    catch { $notices.Add('Git 狀態未能取得，未將工作樹推定為乾淨；可直接查核目前檔案。') }

    $evidence = [Collections.Generic.List[object]]::new()
    $seenSelections = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    try {
        $runs = Join-Path $RepositoryRoot 'artifacts/harness/runs'
        Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $runs
        if (Test-Path -LiteralPath $runs -PathType Container) {
            $directories = @(Get-ChildItem -LiteralPath $runs -Directory | Where-Object { $_.Name -match '^\d{8}-\d{9}-[a-f0-9]{32}$' } |
                Sort-Object Name -Descending | Select-Object -First 20)
            foreach ($directory in $directories) {
                $relative = 'artifacts/harness/runs/' + $directory.Name + '/receipt.json'
                try {
                    $receipt = Read-JetContextText $RepositoryRoot $relative | ConvertFrom-Json -AsHashtable -Depth 30
                    $selection = ConvertTo-Json -InputObject @($receipt.command, $receipt.configuration, $receipt.filter, $receipt['contractScenario'], $receipt['guiScenario']) -Compress
                    if (-not $seenSelections.Add($selection)) { continue }
                    # Only summary fields; never reproduce raw logs, exception messages or private-case inputs.
                    $evidence.Add([ordered]@{
                        path = $relative
                        command = $receipt.command
                        status = $receipt.status
                        configuration = $receipt.configuration
                        filter = $receipt.filter
                        contractScenario = $receipt['contractScenario']
                        guiScenario = $receipt['guiScenario']
                        partial = $receipt.partial
                        head = $receipt.repository.head
                        completedUtc = $receipt.completedUtc
                        appliesToCurrentTree = 'not_verified'
                    })
                }
                catch { $notices.Add('一份近期收據無法讀取，已保留其他證據；可到執行目錄查核。') }
                if ($evidence.Count -ge 8) { break }
            }
        }
    }
    catch { $notices.Add('近期驗證收據未能取得，不影響讀取計畫或繼續開發。') }

    $availableChecks = @()
    try {
        $registry = Read-JetRegistry
        $availableChecks = @($registry.commands.Keys | Where-Object { $_ -ne 'Context' -and $registry.commands[$_].enabled } | Sort-Object)
    }
    catch { $notices.Add('驗證命令目錄未能載入，請查 tools/README.md；其他上下文仍可使用。') }

    return [ordered]@{
        schemaVersion = 1
        command = 'Context'
        status = 'observed'
        readOnly = $true
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        plan = $plan
        workspace = $workspace
        recentEvidence = @($evidence.ToArray())
        evidenceWindow = 'latest receipt per command/configuration/filter/scenario, up to 8 selections from the newest 20 run directories; not a full validation history'
        evidenceMeaning = '收據只證明當次執行；相同 HEAD 也不代表目前未提交內容已驗證。'
        availableChecks = $availableChecks
        navigation = @('docs/project-context.md', 'docs/jet-guide.md', 'docs/action-contract-manifest.md', 'docs/development-workflow.md', 'tools/README.md')
        guidance = '依本次任務界定交付範圍；計畫中的下一步表示工作順序，不是本輪停止點。查來源、修改、驗證並依結果繼續，直到本輪成果完成或有具體阻礙。'
        notices = @($notices.ToArray())
        privateData = @{ pathInspected = $false }
    }
}
