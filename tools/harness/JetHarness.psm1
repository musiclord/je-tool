using namespace System.IO

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ExitPassed = 0
$script:ExitFailed = 1
$script:ExitBlocked = 2
$script:ExitUsage = 3
$script:ExitInfrastructure = 4
$script:ReceiptSchemaVersion = 1
$script:OwnerMarkerName = '.jet-harness-owner.json'
$script:Utf8 = [Text.UTF8Encoding]::new($false, $true)

$processSourcePath = Join-Path $PSScriptRoot 'JetHarness.Process.cs'
if ($null -eq ('Jet.Harness.BoundedProcessRunner' -as [type])) {
    Add-Type -Path $processSourcePath -ErrorAction Stop
}

# 主函式 Invoke-JetHarness 留在本檔，其餘函式依職責分在同目錄的 JetHarness.*.ps1，這裡用點號載入（dot-source）。
# 載入的函式在本模組的範圍執行，上面的嚴格模式、錯誤處理設定與 $script: 變數照常適用；對外仍只匯出 Invoke-JetHarness。
# 新增或改名檔案時，同步改 Get-JetRunnerIdentity 的檔案清單，收據才會記到它的雜湊。
. (Join-Path $PSScriptRoot 'JetHarness.Foundation.ps1')
. (Join-Path $PSScriptRoot 'JetHarness.Artifacts.ps1')
. (Join-Path $PSScriptRoot 'JetHarness.Results.ps1')
. (Join-Path $PSScriptRoot 'JetHarness.Steps.ps1')
. (Join-Path $PSScriptRoot 'JetHarness.Preflight.ps1')
. (Join-Path $PSScriptRoot 'JetHarness.Lanes.ps1')
. (Join-Path $PSScriptRoot 'JetHarness.ReleaseCandidate.ps1')

function Get-JetFirstRed {
    param(
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [object[]] $Steps,
        [Parameter(Mandatory = $true)] $Cleanup,
        $LockEvidence
    )

    foreach ($step in $Steps) {
        if ($step.status -cne 'passed') {
            # detail 是選用欄位：測試步驟會放第一個失敗測試與訊息，其他步驟沒有。步驟可能是 hashtable，
            # 也可能是從子收據讀回的 PSCustomObject，所以兩種都要能讀。
            $detail = $null
            $classification = $step.classification
            if ($classification -is [Collections.IDictionary]) {
                if ($classification.Contains('detail')) {
                    $detail = $classification['detail']
                }
            }
            elseif ($null -ne $classification -and $null -ne $classification.PSObject.Properties['detail']) {
                $detail = $classification.detail
            }
            return [ordered]@{
                kind = 'step'
                name = $step.name
                status = $step.status
                exitCode = $step.process.exitCode
                reason = $step.classification.reason
                detail = $detail
            }
        }
    }
    if ($Cleanup.status -cne 'passed') {
        return [ordered]@{ kind = 'cleanup'; status = $Cleanup.status; reason = $Cleanup.reason }
    }
    if ($null -ne $LockEvidence -and $LockEvidence.status -cne 'passed') {
        return [ordered]@{ kind = 'lock_cleanup'; status = $LockEvidence.status; reason = $LockEvidence.reason }
    }
    return $null
}

function New-JetUsageEnvelope {
    param(
        [Parameter(Mandatory = $true)] [string] $Code,
        [Parameter(Mandatory = $true)] [string] $Message
    )

    return [ordered]@{
        schemaVersion = $script:ReceiptSchemaVersion
        ok = $false
        status = 'usage_error'
        exitCode = $script:ExitUsage
        error = [ordered]@{ code = $Code; message = $Message }
        privateData = [ordered]@{ pathInspected = $false }
    }
}

function Invoke-JetPublicFrontendSteps {
    param(
        [Parameter(Mandatory)] [string] $RepositoryRoot,
        [Parameter(Mandatory)] [string] $RunDirectory,
        [Parameter(Mandatory)] $Registry,
        [Parameter(Mandatory)] [int] $TimeoutSeconds
    )
    $boundedTree = Get-JetMemoryBoundedTreeSettings -Registry $Registry
    foreach ($suite in @('mapping', 'preview')) {
        $script = "frontend-$suite-contract.tests.ps1"
        $step = Invoke-JetChildStep `
            -RepositoryRoot $RepositoryRoot -RunDirectory $RunDirectory `
            -Name "public-frontend-$suite" -FileName ([Environment]::ProcessPath) `
            -Arguments @('-NoProfile', '-File', (Join-Path $RepositoryRoot "tools/tests/$script"), '-RepositoryRoot', $RepositoryRoot) `
            -DisplayCommand @('pwsh', '-NoProfile', '-File', "tools/tests/$script") `
            -MaximumCapturedBytes ([int]$Registry.limits.maximumCapturedBytesPerStream) `
            -TimeoutSeconds $TimeoutSeconds `
            -EnvironmentVariablesToRemove @($Registry.testSettings.environmentVariablesToRemove) `
            @boundedTree
        $step
        if ($step.status -cne 'passed') { break }
    }
}

function Invoke-JetHarness {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $Command,
        [Parameter(Mandatory = $true)] [string] $Configuration,
        [switch] $NoRestore,
        [Parameter(Mandatory = $true)] [AllowEmptyString()] [string] $Filter,
        [string] $MutationScope = '',
        [Parameter(Mandatory = $true)] [string] $WaitSeconds,
        [Parameter(Mandatory = $true)] [string] $TimeoutSeconds,
        [Parameter(Mandatory = $true)] [string] $EvidenceRoot,
        [Parameter(Mandatory = $true)] [string] $ContractScenario,
        [Parameter(Mandatory = $true)] [string] $ProbeSeconds,
        [string] $GuiScenario = ''
    )

    try {
        $repositoryFull = [IO.Path]::GetFullPath($RepositoryRoot)
        if ($Command -ceq 'Context') {
            . (Join-Path $PSScriptRoot 'JetContext.ps1')
            Write-JetEnvelope (Get-JetContext -RepositoryRoot $repositoryFull)
            return $script:ExitPassed
        }
        $registry = Read-JetRegistry
    }
    catch {
        Write-JetEnvelope ([ordered]@{
            schemaVersion = $script:ReceiptSchemaVersion
            ok = $false
            status = 'infrastructure_error'
            exitCode = $script:ExitInfrastructure
            error = [ordered]@{ code = 'registry_invalid'; type = $_.Exception.GetType().Name }
            privateData = [ordered]@{ pathInspected = $false }
        })
        return $script:ExitInfrastructure
    }

    if ($Command -ceq 'Help') {
        Write-JetEnvelope ([ordered]@{
            schemaVersion = $script:ReceiptSchemaVersion
            ok = $true
            status = 'passed'
            exitCode = $script:ExitPassed
            command = 'Help'
            contractVersion = $registry.runnerContractVersion
            commands = @($registry.commands.Keys | Sort-Object)
            enabledLanes = @($registry.lanes.Keys | Where-Object { [bool]$registry.lanes[$_].enabled } | Sort-Object)
            plannedLanes = @($registry.lanes.Keys | Where-Object { -not [bool]$registry.lanes[$_].enabled } | Sort-Object)
            exitCodes = [ordered]@{
                passed = $script:ExitPassed
                failed = $script:ExitFailed
                blocked = $script:ExitBlocked
                usageError = $script:ExitUsage
                infrastructureError = $script:ExitInfrastructure
            }
            privateData = [ordered]@{ pathInspected = $false }
        })
        return $script:ExitPassed
    }

    try {
        if (-not $registry.commands.ContainsKey($Command) -or -not [bool]$registry.commands[$Command].enabled) {
            throw [ArgumentException]::new("Unknown or disabled command: $Command")
        }
        if ($registry.configurations -cnotcontains $Configuration) {
            throw [ArgumentException]::new("Unknown configuration: $Configuration")
        }
        $parsedWaitSeconds = ConvertTo-JetInteger -Value $WaitSeconds -Name 'WaitSeconds' -Minimum 0 `
            -Maximum ([int]$registry.limits.maximumWaitSeconds)
        $parsedTimeoutSeconds = ConvertTo-JetInteger -Value $TimeoutSeconds -Name 'TimeoutSeconds' -Minimum 1 `
            -Maximum ([int]$registry.limits.maximumTimeoutSeconds)
        $parsedProbeSeconds = ConvertTo-JetInteger -Value $ProbeSeconds -Name 'ProbeSeconds' -Minimum 1 -Maximum 30
        if (-not [bool]$registry.commands[$Command].requiresExclusiveLock -and $parsedWaitSeconds -ne 0) {
            throw [ArgumentException]::new('WaitSeconds 只適用於需要共享鎖的命令。')
        }
        if ($Command -ceq 'Contract') {
            if ($registry.contractScenarios -cnotcontains $ContractScenario) {
                throw [ArgumentException]::new("Unknown ContractScenario: $ContractScenario")
            }
        }
        elseif ($ContractScenario -cne 'Normal' -or $ProbeSeconds -cne '8') {
            throw [ArgumentException]::new('ContractScenario and ProbeSeconds are accepted only by Contract.')
        }
        if ($Command -ceq 'Focused') {
            if ([string]::IsNullOrWhiteSpace($Filter) -or $Filter.Length -gt 200 -or
                $Filter -notmatch '^[A-Za-z0-9_.+]+$') {
                throw [ArgumentException]::new(
                    'Focused requires a 1-200 character class or method substring using letters, digits, dot, underscore, or plus.')
            }
        }
        elseif (-not [string]::IsNullOrWhiteSpace($Filter)) {
            throw [ArgumentException]::new('Filter is accepted only by Focused.')
        }
        # GuiScenario 只供診斷單跑一個情境；收據標記 partial，不算 Gui 通過。
        if ($Command -ceq 'Gui') {
            if (-not [string]::IsNullOrWhiteSpace($GuiScenario) -and
                @($registry.guiSettings.scenarios | ForEach-Object { [string]$_.name }) -cnotcontains $GuiScenario) {
                throw [ArgumentException]::new("Unknown GuiScenario: $GuiScenario")
            }
        }
        elseif (-not [string]::IsNullOrWhiteSpace($GuiScenario)) {
            throw [ArgumentException]::new('GuiScenario is accepted only by Gui.')
        }
        if ($Command -ceq 'Mutation') {
            if ([string]::IsNullOrWhiteSpace($MutationScope)) { $MutationScope = 'GlProjectionGuard' }
            if ($MutationScope -cnotin @('GlProjectionGuard', 'MoneyScaling')) {
                throw [ArgumentException]::new('MutationScope must be GlProjectionGuard or MoneyScaling.')
            }
            if ($Configuration -cne 'Release' -or $parsedTimeoutSeconds -gt 1800) {
                throw [ArgumentException]::new('Mutation requires Configuration Release and at most 1800 seconds.')
            }
        }
        elseif (-not [string]::IsNullOrWhiteSpace($MutationScope)) {
            throw [ArgumentException]::new('MutationScope is accepted only by Mutation.')
        }
        if ($NoRestore -and $Command -notin @('Build', 'Focused', 'Public', 'Provider', 'Package', 'PrivateCase')) {
            throw [ArgumentException]::new(
                'NoRestore is accepted only by Build, Focused, Public, Provider, Package, or PrivateCase.')
        }
        if ($Command -ceq 'Package' -and $Configuration -cne 'Release') {
            throw [ArgumentException]::new('Package requires Configuration Release.')
        }
        if ($Command -ceq 'Gui' -and $Configuration -cne [string]$registry.guiSettings.configuration) {
            throw [ArgumentException]::new('Gui requires Configuration AgentGuiTest.')
        }
        if ($Command -ceq 'Excel' -and $Configuration -cne [string]$registry.excelSettings.configuration) {
            throw [ArgumentException]::new('Excel requires Configuration Release.')
        }
        if ($Command -ceq 'ReleaseCandidate' -and $Configuration -cne 'Release') {
            throw [ArgumentException]::new('ReleaseCandidate requires Configuration Release.')
        }
        if ($Command -cne 'Gui' -and $Configuration -ceq 'AgentGuiTest') {
            throw [ArgumentException]::new('Configuration AgentGuiTest is accepted only by Gui.')
        }
        $guiMinimumTimeoutSeconds = [int](
            @($registry.guiSettings.scenarios | ForEach-Object { [int]$_.timeoutSeconds }) |
                Measure-Object -Maximum |
                Select-Object -ExpandProperty Maximum) + 30
        if ($Command -ceq 'Gui' -and $parsedTimeoutSeconds -lt $guiMinimumTimeoutSeconds) {
            throw [ArgumentException]::new(
                "Gui TimeoutSeconds must be at least $guiMinimumTimeoutSeconds seconds.")
        }
        $excelMinimumTimeoutSeconds = [int]$registry.excelSettings.timeoutSeconds + 30
        if ($Command -ceq 'Excel' -and $parsedTimeoutSeconds -lt $excelMinimumTimeoutSeconds) {
            throw [ArgumentException]::new(
                "Excel TimeoutSeconds must be at least $excelMinimumTimeoutSeconds seconds.")
        }
        $releaseCandidateMinimumTimeoutSeconds = [Math]::Max(
            $guiMinimumTimeoutSeconds,
            $excelMinimumTimeoutSeconds)
        if ($Command -ceq 'ReleaseCandidate' -and
            $parsedTimeoutSeconds -lt $releaseCandidateMinimumTimeoutSeconds) {
            throw [ArgumentException]::new(
                "ReleaseCandidate TimeoutSeconds must be at least $releaseCandidateMinimumTimeoutSeconds seconds per child.")
        }
        if ($Command -in @('Restore', 'Build', 'Focused', 'Public', 'Provider', 'Package', 'Gui', 'Excel', 'PrivateCase', 'ReleaseCandidate') -and
            -not (Test-Path -LiteralPath (Join-Path $repositoryFull 'src/JET/JET.slnx') -PathType Leaf)) {
            throw [ArgumentException]::new('Repository solution contract is missing.')
        }
        $evidenceFull = Resolve-JetEvidenceRoot -RepositoryRoot $repositoryFull -EvidenceRoot $EvidenceRoot
    }
    catch [ArgumentException] {
        Write-JetEnvelope (New-JetUsageEnvelope -Code 'invalid_argument' -Message $_.Exception.Message)
        return $script:ExitUsage
    }
    catch {
        Write-JetEnvelope ([ordered]@{
            schemaVersion = $script:ReceiptSchemaVersion
            ok = $false
            status = 'infrastructure_error'
            exitCode = $script:ExitInfrastructure
            error = [ordered]@{ code = 'evidence_root_unavailable'; type = $_.Exception.GetType().Name }
            privateData = [ordered]@{ pathInspected = $false }
        })
        return $script:ExitInfrastructure
    }

    $context = $null
    try {
        $context = New-JetRunContext `
            -RepositoryRoot $repositoryFull `
            -EvidenceRoot $evidenceFull `
            -Command $Command
    }
    catch {
        Write-JetEnvelope ([ordered]@{
            schemaVersion = $script:ReceiptSchemaVersion
            ok = $false
            status = 'infrastructure_error'
            exitCode = $script:ExitInfrastructure
            error = [ordered]@{ code = 'run_root_create_failed'; type = $_.Exception.GetType().Name }
            privateData = [ordered]@{ pathInspected = $false }
        })
        return $script:ExitInfrastructure
    }

    $steps = New-Object 'System.Collections.Generic.List[object]'
    $lock = $null
    $lockAcquireEvidence = [ordered]@{ required = [bool]$registry.commands[$Command].requiresExclusiveLock; acquired = $false }
    $lockCleanup = $null
    $cleanup = [ordered]@{ status = 'not_run'; removed = $false; reason = $null }
    $status = 'passed'
    $exitCode = $script:ExitPassed
    $errorEvidence = $null
    $privateDataState = if ($Command -ceq 'PrivateCase') { 'selected' } else { 'not_selected' }
    $privateDataPathInspected = $false
    $privateCaseProvider = $null
    $releaseCandidateWorkspace = $null
    $releaseCandidateWorkspaceCleanup = $null
    $mutationEvidence = $null

    try {
        if ([bool]$registry.commands[$Command].requiresExclusiveLock) {
            $lock = Enter-JetExclusiveLock `
                -RepositoryRoot $repositoryFull `
                -RunId $context.RunId `
                -Command $Command `
                -WaitSeconds $parsedWaitSeconds
            $lockAcquireEvidence.acquired = $lock.Acquired
            if (-not $lock.Acquired) {
                $lockAcquireEvidence.holder = $lock.Holder
                $status = 'blocked'
                $exitCode = $script:ExitBlocked
                $errorEvidence = [ordered]@{ code = 'exclusive_lock_busy' }
            }
        }

        if ($status -ceq 'passed') {
            switch -CaseSensitive ($Command) {
                'Contract' {
                    $probeOutcome = switch -CaseSensitive ($ContractScenario) {
                        'ChildFailure' { 'Failure' }
                        'Timeout' { 'Sleep' }
                        'HoldLock' { 'Sleep' }
                        'OutputLimit' { 'OutputLimit' }
                        'NuGetUnavailable' { 'NuGetUnavailable' }
                        'SensitiveOutput' { 'SensitiveOutput' }
                        default { 'Success' }
                    }
                    $probeTimeout = if ($ContractScenario -ceq 'Timeout') { 1 } else { $parsedTimeoutSeconds }
                    [string[]]$probeSensitiveValues = @()
                    if ($ContractScenario -ceq 'SensitiveOutput') {
                        $probeSensitiveValues = @('JET-HARNESS-CONTRACT-SECRET-20260828')
                    }
                    $pwshPath = [Environment]::ProcessPath
                    $probePath = Join-Path $PSScriptRoot 'contract-probe.ps1'
                    $arguments = @(
                        '-NoProfile',
                        '-File', $probePath,
                        '-Outcome', $probeOutcome,
                        '-Seconds', ([string]$parsedProbeSeconds)
                    )
                    $step = Invoke-JetChildStep `
                        -RepositoryRoot $repositoryFull `
                        -RunDirectory $context.RunDirectory `
                        -Name 'contract-probe' `
                        -FileName $pwshPath `
                        -Arguments $arguments `
                        -DisplayCommand @('pwsh', '-NoProfile', '-File', 'tools/harness/contract-probe.ps1', '-Outcome', $probeOutcome) `
                        -MaximumCapturedBytes ([int]$registry.limits.maximumCapturedBytesPerStream) `
                        -TimeoutSeconds $probeTimeout `
                        -SensitiveValuesToRedact $probeSensitiveValues `
                        -TreatNuGetSourceFailureAsBlocked:($ContractScenario -ceq 'NuGetUnavailable')
                    $steps.Add($step)
                    if ($step.status -ceq 'passed' -and $ContractScenario -cin @('MutationBoundary', 'OwnedProcessTree', 'FrontendPreview', 'FrontendMapping', 'Context')) {
                        $testScript = switch ($ContractScenario) {
                            'MutationBoundary' { 'mutation-contract.tests.ps1' }
                            'OwnedProcessTree' { 'process-tree-contract.tests.ps1' }
                            'FrontendPreview' { 'frontend-preview-contract.tests.ps1' }
                            'FrontendMapping' { 'frontend-mapping-contract.tests.ps1' }
                            'Context' { 'context-contract.tests.ps1' }
                        }
                        $boundedTree = Get-JetMemoryBoundedTreeSettings -Registry $registry
                        $steps.Add((Invoke-JetChildStep `
                            -RepositoryRoot $repositoryFull -RunDirectory $context.RunDirectory `
                            -Name 'contract-specialized' -FileName $pwshPath `
                            -Arguments @('-NoProfile', '-File', (Join-Path $repositoryFull "tools/tests/$testScript"), '-RepositoryRoot', $repositoryFull) `
                            -DisplayCommand @('pwsh', '-NoProfile', '-File', "tools/tests/$testScript") `
                            -MaximumCapturedBytes ([int]$registry.limits.maximumCapturedBytesPerStream) `
                            -TimeoutSeconds $parsedTimeoutSeconds `
                            -EnvironmentVariablesToRemove @($registry.testSettings.environmentVariablesToRemove) `
                            @boundedTree))
                    }
                }
                'Restore' {
                    $dotnet = Get-Command dotnet -CommandType Application -ErrorAction Stop |
                        Select-Object -First 1 -ExpandProperty Source
                    $step = Invoke-JetChildStep `
                        -RepositoryRoot $repositoryFull `
                        -RunDirectory $context.RunDirectory `
                        -Name 'dotnet-restore' `
                        -FileName $dotnet `
                        -Arguments @('restore', (Join-Path $repositoryFull 'src/JET/JET.slnx')) `
                        -DisplayCommand @('dotnet', 'restore', 'src/JET/JET.slnx') `
                        -MaximumCapturedBytes ([int]$registry.limits.maximumCapturedBytesPerStream) `
                        -TimeoutSeconds $parsedTimeoutSeconds `
                        -EnvironmentVariablesToRemove @($registry.testSettings.environmentVariablesToRemove) `
                        -TreatNuGetSourceFailureAsBlocked
                    $steps.Add($step)
                }
                'Documentation' {
                    $step = Invoke-JetDocumentationCheckStep `
                        -RepositoryRoot $repositoryFull `
                        -RunDirectory $context.RunDirectory `
                        -Registry $registry `
                        -TimeoutSeconds $parsedTimeoutSeconds
                    $steps.Add($step)
                }
                'Build' {
                    foreach ($buildStep in @(Invoke-JetBuildPipeline `
                            -RepositoryRoot $repositoryFull `
                            -RunDirectory $context.RunDirectory `
                            -Configuration $Configuration `
                            -SkipRestore ([bool]$NoRestore) `
                            -Registry $registry `
                            -TimeoutSeconds $parsedTimeoutSeconds)) {
                        $steps.Add($buildStep)
                    }
                }
                'Mutation' {
                    Import-Module (Join-Path $PSScriptRoot 'mutation/JetMutation.psm1') -Force
                    $mutation = Invoke-JetMutationPipeline `
                        -RepositoryRoot $repositoryFull -RunDirectory $context.RunDirectory `
                        -Registry $registry -Scope $MutationScope -TimeoutSeconds $parsedTimeoutSeconds `
                        -RunChildStep {
                            param([hashtable] $parameters)
                            Invoke-JetChildStep @parameters -OwnProcessTree
                        }
                    foreach ($mutationStep in $mutation.steps) { $steps.Add($mutationStep) }
                    $mutationEvidence = $mutation.evidence
                }
                'Focused' {
                    foreach ($buildStep in @(Invoke-JetBuildPipeline `
                            -RepositoryRoot $repositoryFull `
                            -RunDirectory $context.RunDirectory `
                            -Configuration $Configuration `
                            -SkipRestore ([bool]$NoRestore) `
                            -Registry $registry `
                            -TimeoutSeconds $parsedTimeoutSeconds)) {
                        $steps.Add($buildStep)
                    }
                    if ($steps.Count -gt 0 -and $steps[$steps.Count - 1].status -ceq 'passed') {
                        $selected = Invoke-JetTestStep `
                            -RepositoryRoot $repositoryFull `
                            -RunDirectory $context.RunDirectory `
                            -Configuration $Configuration `
                            -Name 'focused-selected' `
                            -MinimumExpectedTests 1 `
                            -Registry $registry `
                            -TimeoutSeconds $parsedTimeoutSeconds `
                            -IncludeKind Method `
                            -IncludeValues @("*$Filter*") `
                            -SkipPolicy BlockOnSkip `
                            -Parallelism Collections
                        $steps.Add($selected)
                    }
                    if ($steps.Count -gt 0 -and $steps[$steps.Count - 1].status -ceq 'passed') {
                        $guards = Invoke-JetTestStep `
                            -RepositoryRoot $repositoryFull `
                            -RunDirectory $context.RunDirectory `
                            -Configuration $Configuration `
                            -Name 'focused-guards' `
                            -MinimumExpectedTests 1 `
                            -Registry $registry `
                            -TimeoutSeconds $parsedTimeoutSeconds `
                            -IncludeKind Namespace `
                            -IncludeValues @($registry.testSettings.focusedGuardNamespaces) `
                            -SkipPolicy NoSkips `
                            -Parallelism Collections
                        $steps.Add($guards)
                    }
                }
                'Public' {
                    foreach ($buildStep in @(Invoke-JetBuildPipeline `
                            -RepositoryRoot $repositoryFull `
                            -RunDirectory $context.RunDirectory `
                            -Configuration $Configuration `
                            -SkipRestore ([bool]$NoRestore) `
                            -Registry $registry `
                            -TimeoutSeconds $parsedTimeoutSeconds)) {
                        $steps.Add($buildStep)
                    }
                    if ($steps.Count -gt 0 -and $steps[$steps.Count - 1].status -ceq 'passed') {
                        $public = Invoke-JetTestStep `
                            -RepositoryRoot $repositoryFull `
                            -RunDirectory $context.RunDirectory `
                            -Configuration $Configuration `
                            -Name 'public-tests' `
                            -MinimumExpectedTests ([int]$registry.testSettings.publicMinimumExpectedTests) `
                            -Registry $registry `
                            -TimeoutSeconds $parsedTimeoutSeconds `
                            -SkipPolicy PublicPolicy `
                            -Parallelism Collections
                        $steps.Add($public)
                    }
                    if ($steps.Count -gt 0 -and $steps[$steps.Count - 1].status -ceq 'passed') {
                        foreach ($frontendStep in @(Invoke-JetPublicFrontendSteps `
                                -RepositoryRoot $repositoryFull -RunDirectory $context.RunDirectory `
                                -Registry $registry -TimeoutSeconds $parsedTimeoutSeconds)) {
                            $steps.Add($frontendStep)
                        }
                    }
                }
                'Provider' {
                    $providerPreflight = Get-JetProviderConnectionPreflight -Registry $registry
                    $steps.Add($providerPreflight.Step)
                    if ($steps[$steps.Count - 1].status -ceq 'passed') {
                        foreach ($buildStep in @(Invoke-JetBuildPipeline `
                                -RepositoryRoot $repositoryFull `
                                -RunDirectory $context.RunDirectory `
                                -Configuration $Configuration `
                                -SkipRestore ([bool]$NoRestore) `
                                -Registry $registry `
                                -TimeoutSeconds $parsedTimeoutSeconds)) {
                            $steps.Add($buildStep)
                        }
                    }
                    if ($steps[$steps.Count - 1].status -ceq 'passed') {
                        $localParity = Invoke-JetTestStep `
                            -RepositoryRoot $repositoryFull `
                            -RunDirectory $context.RunDirectory `
                            -Configuration $Configuration `
                            -Name 'provider-local-parity' `
                            -MinimumExpectedTests ([int]$registry.testSettings.providerLocalMinimumExpectedTests) `
                            -Registry $registry `
                            -TimeoutSeconds $parsedTimeoutSeconds `
                            -IncludeKind Method `
                            -IncludeValues @($registry.testSettings.providerLocalParityMethodPatterns) `
                            -EnvironmentVariablesToRemove @($registry.testSettings.environmentVariablesToRemove) `
                            -SkipPolicy NoSkips
                        $steps.Add($localParity)
                    }
                    if ($steps[$steps.Count - 1].status -ceq 'passed') {
                        $sqlProvider = Invoke-JetTestStep `
                            -RepositoryRoot $repositoryFull `
                            -RunDirectory $context.RunDirectory `
                            -Configuration $Configuration `
                            -Name 'provider-sqlserver' `
                            -MinimumExpectedTests ([int]$registry.testSettings.providerSqlMinimumExpectedTests) `
                            -Registry $registry `
                            -TimeoutSeconds $parsedTimeoutSeconds `
                            -IncludeKind Trait `
                            -IncludeValues @($registry.testSettings.providerSqlTraits) `
                            -ExcludedProfiles @('Scale') `
                            -EnvironmentVariablesToRemove @($registry.testSettings.providerEnvironmentVariablesToRemove) `
                            -SensitiveValuesToRedact @($providerPreflight.SensitiveValues) `
                            -SkipPolicy ProviderRequired
                        $steps.Add($sqlProvider)
                    }
                    if ($steps[$steps.Count - 1].status -ceq 'passed') {
                        $localDb = Invoke-JetTestStep `
                            -RepositoryRoot $repositoryFull `
                            -RunDirectory $context.RunDirectory `
                            -Configuration $Configuration `
                            -Name 'provider-localdb-express-guard' `
                            -MinimumExpectedTests ([int]$registry.testSettings.providerLocalDbMinimumExpectedTests) `
                            -Registry $registry `
                            -TimeoutSeconds $parsedTimeoutSeconds `
                            -IncludeKind Trait `
                            -IncludeValues @($registry.testSettings.providerLocalDbTraits) `
                            -ExcludedProfiles @('Scale') `
                            -EnvironmentVariablesToRemove @($registry.testSettings.environmentVariablesToRemove) `
                            -SkipPolicy ProviderPolicy
                        $steps.Add($localDb)
                    }
                }
                'PrivateCase' {
                    $privatePreflight = Get-JetPrivateCasePreflight -Registry $registry
                    $privateDataPathInspected = [bool]$privatePreflight.PathInspected
                    $privateCaseProvider = $privatePreflight.Provider
                    $steps.Add($privatePreflight.Step)
                    if ($steps[$steps.Count - 1].status -ceq 'passed') {
                        foreach ($buildStep in @(Invoke-JetBuildPipeline `
                                -RepositoryRoot $repositoryFull `
                                -RunDirectory $context.RunDirectory `
                                -Configuration $Configuration `
                                -SkipRestore ([bool]$NoRestore) `
                                -Registry $registry `
                                -TimeoutSeconds $parsedTimeoutSeconds)) {
                            $steps.Add($buildStep)
                        }
                    }
                    if ($steps[$steps.Count - 1].status -ceq 'passed') {
                        $privateTempRoot = Join-Path $context.ScratchDirectory 'private-case-temp'
                        Assert-JetNoExistingReparsePoint `
                            -RepositoryRoot $repositoryFull `
                            -Candidate $privateTempRoot
                        [void][IO.Directory]::CreateDirectory($privateTempRoot)
                        Assert-JetNoExistingReparsePoint `
                            -RepositoryRoot $repositoryFull `
                            -Candidate $privateTempRoot
                        $privatePreflight.EnvironmentVariablesToSet['TEMP'] = $privateTempRoot
                        $privatePreflight.EnvironmentVariablesToSet['TMP'] = $privateTempRoot
                        $privateSensitiveValues = @($privatePreflight.SensitiveValues) + @($privateTempRoot)
                        $privateCase = Invoke-JetTestStep `
                            -RepositoryRoot $repositoryFull `
                            -RunDirectory $context.RunDirectory `
                            -Configuration $Configuration `
                            -Name 'private-case-tests' `
                            -MinimumExpectedTests ([int]$registry.testSettings.privateCaseMinimumExpectedTests) `
                            -Registry $registry `
                            -TimeoutSeconds $parsedTimeoutSeconds `
                            -IncludeKind Trait `
                            -IncludeValues @('TestProfile=PrivateCase') `
                            -ExcludedProfiles @('Provider', 'Scale') `
                            -EnvironmentVariablesToRemove @($registry.testSettings.environmentVariablesToRemove) `
                            -EnvironmentVariablesToSet $privatePreflight.EnvironmentVariablesToSet `
                            -SensitiveValuesToRedact $privateSensitiveValues `
                            -SkipPolicy NoSkips `
                            -WithholdFailureMessages
                        $steps.Add($privateCase)
                    }
                }
                'ReleaseCandidate' {
                    $snapshot = New-JetReleaseCandidateSnapshot `
                        -RepositoryRoot $repositoryFull `
                        -RunId $context.RunId `
                        -Registry $registry
                    $releaseCandidateWorkspace = $snapshot.WorkspaceRoot
                    $steps.Add($snapshot.Step)
                    if ($steps[$steps.Count - 1].status -ceq 'passed') {
                        foreach ($definition in @($registry.releaseCandidateSettings.steps)) {
                            $child = Invoke-JetReleaseCandidateChildStep `
                                -RepositoryRoot $repositoryFull `
                                -SnapshotRoot $snapshot.SnapshotRoot `
                                -RunDirectory $context.RunDirectory `
                                -Definition $definition `
                                -Registry $registry `
                                -TimeoutSeconds $parsedTimeoutSeconds
                            $steps.Add($child)
                            if ($child.status -cne 'passed') {
                                break
                            }
                        }
                    }
                }
                'Package' {
                    $sourcePreflight = Invoke-JetPackageVerifierStep `
                        -RepositoryRoot $repositoryFull `
                        -RunDirectory $context.RunDirectory `
                        -ScratchDirectory $context.ScratchDirectory `
                        -Mode SourcePreflight `
                        -Registry $registry `
                        -TimeoutSeconds $parsedTimeoutSeconds
                    $steps.Add($sourcePreflight)
                    if ($steps[$steps.Count - 1].status -ceq 'passed') {
                        foreach ($buildStep in @(Invoke-JetBuildPipeline `
                                -RepositoryRoot $repositoryFull `
                                -RunDirectory $context.RunDirectory `
                                -Configuration 'Release' `
                                -SkipRestore ([bool]$NoRestore) `
                                -Registry $registry `
                                -TimeoutSeconds $parsedTimeoutSeconds)) {
                            $steps.Add($buildStep)
                        }
                    }
                    if ($steps[$steps.Count - 1].status -ceq 'passed') {
                        $packageTests = Invoke-JetTestStep `
                            -RepositoryRoot $repositoryFull `
                            -RunDirectory $context.RunDirectory `
                            -Configuration 'Release' `
                            -Name 'package-tests' `
                            -MinimumExpectedTests ([int]$registry.testSettings.packageMinimumExpectedTests) `
                            -Registry $registry `
                            -TimeoutSeconds $parsedTimeoutSeconds `
                            -IncludeKind Method `
                            -IncludeValues @($registry.testSettings.packageMethodPatterns) `
                            -SkipPolicy NoSkips `
                            -Parallelism Collections
                        $steps.Add($packageTests)
                    }
                    $publishDirectory = Join-Path $context.ScratchDirectory 'publish'
                    if ($steps[$steps.Count - 1].status -ceq 'passed') {
                        Assert-JetNoExistingReparsePoint -RepositoryRoot $repositoryFull -Candidate $publishDirectory
                        $dotnet = Get-Command dotnet -CommandType Application -ErrorAction Stop |
                            Select-Object -First 1 -ExpandProperty Source
                        $boundedTree = Get-JetMemoryBoundedTreeSettings -Registry $registry
                        $publish = Invoke-JetChildStep `
                            -RepositoryRoot $repositoryFull `
                            -RunDirectory $context.RunDirectory `
                            -Name 'dotnet-publish' `
                            -FileName $dotnet `
                            -Arguments @(
                                'publish', (Join-Path $repositoryFull ([string]$registry.packageSettings.project)),
                                '--configuration', 'Release',
                                '--runtime', ([string]$registry.packageSettings.runtimeIdentifier),
                                '--no-restore', '--disable-build-servers',
                                '--output', $publishDirectory,
                                "/p:PublishProfile=$([string]$registry.packageSettings.publishProfile)"
                            ) `
                            -DisplayCommand @(
                                'dotnet', 'publish', ([string]$registry.packageSettings.project),
                                '--configuration', 'Release',
                                '--runtime', ([string]$registry.packageSettings.runtimeIdentifier),
                                '--no-restore', '--disable-build-servers',
                                '--output', (Get-JetRelativePath -RepositoryRoot $repositoryFull -Path $publishDirectory),
                                "/p:PublishProfile=$([string]$registry.packageSettings.publishProfile)"
                            ) `
                            -MaximumCapturedBytes ([int]$registry.limits.maximumCapturedBytesPerStream) `
                            -TimeoutSeconds $parsedTimeoutSeconds `
                            -EnvironmentVariablesToRemove @($registry.testSettings.environmentVariablesToRemove) `
                            -TreatNuGetSourceFailureAsBlocked `
                            @boundedTree
                        $steps.Add($publish)
                    }
                    if ($steps[$steps.Count - 1].status -ceq 'passed') {
                        $packageVerification = Invoke-JetPackageVerifierStep `
                            -RepositoryRoot $repositoryFull `
                            -RunDirectory $context.RunDirectory `
                            -ScratchDirectory $context.ScratchDirectory `
                            -Mode Verify `
                            -Registry $registry `
                            -TimeoutSeconds $parsedTimeoutSeconds `
                            -CandidatePath $publishDirectory
                        $steps.Add($packageVerification)
                    }
                }
                'Gui' {
                    foreach ($buildStep in @(Invoke-JetGuiBuildPipeline `
                            -RepositoryRoot $repositoryFull `
                            -RunDirectory $context.RunDirectory `
                            -Registry $registry `
                            -TimeoutSeconds $parsedTimeoutSeconds)) {
                        $steps.Add($buildStep)
                    }
                    $selectedGuiScenarios = @($registry.guiSettings.scenarios | Where-Object {
                        [string]::IsNullOrWhiteSpace($GuiScenario) -or [string]$_.name -ceq $GuiScenario })
                    # 迴圈變數不能叫 $guiScenario：PowerShell 變數不分大小寫，會撞到 [string] 參數 $GuiScenario 而被轉成字串。
                    foreach ($guiScenarioEntry in $selectedGuiScenarios) {
                        if ($steps.Count -gt 0 -and $steps[$steps.Count - 1].status -cne 'passed') {
                            break
                        }
                        $gui = Invoke-JetGuiScenarioStep `
                            -RepositoryRoot $repositoryFull `
                            -RunDirectory $context.RunDirectory `
                            -Registry $registry `
                            -Scenario $guiScenarioEntry `
                            -TimeoutSeconds ([int]$guiScenarioEntry.timeoutSeconds + 30)
                        $steps.Add($gui)
                    }
                }
                'Excel' {
                    foreach ($buildStep in @(Invoke-JetExcelBuildPipeline `
                            -RepositoryRoot $repositoryFull `
                            -RunDirectory $context.RunDirectory `
                            -Registry $registry `
                            -TimeoutSeconds $parsedTimeoutSeconds)) {
                        $steps.Add($buildStep)
                    }
                    if ($steps.Count -eq 0 -or $steps[$steps.Count - 1].status -ceq 'passed') {
                        $fixture = Invoke-JetExcelFixtureStep `
                            -RepositoryRoot $repositoryFull `
                            -RunDirectory $context.RunDirectory `
                            -ScratchDirectory $context.ScratchDirectory `
                            -Registry $registry `
                            -TimeoutSeconds $parsedTimeoutSeconds
                        $steps.Add($fixture)
                    }
                    foreach ($reportKind in @($registry.excelSettings.reportKinds)) {
                        if ($steps.Count -gt 0 -and $steps[$steps.Count - 1].status -cne 'passed') {
                            break
                        }
                        $excel = Invoke-JetExcelScenarioStep `
                            -RepositoryRoot $repositoryFull `
                            -RunDirectory $context.RunDirectory `
                            -ScratchDirectory $context.ScratchDirectory `
                            -RunId $context.RunId `
                            -Registry $registry `
                            -ReportKind ([string]$reportKind) `
                            -TimeoutSeconds ([int]$registry.excelSettings.timeoutSeconds + 30)
                        $steps.Add($excel)
                    }
                }
            }

            $firstNonPassing = @($steps | Where-Object { $_.status -cne 'passed' } | Select-Object -First 1)
            if ($firstNonPassing.Count -gt 0) {
                $reason = [string]$firstNonPassing[0].classification.reason
                switch -CaseSensitive ([string]$firstNonPassing[0].status) {
                    'blocked' {
                        $status = 'blocked'
                        $exitCode = $script:ExitBlocked
                        $errorEvidence = [ordered]@{
                            code = if ([string]::IsNullOrWhiteSpace($reason)) { 'child_blocked' } else { $reason }
                        }
                    }
                    'infrastructure_error' {
                        $status = 'infrastructure_error'
                        $exitCode = $script:ExitInfrastructure
                        $errorEvidence = [ordered]@{
                            code = if ([string]::IsNullOrWhiteSpace($reason)) { 'child_infrastructure_error' } else { $reason }
                        }
                    }
                    default {
                        $status = 'failed'
                        $exitCode = $script:ExitFailed
                        $errorEvidence = [ordered]@{
                            code = if ([string]::IsNullOrWhiteSpace($reason)) { 'child_failed' } else { $reason }
                        }
                    }
                }
            }
        }
    }
    catch {
        $status = 'infrastructure_error'
        $exitCode = $script:ExitInfrastructure
        # 訊息只留給診斷：去掉本機絕對路徑，附上模組內的行號，讓接手的人不用重跑就知道炸在哪裡。
        $safeMessage = [string]$_.Exception.Message
        foreach ($localRoot in @($repositoryFull, [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile))) {
            if (-not [string]::IsNullOrWhiteSpace($localRoot)) {
                $safeMessage = $safeMessage.Replace($localRoot, '<local>', [StringComparison]::OrdinalIgnoreCase)
            }
        }
        $errorEvidence = [ordered]@{
            code = 'runner_exception'
            type = $_.Exception.GetType().Name
            message = $safeMessage
            line = [int]$_.InvocationInfo.ScriptLineNumber
        }
    }
    finally {
        if ($Command -ceq 'ReleaseCandidate' -and
            -not [string]::IsNullOrWhiteSpace([string]$releaseCandidateWorkspace)) {
            $releaseCandidateWorkspaceCleanup = Remove-JetReleaseCandidateWorkspace `
                -RepositoryRoot $repositoryFull `
                -WorkspaceRoot $releaseCandidateWorkspace `
                -RunId $context.RunId `
                -Registry $registry
            $snapshotStep = @($steps | Where-Object { $_.name -ceq 'release-candidate-snapshot' }) |
                Select-Object -First 1
            if ($null -ne $snapshotStep) {
                $snapshotStep.evidence.snapshotRetained =
                    $releaseCandidateWorkspaceCleanup.status -cne 'passed'
            }
            $workspaceCleanupStatus = if ($releaseCandidateWorkspaceCleanup.status -ceq 'passed') {
                'passed'
            }
            else {
                'infrastructure_error'
            }
            $workspaceCleanupReason = if ($releaseCandidateWorkspaceCleanup.status -ceq 'passed') {
                $null
            }
            else {
                [string]$releaseCandidateWorkspaceCleanup.reason
            }
            $workspaceCleanupStep = New-JetSyntheticStep `
                -Name 'release-candidate-snapshot-cleanup' `
                -Status $workspaceCleanupStatus `
                -Reason $workspaceCleanupReason `
                -Evidence ([ordered]@{
                    removed = [bool]$releaseCandidateWorkspaceCleanup.removed
                    pathRecorded = $false
                })
            $steps.Add($workspaceCleanupStep)
            if ($releaseCandidateWorkspaceCleanup.status -cne 'passed') {
                $status = 'infrastructure_error'
                $exitCode = $script:ExitInfrastructure
                $errorEvidence = [ordered]@{
                    code = 'release_candidate_snapshot_cleanup_failed'
                    reason = $releaseCandidateWorkspaceCleanup.reason
                }
            }
        }
        if ($ContractScenario -ceq 'ReadOnlyCleanup' -and
            (Test-Path -LiteralPath $context.ScratchDirectory -PathType Container)) {
            $readOnlyPath = Join-Path $context.ScratchDirectory 'read-only-cleanup.txt'
            [IO.File]::WriteAllText($readOnlyPath, 'owned read-only cleanup probe', $script:Utf8)
            [IO.File]::SetAttributes($readOnlyPath, [IO.FileAttributes]::ReadOnly)
        }
        if ($ContractScenario -ceq 'CleanupFailure' -and
            (Test-Path -LiteralPath $context.ScratchDirectory -PathType Container)) {
            $wrongOwner = [ordered]@{
                schemaVersion = 1
                kind = 'jet-harness-scratch'
                runId = 'intentional-contract-mismatch'
                processId = $PID
            }
            Write-JetAtomicJson `
                -Path (Join-Path $context.ScratchDirectory $script:OwnerMarkerName) `
                -Value $wrongOwner
        }

        $cleanup = Remove-JetOwnedDirectory `
            -RepositoryRoot $repositoryFull `
            -RunDirectory $context.RunDirectory `
            -OwnedDirectory $context.ScratchDirectory `
            -RunId $context.RunId
        if ($cleanup.status -cne 'passed') {
            $status = 'infrastructure_error'
            $exitCode = $script:ExitInfrastructure
            $errorEvidence = [ordered]@{ code = 'owned_cleanup_failed'; reason = $cleanup.reason }
        }

        if ($null -ne $lock -and $lock.Acquired) {
            # 拿著共享鎖時才整理改壞檢查的舊執行，這時不會有進行中的改壞檢查；整理失敗不影響這次的結果。
            try { $null = Invoke-JetMutationRetention -MutationRoot (Join-Path $repositoryFull 'artifacts/harness/mutation') } catch { }
            $lockCleanup = Exit-JetExclusiveLock -Lock $lock -RunId $context.RunId
            if ($lockCleanup.status -cne 'passed') {
                $status = 'infrastructure_error'
                $exitCode = $script:ExitInfrastructure
                $errorEvidence = [ordered]@{ code = 'lock_cleanup_failed'; reason = $lockCleanup.reason }
            }
        }
    }

    $stepArray = [object[]]::new($steps.Count)
    $steps.CopyTo($stepArray, 0)
    $releaseCandidateIndexMeasured = $false
    $releaseCandidateIndexMutated = $null
    if ($Command -ceq 'ReleaseCandidate') {
        $snapshotSteps = @($stepArray | Where-Object { $_.name -ceq 'release-candidate-snapshot' })
        if ($snapshotSteps.Count -gt 0) {
            $snapshotEvidence = $snapshotSteps[$snapshotSteps.Count - 1].evidence
            $releaseCandidateIndexMeasured = [bool]$snapshotEvidence.sourceIndexMeasured
            if ($releaseCandidateIndexMeasured) {
                $releaseCandidateIndexMutated = [bool]$snapshotEvidence.sourceIndexMutated
            }
        }
    }
    $completedUtc = [DateTime]::UtcNow
    $receipt = [ordered]@{
        schemaVersion = $script:ReceiptSchemaVersion
        ok = $exitCode -eq $script:ExitPassed
        status = $status
        exitCode = $exitCode
        runId = $context.RunId
        command = $Command
        lane = [string]$registry.commands[$Command].lane
        configuration = $Configuration
        filter = if ($Command -ceq 'Focused') { $Filter } else { $null }
        mutation = $mutationEvidence
        contractScenario = if ($Command -ceq 'Contract') { $ContractScenario } else { $null }
        guiScenario = if ($Command -ceq 'Gui' -and -not [string]::IsNullOrWhiteSpace($GuiScenario)) { $GuiScenario } else { $null }
        partial = ($Command -ceq 'Gui' -and -not [string]::IsNullOrWhiteSpace($GuiScenario))
        startedUtc = $context.StartedUtc.ToString('O')
        completedUtc = $completedUtc.ToString('O')
        durationSeconds = [Math]::Round(($completedUtc - $context.StartedUtc).TotalSeconds, 3)
        repository = Get-JetRepositoryIdentity -RepositoryRoot $repositoryFull
        runner = Get-JetRunnerIdentity -RepositoryRoot $repositoryFull -Registry $registry
        runDirectory = Get-JetRelativePath -RepositoryRoot $repositoryFull -Path $context.RunDirectory
        steps = $stepArray
        testBoundary = switch -CaseSensitive ($Command) {
            { $_ -in @('Focused', 'Public') } {
                [ordered]@{
                    excludedProfiles = @($registry.testSettings.excludedProfiles)
                    protectedWorkspaceTests = 'excluded'
                    sanitizedEnvironmentVariables = @($registry.testSettings.environmentVariablesToRemove)
                    assertionDrift = Get-JetTestAssertionDrift -RepositoryRoot $repositoryFull
                }
            }
            'Provider' {
                [ordered]@{
                    sqlServerCapabilityRequired = $true
                    connectionVariable = [string]$registry.testSettings.providerConnectionEnvironmentVariable
                    connectionValueRecorded = $false
                    protectedWorkspaceTests = 'excluded'
                    sqlStepSanitizedEnvironmentVariables = @($registry.testSettings.providerEnvironmentVariablesToRemove)
                }
            }
            'PrivateCase' {
                [ordered]@{
                    provider = $privateCaseProvider
                    providerMustBeExplicit = $true
                    sourcePathsRecorded = $false
                    expectedReports = 6
                    reportComparison = 'content-and-appearance'
                    infAcceptance = 'rules-and-effective-population'
                    cleanupPolicy = 'always-delete'
                    outputsRetained = $false
                    sanitizedEnvironmentVariables = @($registry.testSettings.environmentVariablesToRemove)
                }
            }
            'ReleaseCandidate' {
                [ordered]@{
                    configuration = 'Release'
                    candidateInventory = 'git'
                    isolatedCandidateSnapshot = $true
                    sourceGitIndexMeasured = $releaseCandidateIndexMeasured
                    sourceGitIndexMutated = $releaseCandidateIndexMutated
                    failFast = $true
                    childCommands = @($registry.releaseCandidateSettings.steps | ForEach-Object { [string]$_.command })
                    sqlServerCapabilityRequired = $false
                    liveProviderIncluded = [bool]$registry.releaseCandidateSettings.liveProviderIncluded
                    sqlServerCompatibilityCommand = 'Provider'
                    connectionValueRecorded = $false
                    privateCaseIncluded = [bool]$registry.releaseCandidateSettings.privateCaseIncluded
                    privateRootsInspected = $false
                    sanitizedEnvironmentVariables = @($registry.testSettings.environmentVariablesToRemove)
                }
            }
            'Package' {
                [ordered]@{
                    configuration = 'Release'
                    trackedCredentialsAllowed = $false
                    protectedWorkspaceTests = 'excluded'
                    sanitizedEnvironmentVariables = @($registry.testSettings.environmentVariablesToRemove)
                }
            }
            'Gui' {
                [ordered]@{
                    configuration = [string]$registry.guiSettings.configuration
                    scenarios = @($registry.guiSettings.scenarios | ForEach-Object { [string]$_.name })
                    protocol = 'WebView2-CDP'
                    loopbackOnly = $true
                    arbitraryScriptAccepted = $false
                    privateRootsInspected = $false
                    sanitizedEnvironmentVariables = @($registry.testSettings.environmentVariablesToRemove)
                }
            }
            'Excel' {
                [ordered]@{
                    configuration = [string]$registry.excelSettings.configuration
                    scenario = [string]$registry.excelSettings.scenario
                    reportKinds = @($registry.excelSettings.reportKinds | ForEach-Object { [string]$_ })
                    fixture = 'same_run_synthetic_six_report_journey'
                    fixtureEnvironmentVariable = [string]$registry.excelSettings.fixtureEnvironmentVariable
                    privateRootsInspected = $false
                    arbitraryWorkbookPathAccepted = $false
                    sanitizedEnvironmentVariables = @($registry.testSettings.environmentVariablesToRemove)
                }
            }
            default {
                $null
            }
        }
        firstRed = Get-JetFirstRed -Steps $stepArray -Cleanup $cleanup -LockEvidence $lockCleanup
        lock = [ordered]@{
            required = $lockAcquireEvidence.required
            acquired = $lockAcquireEvidence.acquired
            holder = if ($lockAcquireEvidence.Contains('holder')) { $lockAcquireEvidence.holder } else { $null }
            cleanup = $lockCleanup
        }
        cleanup = $cleanup
        candidateSnapshotCleanup = if ($Command -ceq 'ReleaseCandidate') {
            $releaseCandidateWorkspaceCleanup
        }
        else {
            $null
        }
        privateData = [ordered]@{
            state = $privateDataState
            pathInspected = $privateDataPathInspected
            pathsRecorded = $false
            outputsRetained = if ($Command -ceq 'PrivateCase') { $false } else { $null }
        }
        error = $errorEvidence
    }

    if ($ContractScenario -ceq 'ReceiptFailure') {
        [void][IO.Directory]::CreateDirectory($context.ReceiptPath)
    }

    try {
        Write-JetAtomicJson -Path $context.ReceiptPath -Value $receipt
        # 收據寫好之後才整理舊執行；整理失敗不影響這次的結果。
        try { $null = Invoke-JetReceiptRetention -EvidenceRoot $evidenceFull } catch { }
        Write-JetEnvelope ([ordered]@{
            schemaVersion = $script:ReceiptSchemaVersion
            ok = $receipt.ok
            status = $receipt.status
            exitCode = $receipt.exitCode
            runId = $context.RunId
            receipt = Get-JetRelativePath -RepositoryRoot $repositoryFull -Path $context.ReceiptPath
            firstRed = $receipt.firstRed
            privateData = $receipt.privateData
        })
        return $exitCode
    }
    catch {
        Write-JetEnvelope ([ordered]@{
            schemaVersion = $script:ReceiptSchemaVersion
            ok = $false
            status = 'infrastructure_error'
            exitCode = $script:ExitInfrastructure
            runId = $context.RunId
            receipt = $null
            error = [ordered]@{
                code = 'receipt_write_failed'
                type = $_.Exception.GetType().Name
            }
            privateData = [ordered]@{
                state = $privateDataState
                pathInspected = $privateDataPathInspected
            }
        })
        return $script:ExitInfrastructure
    }
}

Export-ModuleMember -Function Invoke-JetHarness
