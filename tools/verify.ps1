#requires -Version 7.4

<#
.SYNOPSIS
    JET 儲存庫唯一的公開驗證入口。

.DESCRIPTION
    提供驗證框架自身檢查、文件用語檢查、Restore、Build、Focused／Public 產品測試、Provider／Package
    驗證、隔離的 GUI 情境、五份合成報告與科目配對工作檔的原生 Excel 往返檢查、明示授權的 PrivateCase，以及在一次性
    候選快照中依序執行公開必要檢查的 ReleaseCandidate。
#>

[CmdletBinding()]
param(
    [string] $Command = 'Help',
    [string] $Configuration = 'Debug',
    [switch] $NoRestore,
    [string] $Filter = '',
    [string] $WaitSeconds = '0',
    [string] $TimeoutSeconds = '1800',
    [string] $EvidenceRoot = 'artifacts/harness/runs',
    [string] $ContractScenario = 'Normal',
    [string] $ProbeSeconds = '8'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false, $true)
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8

try {
    Import-Module (Join-Path $PSScriptRoot 'harness/JetHarness.psm1') -Force -ErrorAction Stop
    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    $exitCode = Invoke-JetHarness `
        -RepositoryRoot $repositoryRoot `
        -Command $Command `
        -Configuration $Configuration `
        -NoRestore:$NoRestore `
        -Filter $Filter `
        -WaitSeconds $WaitSeconds `
        -TimeoutSeconds $TimeoutSeconds `
        -EvidenceRoot $EvidenceRoot `
        -ContractScenario $ContractScenario `
        -ProbeSeconds $ProbeSeconds
    exit $exitCode
}
catch {
    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    $safeMessage = $_.Exception.Message.Replace($repositoryRoot, '<repo>', [StringComparison]::OrdinalIgnoreCase)
    $fallback = [ordered]@{
        schemaVersion = 1
        ok = $false
        status = 'infrastructure_error'
        exitCode = 4
        error = [ordered]@{
            code = 'runner_bootstrap_failed'
            type = $_.Exception.GetType().Name
            message = $safeMessage
        }
        privateData = [ordered]@{
            pathInspected = $false
        }
    }
    [Console]::Out.WriteLine(($fallback | ConvertTo-Json -Depth 8 -Compress))
    exit 4
}
