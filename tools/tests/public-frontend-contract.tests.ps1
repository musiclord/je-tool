#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $RepositoryRoot)
$ErrorActionPreference = 'Stop'
$modulePath = Join-Path $RepositoryRoot 'tools/harness/JetHarness.psm1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($modulePath, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw 'Harness source must parse.' }
$public = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.SwitchStatementAst] }, $true) |
    ForEach-Object { $_.Clauses } | Where-Object { $_.Item1.Extent.Text -ceq "'Public'" } |
    Where-Object { $_.Item2.Extent.Text.Contains("'public-tests'") }
if (@($public).Count -ne 1 -or -not $public.Item2.Extent.Text.Contains('Invoke-JetPublicFrontendSteps')) {
    throw 'Public must execute the shared frontend test pipeline after its .NET tests.'
}
$module = Import-Module $modulePath -Force -PassThru
& $module {
    param($root)
    $original = (Get-Item Function:Invoke-JetChildStep).ScriptBlock
    try {
        function Invoke-JetChildStep {
            param($RepositoryRoot, $RunDirectory, $Name, $FileName, $Arguments, $DisplayCommand,
                $MaximumCapturedBytes, $TimeoutSeconds, [switch]$OwnProcessTree, $EnvironmentVariablesToRemove)
            if (-not $OwnProcessTree -or $EnvironmentVariablesToRemove -notcontains 'JET_PRIVATE_CASE_ROOT') {
                throw 'Public frontend tests must own and clean their child tree and remove private inputs.'
            }
            [pscustomobject]@{ name = $Name; status = $(if ($script:failFrontend) { 'failed' } else { 'passed' });
                script = [IO.Path]::GetFileName($Arguments[2]) }
        }
        $registry = [pscustomobject]@{ limits = @{ maximumCapturedBytesPerStream = 262144 };
            testSettings = @{ environmentVariablesToRemove = @('JET_PRIVATE_CASE_ROOT') } }
        $script:failFrontend = $false
        $steps = @(Invoke-JetPublicFrontendSteps -RepositoryRoot $root -RunDirectory $root -Registry $registry -TimeoutSeconds 120)
        if ($steps.Count -ne 2 -or $steps[0].name -cne 'public-frontend-mapping' -or
            $steps[0].script -cne 'frontend-mapping-contract.tests.ps1' -or
            $steps[1].name -cne 'public-frontend-preview' -or $steps[1].script -cne 'frontend-preview-contract.tests.ps1') {
            throw 'Public must retain both frontend scripts in order and their independent results.'
        }
        $script:failFrontend = $true
        $failed = @(Invoke-JetPublicFrontendSteps -RepositoryRoot $root -RunDirectory $root -Registry $registry -TimeoutSeconds 120)
        if ($failed.Count -ne 1 -or $failed[0].status -cne 'failed') { throw 'Frontend failure must stop Public, not be hidden.' }
    } finally {
        Set-Item Function:Invoke-JetChildStep $original
        Remove-Variable failFrontend -Scope Script -ErrorAction SilentlyContinue
    }
} $RepositoryRoot
Write-Output 'Public frontend routing, isolation and failure propagation: passed.'
