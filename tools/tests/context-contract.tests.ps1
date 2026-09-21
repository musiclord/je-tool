#requires -Version 7.4
[CmdletBinding()]
param([string] $RepositoryRoot = ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$module = Import-Module (Join-Path $RepositoryRoot 'tools/harness/JetHarness.psm1') -Force -PassThru
$fixture = Join-Path $RepositoryRoot ('artifacts/harness/context-tests/' + [Guid]::NewGuid().ToString('N'))
$count = 0
function Check([bool] $Condition, [string] $Message) {
    $script:count++
    if (-not $Condition) { throw $Message }
}
function Put([string] $Relative, [string] $Value) {
    $path = Join-Path $fixture $Relative
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $path))
    [IO.File]::WriteAllText($path, $Value, [Text.UTF8Encoding]::new($false))
}
function Context {
    & $module { param($root, $contextScript); . $contextScript; Get-JetContext -RepositoryRoot $root } `
        $fixture (Join-Path $RepositoryRoot 'tools/harness/JetContext.ps1')
}
try {
    & $module {
        $script:contextGitMissing = $false
        $script:contextStatusReadOnly = $false
        function script:Invoke-JetGitLines {
            param([string] $RepositoryRoot, [string[]] $Arguments)
            if ($script:contextGitMissing) { throw 'simulated missing git' }
            $operation = $Arguments[0]
            if ($operation -ceq '--no-optional-locks') {
                $script:contextStatusReadOnly = $true
                $script:contextStatusArguments = $Arguments
                $operation = $Arguments[1]
            }
            $lines = switch ($operation) {
                'branch' { 'main' }
                'rev-parse' { 'synthetic-head' }
                'status' { ' M src/JET/JET/Example.cs'; 'M  docs/guide.md' }
                'ls-files' { 'tools/harness/NewExample.ps1' }
            }
            [pscustomobject]@{ ExitCode = 0; Lines = @($lines) }
        }
    }
    $active = "## 目前大型計畫`n[現行](specs/current.md)`n## 歷史`n[舊版](specs/old.md)"
    Put 'docs/development-status.md' $active
    Put 'docs/specs/current.md' "# 計畫`n<!-- jet-context:start -->`n現行：固定 6 位，繼續 AST 修正。`n<!-- jet-context:end -->`n## 歷史`n舊提案：動態尾零。"
    Put 'data/test-case/sentinel.txt' 'SYNTHETIC_PRIVATE_CONTENT_MUST_NOT_APPEAR'
    $before = @(Get-ChildItem $fixture -Recurse -File | ForEach-Object { (Get-FileHash $_.FullName).Hash }) -join ','
    $result = Context
    $after = @(Get-ChildItem $fixture -Recurse -File | ForEach-Object { (Get-FileHash $_.FullName).Hash }) -join ','
    Check ($result.status -ceq 'observed' -and $result.readOnly) 'Context is observation, never a test pass.'
    Check ($result.plan.path -ceq 'docs/specs/current.md') 'Use the active section, not history links.'
    Check ($result.plan.text.Contains('固定 6 位') -and -not $result.plan.text.Contains('動態尾零')) 'The brief must exclude superseded history.'
    Check ($result.workspace.counts.product -eq 1 -and $result.workspace.counts.harness -eq 1 -and $result.workspace.counts.docs -eq 1) 'Include untracked tools and distinguish changed areas.'
    Check ($before -ceq $after -and -not (Test-Path (Join-Path $fixture 'artifacts'))) 'Observation must not write receipts or change sources.'
    Check (& $module { $script:contextStatusReadOnly }) 'Git status must disable optional index refresh writes.'
    Check (-not (($result | ConvertTo-Json -Depth 15).Contains('SYNTHETIC_PRIVATE_CONTENT'))) 'Do not read private fixtures.'

    Put 'docs/specs/current.md' "# 計畫`n## 當前工作`n新的決定`n## 歷史`n舊決定"
    $result = Context
    Check ($result.plan.text -ceq '新的決定') 'An unmarked plan remains usable without a formatting gate or cache.'
    Put 'docs/specs/current.md' ('# Plan' + "`n" + ('x' * 7100))
    $result = Context
    Check ($result.plan.truncated -and $result.plan.text.Length -eq 7000) 'Bound large excerpts and disclose truncation.'

    Put 'docs/development-status.md' "## 目前大型計畫`n[一](specs/current.md) [二](specs/other.md)"
    $result = Context
    Check ($null -eq $result.plan -and $result.notices.Count -gt 0 -and $result.workspace.available) 'Ambiguity must not choose a plan silently or stop other observations.'
    Put 'docs/development-status.md' "## 目前大型計畫`n[來源](specs/../../data/test-case/sentinel.md)"
    Put 'data/test-case/sentinel.md' 'SYNTHETIC_PRIVATE_CONTENT_MUST_NOT_APPEAR'
    $result = Context
    Check ($null -eq $result.plan -and -not (($result | ConvertTo-Json -Depth 15).Contains('SYNTHETIC_PRIVATE_CONTENT'))) 'A plan link must not expand reads into case data.'

    Put 'docs/development-status.md' $active
    $prefix = 'artifacts/harness/runs/'
    $failure = '20260918-020000000-11111111111111111111111111111111'
    $pass = '20260918-010000000-22222222222222222222222222222222'
    Put ($prefix + $failure + '/receipt.json') '{"command":"Focused","status":"failed","configuration":"Debug","filter":"ExampleTests","partial":false,"repository":{"head":"synthetic-head"},"completedUtc":"2026-09-18T02:00:00Z","raw":"SYNTHETIC_PRIVATE_CONTENT"}'
    Put ($prefix + $pass + '/receipt.json') '{"command":"Public","status":"passed","configuration":"Debug","filter":null,"partial":false,"repository":{"head":"synthetic-head"},"completedUtc":"2026-09-18T01:00:00Z"}'
    Put ($prefix + '20260918-030000000-33333333333333333333333333333333/receipt.json') '{broken'
    $result = Context
    Check ($result.recentEvidence.Count -eq 2 -and $result.recentEvidence[0].status -ceq 'failed') 'Keep the latest failure alongside earlier passes, skipping only unreadable receipts.'
    Check ($result.recentEvidence[1].appliesToCurrentTree -ceq 'not_verified') 'A matching HEAD must not certify a dirty tree.'
    Check (-not (($result | ConvertTo-Json -Depth 15).Contains('SYNTHETIC_PRIVATE_CONTENT'))) 'Receipt raw content must not escape into the brief.'

    & $module { $script:contextGitMissing = $true }
    $result = Context
    Check (-not $result.workspace.available -and $null -ne $result.plan -and $result.recentEvidence.Count -eq 2) 'Missing Git must not block plan and evidence retrieval.'
    Put 'docs/development-status.md' 'No active project section.'
    $result = Context
    Check ($result.status -ceq 'observed' -and $result.availableChecks -contains 'Focused') 'Missing context remains an observation, not a failed development gate.'
    & $module { function script:Read-JetRegistry { throw 'simulated unavailable registry' } }
    $result = Context
    Check ($result.status -ceq 'observed' -and $result.availableChecks.Count -eq 0 -and $result.recentEvidence.Count -eq 2) 'A broken test registry should not hide the remaining context.'

    # Repeated documentation runs must not crowd out the latest product failure.
    # Keep different test selections separate, and never replace a failure with an older pass.
    Put ($prefix + '20260918-040000000-44444444444444444444444444444444/receipt.json') '{"command":"Public","status":"failed","configuration":"Debug","filter":null,"partial":true,"repository":{"head":"synthetic-head"},"completedUtc":"2026-09-18T04:00:00Z"}'
    foreach ($hour in 5..14) {
        $run = '20260918-{0:00}0000000-55555555555555555555555555555555' -f $hour
        Put ($prefix + $run + '/receipt.json') '{"command":"Documentation","status":"passed","configuration":"Debug","filter":null,"partial":false,"repository":{"head":"synthetic-head"},"completedUtc":"2026-09-18T05:00:00Z"}'
    }
    Put ($prefix + '20260918-150000000-66666666666666666666666666666666/receipt.json') '{"command":"Focused","status":"passed","configuration":"Debug","filter":"OtherTests","partial":false,"repository":{"head":"synthetic-head"},"completedUtc":"2026-09-18T15:00:00Z"}'
    $result = Context
    Check (@($result.recentEvidence | Where-Object command -CEQ 'Documentation').Count -eq 1) 'Show only the latest receipt for a repeated selection.'
    $public = @($result.recentEvidence | Where-Object command -CEQ 'Public')
    Check ($public.Count -eq 1 -and $public[0].status -ceq 'failed' -and $public[0].partial) 'Keep the latest partial failure, not the earlier successful Public run.'
    Check (@($result.recentEvidence | Where-Object command -CEQ 'Focused').Count -eq 2) 'Different focused test selections must remain visible.'
    Check ($result.recentEvidence[0].filter -ceq 'OtherTests') 'Summaries retain newest-first ordering.'
    foreach ($case in @(@('17', 'Context'), @('18', 'FrontendMapping'))) {
        $run = '20260918-' + $case[0] + '0000000-77777777777777777777777777777777'
        Put ($prefix + $run + '/receipt.json') ('{"command":"Contract","status":"passed","configuration":"Debug","filter":null,"contractScenario":"' + $case[1] + '","partial":false,"repository":{"head":"synthetic-head"},"completedUtc":"2026-09-18T17:00:00Z"}')
    }
    $result = Context
    $contracts = @($result.recentEvidence | Where-Object command -CEQ 'Contract')
    Check ($contracts.Count -eq 2 -and $contracts[0].contractScenario -ceq 'FrontendMapping' -and $contracts[1].contractScenario -ceq 'Context') 'Different contract scenarios stay identifiable and are not deduplicated together.'

    # Agent adapters affect a handoff even when no product file changed.
    & $module { $script:contextGitMissing = $false }
    $result = Context
    $arguments = & $module { $script:contextStatusArguments }
    Check ($arguments -ccontains 'CLAUDE.md' -and $arguments -ccontains '.claude' -and $arguments -ccontains '.github/copilot-instructions.md') 'Include cross-agent adapters in the observed working tree.'
    [Console]::Out.WriteLine((@{ status = 'passed'; assertions = $count } | ConvertTo-Json -Compress))
}
finally {
    $expectedParent = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'artifacts/harness/context-tests')) + [IO.Path]::DirectorySeparatorChar
    $resolved = [IO.Path]::GetFullPath($fixture)
    if ($resolved.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
