using namespace System.IO

# 由 JetHarness.psm1 點號載入。各路線專用的步驟：文件檢查、封裝檢查、Gui 情境，以及 Excel 的測試檔與情境。

function Invoke-JetDocumentationCheckStep {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $RunDirectory,
        [Parameter(Mandatory = $true)] $Registry,
        [Parameter(Mandatory = $true)] [int] $TimeoutSeconds
    )

    $pwshPath = [Environment]::ProcessPath
    $verifierRelative = [string]$Registry.documentationSettings.verifier
    $policyRelative = [string]$Registry.documentationSettings.policy
    $verifierPath = Join-Path $RepositoryRoot $verifierRelative
    $policyPath = Join-Path $RepositoryRoot $policyRelative
    $reportPath = Join-Path $RunDirectory 'documentation-report.json'
    Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $verifierPath
    Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $policyPath
    Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $reportPath

    $step = Invoke-JetChildStep `
        -RepositoryRoot $RepositoryRoot `
        -RunDirectory $RunDirectory `
        -Name 'documentation-check' `
        -FileName $pwshPath `
        -Arguments @(
            '-NoProfile',
            '-File', $verifierPath,
            '-RepositoryRoot', $RepositoryRoot,
            '-ReportPath', $reportPath,
            '-PolicyPath', $policyPath
        ) `
        -DisplayCommand @(
            'pwsh', '-NoProfile', '-File', $verifierRelative,
            '-RepositoryRoot', '.',
            '-ReportPath', (Get-JetRelativePath -RepositoryRoot $RepositoryRoot -Path $reportPath),
            '-PolicyPath', $policyRelative
        ) `
        -MaximumCapturedBytes ([int]$Registry.limits.maximumCapturedBytesPerStream) `
        -TimeoutSeconds $TimeoutSeconds `
        -EnvironmentVariablesToRemove @($Registry.testSettings.environmentVariablesToRemove)

    try {
        if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
            throw [FileNotFoundException]::new('The documentation report is missing.')
        }
        $report = Get-Content -LiteralPath $reportPath -Raw -Encoding utf8 |
            ConvertFrom-Json -AsHashtable -Depth 20
        if ([int]$report.schemaVersion -ne 2 -or -not $report.ContainsKey('status') -or
            [bool]$report.privateData.pathInspected -or -not [bool]$report.manualReview.required) {
            throw [InvalidDataException]::new('The documentation report is invalid.')
        }

        $errorCodes = @($report.errors | ForEach-Object { [string]$_.code })
        $warningCodes = @($report.warnings | ForEach-Object { [string]$_.code })
        $step['documentation'] = [ordered]@{
            report = Get-JetRelativePath -RepositoryRoot $RepositoryRoot -Path $reportPath
            status = [string]$report.status
            counters = $report.counters
            errorCodes = $errorCodes
            warningCodes = $warningCodes
            manualReviewRequired = $true
            manualReviewItems = @($report.manualReview.items).Count
        }
        $firstError = if ($errorCodes.Count -gt 0) { [string]$errorCodes[0] } else { $null }
        switch -CaseSensitive ([string]$report.status) {
            'passed' {
                if (-not $step.process.hasExitCode -or $step.process.exitCode -ne 0) {
                    throw [InvalidDataException]::new('The documentation checker exit code disagrees with its report.')
                }
                $step.status = 'passed'
                $step.classification.reason = $null
            }
            'failed' {
                $step.status = 'failed'
                $step.classification.reason = if ([string]::IsNullOrWhiteSpace($firstError)) {
                    'documentation_check_failed'
                }
                else {
                    $firstError
                }
            }
            'infrastructure_error' {
                $step.status = 'infrastructure_error'
                $step.classification.reason = 'documentation_checker_invalid'
            }
            default {
                throw [InvalidDataException]::new('The documentation checker returned an unknown status.')
            }
        }
    }
    catch {
        $step['documentation'] = [ordered]@{ reportStatus = 'invalid'; errorType = $_.Exception.GetType().Name }
        $step.status = 'infrastructure_error'
        $step.classification.reason = 'documentation_report_invalid'
    }
    return $step
}

function Invoke-JetPackageVerifierStep {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $RunDirectory,
        [Parameter(Mandatory = $true)] [string] $ScratchDirectory,
        [Parameter(Mandatory = $true)] [ValidateSet('SourcePreflight', 'Verify')] [string] $Mode,
        [Parameter(Mandatory = $true)] $Registry,
        [Parameter(Mandatory = $true)] [int] $TimeoutSeconds,
        [string] $CandidatePath = ''
    )

    $pwshPath = [Environment]::ProcessPath
    $verifierRelative = [string]$Registry.packageSettings.verifier
    $verifierPath = Join-Path $RepositoryRoot $verifierRelative
    $sourceRoot = Join-Path $RepositoryRoot ([string]$Registry.packageSettings.sourceRoot)
    Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $verifierPath
    Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $sourceRoot
    $manifestName = if ($Mode -ceq 'SourcePreflight') {
        'package-source-preflight.json'
    }
    else {
        'package-manifest.json'
    }
    $manifestPath = Join-Path $RunDirectory $manifestName
    $arguments = New-Object 'System.Collections.Generic.List[string]'
    foreach ($argument in @(
            '-NoProfile',
            '-File', $verifierPath,
            '-Mode', $Mode,
            '-SourceRoot', $sourceRoot,
            '-ManifestPath', $manifestPath)) {
        $arguments.Add([string]$argument)
    }
    $displayCommand = @(
        'pwsh', '-NoProfile', '-File', $verifierRelative,
        '-Mode', $Mode,
        '-SourceRoot', [string]$Registry.packageSettings.sourceRoot,
        '-ManifestPath', (Get-JetRelativePath -RepositoryRoot $RepositoryRoot -Path $manifestPath)
    )
    if ($Mode -ceq 'Verify') {
        foreach ($argument in @(
                '-CandidatePath', $CandidatePath,
                '-OwnedRoot', $ScratchDirectory,
                '-RegistryPath', (Join-Path $PSScriptRoot 'lanes.json'))) {
            $arguments.Add([string]$argument)
        }
        $displayCommand += @(
            '-CandidatePath', (Get-JetRelativePath -RepositoryRoot $RepositoryRoot -Path $CandidatePath),
            '-OwnedRoot', (Get-JetRelativePath -RepositoryRoot $RepositoryRoot -Path $ScratchDirectory),
            '-RegistryPath', 'tools/harness/lanes.json'
        )
    }

    $name = if ($Mode -ceq 'SourcePreflight') { 'package-source-preflight' } else { 'package-verification' }
    $step = Invoke-JetChildStep `
        -RepositoryRoot $RepositoryRoot `
        -RunDirectory $RunDirectory `
        -Name $name `
        -FileName $pwshPath `
        -Arguments $arguments.ToArray() `
        -DisplayCommand $displayCommand `
        -MaximumCapturedBytes ([int]$Registry.limits.maximumCapturedBytesPerStream) `
        -TimeoutSeconds $TimeoutSeconds `
        -EnvironmentVariablesToRemove @($Registry.testSettings.environmentVariablesToRemove)

    try {
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            throw [FileNotFoundException]::new('Package verifier manifest is missing.')
        }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 |
            ConvertFrom-Json -AsHashtable -Depth 20
        if ([int]$manifest.schemaVersion -ne 1 -or -not $manifest.ContainsKey('status') -or
            [bool]$manifest.privateData.pathInspected) {
            throw [InvalidDataException]::new('Package verifier manifest is invalid.')
        }
        $errorCodes = @($manifest.errors | ForEach-Object { [string]$_.code })
        $step['package'] = [ordered]@{
            manifest = Get-JetRelativePath -RepositoryRoot $RepositoryRoot -Path $manifestPath
            status = [string]$manifest.status
            errorCodes = $errorCodes
            counters = if ($manifest.ContainsKey('counters')) { $manifest.counters } else { $null }
            inventorySha256 = if ($manifest.ContainsKey('inventorySha256')) { $manifest.inventorySha256 } else { $null }
            executableSha256 = if ($manifest.ContainsKey('executableSha256')) { $manifest.executableSha256 } else { $null }
        }
        $firstError = if ($errorCodes.Count -gt 0) { [string]$errorCodes[0] } else { $null }
        switch -CaseSensitive ([string]$manifest.status) {
            'passed' {
                if (-not $step.process.hasExitCode -or $step.process.exitCode -ne 0) {
                    throw [InvalidDataException]::new('Package verifier exit code disagrees with its manifest.')
                }
                $step.status = 'passed'
                $step.classification.reason = $null
            }
            'failed' {
                $step.status = 'failed'
                $step.classification.reason = if ([string]::IsNullOrWhiteSpace($firstError)) {
                    'package_verification_failed'
                }
                else {
                    $firstError
                }
            }
            'infrastructure_error' {
                $step.status = 'infrastructure_error'
                $step.classification.reason = if ([string]::IsNullOrWhiteSpace($firstError)) {
                    'package_verifier_infrastructure_error'
                }
                else {
                    $firstError
                }
            }
            default {
                throw [InvalidDataException]::new('Package verifier returned an unknown status.')
            }
        }
    }
    catch {
        $step['package'] = [ordered]@{ reportStatus = 'invalid'; errorType = $_.Exception.GetType().Name }
        $step.status = 'infrastructure_error'
        $step.classification.reason = 'package_verifier_report_invalid'
    }
    return $step
}

function Invoke-JetGuiScenarioStep {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $RunDirectory,
        [Parameter(Mandatory = $true)] $Registry,
        [Parameter(Mandatory = $true)] $Scenario,
        [Parameter(Mandatory = $true)] [int] $TimeoutSeconds
    )

    $dotnet = Get-Command dotnet -CommandType Application -ErrorAction Stop |
        Select-Object -First 1 -ExpandProperty Source
    $scenarioName = [string]$Scenario.name
    $applicationPath = Join-Path $RepositoryRoot ([string]$Registry.guiSettings.applicationExecutable)
    $driverPath = Join-Path $RepositoryRoot ([string]$Registry.guiSettings.driverAssembly)
    $manifestPath = Join-Path $RunDirectory "gui-$scenarioName-manifest.json"
    Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $applicationPath
    Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $driverPath
    if (-not (Test-Path -LiteralPath $applicationPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $driverPath -PathType Leaf)) {
        throw [FileNotFoundException]::new('GUI build output is missing.')
    }

    $driverTimeout = [int]$Scenario.timeoutSeconds
    $actionBudget = [string][int]$Scenario.actionBudget
    $screenshotBudget = [string][int]$Scenario.screenshotBudget
    $step = Invoke-JetChildStep `
        -RepositoryRoot $RepositoryRoot `
        -RunDirectory $RunDirectory `
        -Name "gui-$scenarioName" `
        -FileName $dotnet `
        -Arguments @(
            $driverPath,
            '--app', $applicationPath,
            '--manifest', $manifestPath,
            '--scenario', $scenarioName,
            '--timeout-seconds', ([string]$driverTimeout),
            '--action-budget', $actionBudget,
            '--screenshot-budget', $screenshotBudget
        ) `
        -DisplayCommand @(
            'dotnet', ([string]$Registry.guiSettings.driverAssembly),
            '--app', ([string]$Registry.guiSettings.applicationExecutable),
            '--manifest', (Get-JetRelativePath -RepositoryRoot $RepositoryRoot -Path $manifestPath),
            '--scenario', $scenarioName,
            '--timeout-seconds', ([string]$driverTimeout),
            '--action-budget', $actionBudget,
            '--screenshot-budget', $screenshotBudget
        ) `
        -MaximumCapturedBytes ([int]$Registry.limits.maximumCapturedBytesPerStream) `
        -TimeoutSeconds $TimeoutSeconds `
        -EnvironmentVariablesToRemove @($Registry.testSettings.environmentVariablesToRemove)

    try {
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            throw [FileNotFoundException]::new('GUI scenario manifest is missing.')
        }

        $manifestText = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8
        $userProfileDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
        if ($manifestText.Contains($RepositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
            (-not [string]::IsNullOrWhiteSpace($userProfileDirectory) -and
                $manifestText.Contains($userProfileDirectory, [StringComparison]::OrdinalIgnoreCase))) {
            throw [InvalidDataException]::new('GUI scenario manifest contains a local absolute path.')
        }

        $manifest = $manifestText | ConvertFrom-Json -AsHashtable -Depth 20
        if ([int]$manifest.schemaVersion -ne 1 -or
            [string]$manifest.scenario -cne $scenarioName -or
            -not $manifest.ContainsKey('status') -or
            [bool]$manifest.privateData.pathInspected -or
            [string]$manifest.privateData.state -cne 'not_selected' -or
            [string]$manifest.browserAutomation.protocol -cne 'WebView2-CDP' -or
            -not [bool]$manifest.browserAutomation.loopbackOnly -or
            [bool]$manifest.browserAutomation.arbitraryScriptAccepted -or
            [int]$manifest.budget.actionLimit -ne [int]$Scenario.actionBudget -or
            [int]$manifest.budget.actionCount -lt 0 -or
            [int]$manifest.budget.actionCount -gt [int]$Scenario.actionBudget -or
            [int]$manifest.budget.screenshotLimit -ne [int]$Scenario.screenshotBudget -or
            [int]$manifest.budget.screenshotCount -lt 0 -or
            [int]$manifest.budget.screenshotCount -gt [int]$Scenario.screenshotBudget -or
            @($manifest.screenshots).Count -ne [int]$manifest.budget.screenshotCount) {
            throw [InvalidDataException]::new('GUI scenario manifest is invalid.')
        }
        for ($imageIndex = 0; $imageIndex -lt @($manifest.screenshots).Count; $imageIndex++) {
            $screenshot = $manifest.screenshots[$imageIndex]
            $expectedFile = [IO.Path]::GetFileNameWithoutExtension($manifestPath) + '-view-' + ($imageIndex + 1) + '.png'
            if ([string]$screenshot.fileName -cne $expectedFile -or [int]$screenshot.bytes -le 8 -or [int]$screenshot.bytes -gt 2MB) {
                throw [InvalidDataException]::new('GUI screenshot metadata escaped the reviewed boundary.')
            }
            $imagePath = Join-Path (Split-Path $manifestPath -Parent) $expectedFile
            Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $imagePath
            if ((Get-Item -LiteralPath $imagePath).Length -ne [int]$screenshot.bytes) { throw 'GUI screenshot is incomplete.' }
            $signature = [byte[]]::new(8)
            $imageStream = [IO.File]::OpenRead($imagePath)
            try { $imageStream.ReadExactly($signature, 0, 8) } finally { $imageStream.Dispose() }
            if ([Convert]::ToHexString($signature) -cne '89504E470D0A1A0A') { throw 'GUI screenshot is not PNG.' }
        }

        $errorCodes = @($manifest.errors | ForEach-Object { [string]$_.code })
        foreach ($errorCode in $errorCodes) {
            if ([string]::IsNullOrWhiteSpace($errorCode) -or $errorCode -notmatch '^[a-z][a-z0-9_]+$') {
                throw [InvalidDataException]::new('GUI scenario error code is invalid.')
            }
        }

        $step['gui'] = [ordered]@{
            manifest = Get-JetRelativePath -RepositoryRoot $RepositoryRoot -Path $manifestPath
            scenario = [string]$manifest.scenario
            status = [string]$manifest.status
            assertions = $manifest.assertions
            budget = $manifest.budget
            cleanup = $manifest.cleanup
            errorCodes = $errorCodes
        }
        $firstError = if ($errorCodes.Count -gt 0) { [string]$errorCodes[0] } else { $null }
        $scenarioAssertionsComplete = switch -CaseSensitive ($scenarioName) {
            'startup-smoke' {
                [int]$manifest.budget.actionCount -eq [int]$Scenario.expectedActionCount
            }
            'synthetic-sqlite-create' {
                [int]$manifest.budget.actionCount -eq [int]$Scenario.expectedActionCount -and
                    [int]$manifest.budget.screenshotCount -eq 1 -and
                    [bool]$manifest.assertions.createFormVisible -and
                    [bool]$manifest.assertions.requiredFieldsEntered -and
                    [bool]$manifest.assertions.sqliteSelected -and
                    [bool]$manifest.assertions.projectCreated -and
                    [bool]$manifest.assertions.importStepVisible -and
                    [bool]$manifest.assertions.caseNameVisible -and
                    [bool]$manifest.assertions.projectJsonExists -and
                    [bool]$manifest.assertions.sqliteDatabaseExists -and
                    [bool]$manifest.assertions.storedProjectMatches -and
                    [bool]$manifest.assertions.storedOptionalMetadataBlank -and
                    [bool]$manifest.assertions.storedOperatorMatches
            }
            'mapping-required-sync' {
                [int]$manifest.budget.actionCount -eq [int]$Scenario.expectedActionCount -and
                    [bool]$manifest.assertions.mappingProjectLoaded -and
                    [bool]$manifest.assertions.mappingBaselineReady -and
                    [bool]$manifest.assertions.requiredRailBecameIncomplete -and
                    [bool]$manifest.assertions.requiredRailRecovered -and
                    [bool]$manifest.assertions.mappingFocusPreserved -and
                    [bool]$manifest.assertions.literalCommitFirstClick
            }
            'edited-report-still-loads' {
                [int]$manifest.budget.actionCount -eq [int]$Scenario.expectedActionCount -and
                    [bool]$manifest.assertions.editedReportLoaded -and
                    [bool]$manifest.assertions.modifiedOutsideVisible -and
                    [bool]$manifest.assertions.workpaperExportEnabled -and
                    [bool]$manifest.assertions.cleanupPanelAbsent -and
                    [bool]$manifest.assertions.workpaperHistoryVisible -and
                    [bool]$manifest.assertions.historyDoesNotCompleteCurrentRun -and
                    [bool]$manifest.assertions.oldVersionRevealAvailable -and
                    [bool]$manifest.assertions.missingVersionRevealDisabled -and
                    [bool]$manifest.assertions.workpaperHistoryRetainedAfterExport -and
                    [bool]$manifest.assertions.newestWorkpaperFirst -and
                    [bool]$manifest.assertions.workpaperHistoryPaginationVerified -and
                    [bool]$manifest.assertions.supportExportAvailable -and
                    [bool]$manifest.assertions.supportLogWritten -and
                    [bool]$manifest.assertions.supportLogSafe -and
                    [bool]$manifest.assertions.legacyJournalDiscarded
            }
            'approval-mapping-modes' {
                [int]$manifest.budget.actionCount -eq [int]$Scenario.expectedActionCount -and
                    [int]$manifest.budget.screenshotCount -eq 1 -and
                    [bool]$manifest.assertions.classicApprovalModesCoherent -and
                    [bool]$manifest.assertions.gridApprovalModesCoherent -and
                    [bool]$manifest.assertions.mappingOptionsDirtyStateVisible -and
                    [bool]$manifest.assertions.committedMappingOptionsRestored -and
                    [bool]$manifest.assertions.manualAutoPolicyResetAfterDetach -and
                    [bool]$manifest.assertions.postingStatusPolicyResetAfterSourceChange -and
                    [bool]$manifest.assertions.rdeSelectAllAndClearVerified -and
                    [bool]$manifest.assertions.requiredFieldJumpFocused
            }
            'validation-auto-outputs' {
                [int]$manifest.budget.actionCount -eq [int]$Scenario.expectedActionCount -and
                    [int]$manifest.budget.screenshotCount -eq 1 -and
                    [bool]$manifest.assertions.automaticValidationReportsCreated -and
                    [bool]$manifest.assertions.automaticMappingTemplateCreated -and
                    [bool]$manifest.assertions.filledTemplatePreservedAfterValidation
            }
            'side-month-workflow' {
                [int]$manifest.budget.actionCount -eq [int]$Scenario.expectedActionCount -and
                    [int]$manifest.budget.screenshotCount -eq 2 -and [bool]$manifest.assertions.sideMonthWorkflowVerified
            }
            'extended-conditions' {
                [int]$manifest.budget.actionCount -eq [int]$Scenario.expectedActionCount -and
                    [int]$manifest.budget.screenshotCount -eq 2 -and [bool]$manifest.assertions.extendedConditionsVerified
            }
            'authorized-list-recovery' {
                [int]$manifest.budget.actionCount -eq [int]$Scenario.expectedActionCount -and
                    [int]$manifest.budget.screenshotCount -eq 1 -and [bool]$manifest.assertions.authorizedListRecoveryVerified
            }
            'common-remap-recovery' {
                [int]$manifest.budget.actionCount -eq [int]$Scenario.expectedActionCount -and
                    [int]$manifest.budget.screenshotCount -eq 1 -and [bool]$manifest.assertions.commonRemapRecoveryVerified
            }
            'upstream-change-after-export' {
                [int]$manifest.budget.actionCount -eq [int]$Scenario.expectedActionCount -and
                    [int]$manifest.budget.screenshotCount -eq 1 -and [bool]$manifest.assertions.upstreamChangeAfterExportVerified
            }
            'case-to-workpaper' {
                [int]$manifest.budget.actionCount -eq [int]$Scenario.expectedActionCount -and
                    [int]$manifest.budget.screenshotCount -eq [int]$Scenario.screenshotBudget -and [bool]$manifest.assertions.caseToWorkpaperVerified
            }
            'null-details-recovery' {
                [int]$manifest.budget.actionCount -eq [int]$Scenario.expectedActionCount -and
                    [int]$manifest.budget.screenshotCount -eq 1 -and [bool]$manifest.assertions.nullDetailsRecoveryVerified
            }
            'nested-voucher-workflow' {
                [int]$manifest.budget.actionCount -eq [int]$Scenario.expectedActionCount -and
                    [int]$manifest.budget.screenshotCount -eq 2 -and [bool]$manifest.assertions.nestedVoucherWorkflowVerified
            }
            'legacy-form-workflow' {
                [int]$manifest.budget.actionCount -eq [int]$Scenario.expectedActionCount -and
                    [int]$manifest.budget.screenshotCount -eq 2 -and [bool]$manifest.assertions.legacyFormWorkflowVerified
            }
            'feedback-workflow' {
                [int]$manifest.budget.actionCount -eq [int]$Scenario.expectedActionCount -and
                    [int]$manifest.budget.screenshotCount -eq 2 -and [bool]$manifest.assertions.feedbackWorkflowVerified
            }
            { $_ -in @('filter-auditor-journey','filter-common-editing') } {
                [int]$manifest.budget.actionCount -eq [int]$Scenario.expectedActionCount -and
                    [int]$manifest.budget.screenshotCount -eq [int]$Scenario.screenshotBudget -and [bool]$manifest.assertions.filterWorkflowVerified
            }
            default { $false }
        }
        switch -CaseSensitive ([string]$manifest.status) {
            'passed' {
                if (-not $step.process.hasExitCode -or $step.process.exitCode -ne 0 -or
                    [int]$manifest.exitCode -ne 0 -or $errorCodes.Count -ne 0 -or
                    -not [bool]$manifest.assertions.documentLoaded -or
                    -not [bool]$manifest.assertions.bridgeReady -or
                    -not [bool]$manifest.assertions.systemPingSucceeded -or
                    -not [bool]$manifest.assertions.projectPickerVisible -or
                    -not [bool]$manifest.assertions.newProjectButtonVisible -or
                    -not [bool]$manifest.assertions.exitButtonVisible -or
                    -not [bool]$manifest.assertions.exitRequested -or
                    -not [bool]$manifest.assertions.processExited -or
                    -not [bool]$scenarioAssertionsComplete -or
                    [bool]$manifest.process.killAttempted -or
                    -not [bool]$manifest.cleanup.rootRemoved) {
                    throw [InvalidDataException]::new('GUI scenario pass evidence is incomplete.')
                }
                $step.status = 'passed'
                $step.classification.reason = $null
            }
            'failed' {
                if (-not $step.process.hasExitCode -or $step.process.exitCode -ne 1 -or
                    [int]$manifest.exitCode -ne 1 -or
                    -not [bool]$manifest.cleanup.rootRemoved) {
                    throw [InvalidDataException]::new('GUI scenario failed exit code disagrees with its manifest.')
                }
                $step.status = 'failed'
                $step.classification.reason = if ([string]::IsNullOrWhiteSpace($firstError)) {
                    'gui_' + ($scenarioName -replace '-', '_') + '_failed'
                }
                else {
                    $firstError
                }
            }
            'infrastructure_error' {
                if (-not $step.process.hasExitCode -or $step.process.exitCode -ne 4 -or
                    [int]$manifest.exitCode -ne 4) {
                    throw [InvalidDataException]::new('GUI scenario infrastructure exit code disagrees with its manifest.')
                }
                $step.status = 'infrastructure_error'
                $step.classification.reason = if ([string]::IsNullOrWhiteSpace($firstError)) {
                    'gui_driver_infrastructure_error'
                }
                else {
                    $firstError
                }
            }
            default {
                throw [InvalidDataException]::new('GUI scenario returned an unknown status.')
            }
        }
    }
    catch {
        $step['gui'] = [ordered]@{
            reportStatus = 'invalid'
            errorType = $_.Exception.GetType().Name
            errorId = [string]$_.FullyQualifiedErrorId
            errorLine = [int]$_.InvocationInfo.ScriptLineNumber
        }
        $step.status = 'infrastructure_error'
        $step.classification.reason = 'gui_result_invalid'
    }

    return $step
}

function Invoke-JetExcelFixtureStep {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $RunDirectory,
        [Parameter(Mandatory = $true)] [string] $ScratchDirectory,
        [Parameter(Mandatory = $true)] $Registry,
        [Parameter(Mandatory = $true)] [int] $TimeoutSeconds
    )

    $sourceRoot = Join-Path $ScratchDirectory 'excel-source'
    Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $sourceRoot
    if (Test-Path -LiteralPath $sourceRoot) {
        throw [IOException]::new('Excel synthetic source root already exists.')
    }
    [void][IO.Directory]::CreateDirectory($sourceRoot)
    Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $sourceRoot

    $fixtureVariable = [string]$Registry.excelSettings.fixtureEnvironmentVariable
    $environmentToRemove = @(
        $Registry.testSettings.environmentVariablesToRemove |
            Where-Object { [string]$_ -cne $fixtureVariable }
    )
    $step = Invoke-JetTestStep `
        -RepositoryRoot $RepositoryRoot `
        -RunDirectory $RunDirectory `
        -Configuration ([string]$Registry.excelSettings.configuration) `
        -Name 'excel-synthetic-six-report-fixture' `
        -MinimumExpectedTests 1 `
        -Registry $Registry `
        -TimeoutSeconds $TimeoutSeconds `
        -IncludeKind Method `
        -IncludeValues @([string]$Registry.excelSettings.fixtureMethodPattern) `
        -EnvironmentVariablesToRemove $environmentToRemove `
        -EnvironmentVariablesToSet ([ordered]@{ $fixtureVariable = $sourceRoot }) `
        -SensitiveValuesToRedact @($sourceRoot) `
        -SkipPolicy NoSkips

    if ($step.status -cne 'passed') {
        return $step
    }

    try {
        $manifestPath = Join-Path $sourceRoot 'evidence-manifest.json'
        $workbookRoot = Join-Path $sourceRoot 'workbooks'
        Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $manifestPath
        Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $workbookRoot
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
            -not (Test-Path -LiteralPath $workbookRoot -PathType Container)) {
            throw [FileNotFoundException]::new('Excel synthetic fixture evidence is missing.')
        }

        $manifestText = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8
        $userProfileDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
        if ($manifestText.Contains($RepositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
            (-not [string]::IsNullOrWhiteSpace($userProfileDirectory) -and
                $manifestText.Contains($userProfileDirectory, [StringComparison]::OrdinalIgnoreCase))) {
            throw [InvalidDataException]::new('Excel synthetic fixture evidence contains a local absolute path.')
        }

        $manifest = $manifestText | ConvertFrom-Json -AsHashtable -Depth 20
        $artifacts = @($manifest.artifacts)
        $expectedKinds = @($Registry.excelSettings.reportKinds | ForEach-Object { [string]$_ })
        if ($artifacts.Count -ne $expectedKinds.Count -or
            ((@($artifacts | ForEach-Object { [string]$_.kind }) -join ',') -cne ($expectedKinds -join ','))) {
            throw [InvalidDataException]::new('Excel synthetic fixture report inventory is invalid.')
        }

        foreach ($artifact in $artifacts) {
            $kind = [string]$artifact.kind
            $expectedFileName = "$kind.xlsx"
            if ([string]$artifact.evidenceFileName -cne $expectedFileName -or
                [long]$artifact.bytes -le 0 -or
                [long]$artifact.bytes -gt 536870912 -or
                [string]$artifact.sha256 -cnotmatch '^[0-9a-f]{64}$') {
                throw [InvalidDataException]::new('Excel synthetic fixture artifact metadata is invalid.')
            }

            $workbookPath = Join-Path $workbookRoot $expectedFileName
            Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $workbookPath
            if (-not (Test-Path -LiteralPath $workbookPath -PathType Leaf)) {
                throw [FileNotFoundException]::new('Excel synthetic fixture workbook is missing.')
            }
            $workbook = Get-Item -LiteralPath $workbookPath -Force
            if (($workbook.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                $workbook.Length -ne [long]$artifact.bytes -or
                (Get-FileHash -LiteralPath $workbookPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$artifact.sha256) {
                throw [InvalidDataException]::new('Excel synthetic fixture workbook disagrees with its manifest.')
            }
        }

        $nativePath = Join-Path $sourceRoot 'native-source-import-verification.json'
        Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $nativePath
        if (-not (Test-Path -LiteralPath $nativePath -PathType Leaf) -or (Get-Item -LiteralPath $nativePath).Length -gt 16384) {
            throw [InvalidDataException]::new('Native source import evidence is missing or too large.')
        }
        $native = Get-Content -LiteralPath $nativePath -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 10
        $nativeCases = @()
        foreach ($format in @('.mdb', '.accdb', '.xls')) {
            foreach ($provider in @('sqlite', 'duckdb')) {
                $case = @($native.cases | Where-Object { $_.format -ceq $format -and $_.provider -ceq $provider })
                if ($case.Count -ne 1 -or $native.status -cne 'passed' -or [int]$case[0].importedRows -ne 2) {
                    throw [InvalidDataException]::new('Native source import case inventory is invalid.')
                }
                foreach ($check in @('inspected', 'previewed', 'retried', 'validated', 'filtered', 'reopened', 'sourceUnchanged')) {
                    if ($case[0][$check] -isnot [bool] -or -not $case[0][$check]) {
                        throw [InvalidDataException]::new('Native source import case is incomplete.')
                    }
                }
                $nativeCases += [ordered]@{ format = $format; provider = $provider; importedRows = 2; status = 'passed' }
            }
        }
        if (@($native.cases).Count -ne 6) { throw [InvalidDataException]::new('Native source import has unexpected cases.') }
        $step['excelFixture'] = [ordered]@{
            source = 'same_run_synthetic_six_report_journey'
            workbookCount = $artifacts.Count
            reportKinds = $expectedKinds
            nativeSourceImports = $nativeCases
            privateData = [ordered]@{ pathInspected = $false }
        }
    }
    catch {
        $step['excelFixture'] = [ordered]@{
            reportStatus = 'invalid'
            errorType = $_.Exception.GetType().Name
            errorId = [string]$_.FullyQualifiedErrorId
            errorLine = [int]$_.InvocationInfo.ScriptLineNumber
        }
        $step.status = 'infrastructure_error'
        $step.classification.reason = 'excel_fixture_invalid'
    }

    return $step
}

function Invoke-JetExcelScenarioStep {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $RunDirectory,
        [Parameter(Mandatory = $true)] [string] $ScratchDirectory,
        [Parameter(Mandatory = $true)] [string] $RunId,
        [Parameter(Mandatory = $true)] $Registry,
        [Parameter(Mandatory = $true)] [string] $ReportKind,
        [Parameter(Mandatory = $true)] [int] $TimeoutSeconds
    )

    $dotnet = Get-Command dotnet -CommandType Application -ErrorAction Stop |
        Select-Object -First 1 -ExpandProperty Source
    $driverPath = Join-Path $RepositoryRoot ([string]$Registry.excelSettings.driverAssembly)
    $manifestPath = Join-Path $RunDirectory "excel-$ReportKind-manifest.json"
    Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $driverPath
    Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $manifestPath
    if (-not (Test-Path -LiteralPath $driverPath -PathType Leaf)) {
        throw [FileNotFoundException]::new('Excel driver build output is missing.')
    }

    $driverTimeout = [int]$Registry.excelSettings.timeoutSeconds
    $step = Invoke-JetChildStep `
        -RepositoryRoot $RepositoryRoot `
        -RunDirectory $RunDirectory `
        -Name "excel-$ReportKind" `
        -FileName $dotnet `
        -Arguments @(
            $driverPath,
            '--owned-root', $ScratchDirectory,
            '--run-id', $RunId,
            '--manifest', $manifestPath,
            '--report-kind', $ReportKind,
            '--timeout-seconds', ([string]$driverTimeout)
        ) `
        -DisplayCommand @(
            'dotnet', ([string]$Registry.excelSettings.driverAssembly),
            '--owned-root', (Get-JetRelativePath -RepositoryRoot $RepositoryRoot -Path $ScratchDirectory),
            '--run-id', $RunId,
            '--manifest', (Get-JetRelativePath -RepositoryRoot $RepositoryRoot -Path $manifestPath),
            '--report-kind', $ReportKind,
            '--timeout-seconds', ([string]$driverTimeout)
        ) `
        -MaximumCapturedBytes ([int]$Registry.limits.maximumCapturedBytesPerStream) `
        -TimeoutSeconds $TimeoutSeconds `
        -EnvironmentVariablesToRemove @($Registry.testSettings.environmentVariablesToRemove)

    try {
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            throw [FileNotFoundException]::new('Excel scenario manifest is missing.')
        }

        $manifestText = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8
        $userProfileDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
        if ($manifestText.Contains($RepositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
            (-not [string]::IsNullOrWhiteSpace($userProfileDirectory) -and
                $manifestText.Contains($userProfileDirectory, [StringComparison]::OrdinalIgnoreCase))) {
            throw [InvalidDataException]::new('Excel scenario manifest contains a local absolute path.')
        }

        $manifest = $manifestText | ConvertFrom-Json -AsHashtable -Depth 20
        if ([int]$manifest.schemaVersion -ne 1 -or
            [string]$manifest.scenario -cne [string]$Registry.excelSettings.scenario -or
            [string]$manifest.reportKind -cne $ReportKind -or
            [int]$manifest.timeoutSeconds -ne $driverTimeout -or
            [bool]$manifest.privateData.pathInspected -or
            [string]$manifest.privateData.state -cne 'not_selected' -or
            [int]$manifest.openPolicy.updateLinks -ne 0 -or
            -not [bool]$manifest.openPolicy.readOnly -or
            -not [bool]$manifest.openPolicy.ignoreReadOnlyRecommended -or
            [bool]$manifest.openPolicy.addToMru -or
            [int]$manifest.openPolicy.corruptLoad -ne 0 -or
            -not [bool]$manifest.openPolicy.macrosDisabled -or
            -not [bool]$manifest.process.preExistingProcessesAllowed -or
            [int]$manifest.process.preExistingProcessCount -lt 0) {
            throw [InvalidDataException]::new('Excel scenario manifest is invalid.')
        }

        $errorCodes = @($manifest.errors | ForEach-Object { [string]$_.code })
        foreach ($errorCode in $errorCodes) {
            if ([string]::IsNullOrWhiteSpace($errorCode) -or $errorCode -notmatch '^[a-z][a-z0-9_]+$') {
                throw [InvalidDataException]::new('Excel scenario error code is invalid.')
            }
        }

        $step['excel'] = [ordered]@{
            manifest = Get-JetRelativePath -RepositoryRoot $RepositoryRoot -Path $manifestPath
            scenario = [string]$manifest.scenario
            reportKind = [string]$manifest.reportKind
            status = [string]$manifest.status
            officeVersion = [string]$manifest.officeVersion
            openPolicy = $manifest.openPolicy
            assertions = $manifest.assertions
            process = $manifest.process
            artifacts = $manifest.artifacts
            cleanup = $manifest.cleanup
            errorCodes = $errorCodes
        }
        $firstError = if ($errorCodes.Count -gt 0) { [string]$errorCodes[0] } else { $null }
        switch -CaseSensitive ([string]$manifest.status) {
            'passed' {
                if (-not $step.process.hasExitCode -or $step.process.exitCode -ne 0 -or
                    [int]$manifest.exitCode -ne 0 -or $errorCodes.Count -ne 0 -or
                    [string]$manifest.officeVersion -notmatch '^[0-9]{1,3}(\.[0-9]{1,5}){0,3}$' -or
                    -not [bool]$manifest.assertions.sourceWorkbookExists -or
                    -not [bool]$manifest.assertions.sourceWorkbookUnchanged -or
                    -not [bool]$manifest.assertions.workbookOpened -or
                    -not [bool]$manifest.assertions.workbookOpenedReadOnly -or
                    [int]$manifest.assertions.externalLinkCount -ne 0 -or
                    [long]$manifest.assertions.formulaErrorCountBefore -lt 0 -or
                    [long]$manifest.assertions.formulaErrorCountAfter -lt 0 -or
                    [bool]$manifest.assertions.formulaErrorsIncreased -or
                    -not [bool]$manifest.assertions.fullRecalculationCompleted -or
                    -not [bool]$manifest.assertions.savedCopyCreated -or
                    -not [bool]$manifest.assertions.sourceWorkbookClosed -or
                    -not [bool]$manifest.assertions.savedCopyReopened -or
                    -not [bool]$manifest.assertions.savedCopyOpenedReadOnly -or
                    -not [bool]$manifest.assertions.savedCopyHasZeroExternalLinks -or
                    -not [bool]$manifest.assertions.savedCopyClosed -or
                    -not [bool]$manifest.assertions.pdfExported -or
                    -not [bool]$manifest.assertions.pdfParseable -or
                    [long]$manifest.artifacts.sourceWorkbookBytes -le 0 -or
                    [long]$manifest.artifacts.savedWorkbookBytes -le 0 -or
                    [long]$manifest.artifacts.pdfBytes -le 0 -or
                    -not [bool]$manifest.process.ownedProcessVerified -or
                    [bool]$manifest.process.deadlineExceeded -or
                    ([bool]$manifest.process.killAttempted -and -not [bool]$manifest.process.killSucceeded) -or
                    -not [bool]$manifest.cleanup.applicationQuitRequested -or
                    -not [bool]$manifest.cleanup.ownedProcessExited -or
                    -not [bool]$manifest.cleanup.outputsRetainedForHarness) {
                    throw [InvalidDataException]::new('Excel scenario pass evidence is incomplete.')
                }
                $step.status = 'passed'
                $step.classification.reason = $null
            }
            'failed' {
                if (-not $step.process.hasExitCode -or $step.process.exitCode -ne 1 -or
                    [int]$manifest.exitCode -ne 1 -or $errorCodes.Count -eq 0 -or
                    -not [bool]$manifest.assertions.sourceWorkbookUnchanged -or
                    -not [bool]$manifest.cleanup.ownedProcessExited) {
                    throw [InvalidDataException]::new('Excel scenario failed evidence is incomplete.')
                }
                $step.status = 'failed'
                $step.classification.reason = $firstError
            }
            'blocked' {
                if (-not $step.process.hasExitCode -or $step.process.exitCode -ne 2 -or
                    [int]$manifest.exitCode -ne 2 -or $errorCodes.Count -eq 0 -or
                    -not [bool]$manifest.assertions.sourceWorkbookUnchanged -or
                    -not [bool]$manifest.cleanup.ownedProcessExited) {
                    throw [InvalidDataException]::new('Excel scenario blocked evidence is incomplete.')
                }
                $step.status = 'blocked'
                $step.classification.reason = $firstError
            }
            'infrastructure_error' {
                if (-not $step.process.hasExitCode -or $step.process.exitCode -ne 4 -or
                    [int]$manifest.exitCode -ne 4 -or $errorCodes.Count -eq 0) {
                    throw [InvalidDataException]::new('Excel scenario infrastructure evidence is incomplete.')
                }
                $step.status = 'infrastructure_error'
                $step.classification.reason = $firstError
            }
            default {
                throw [InvalidDataException]::new('Excel scenario returned an unknown status.')
            }
        }
    }
    catch {
        $step['excel'] = [ordered]@{
            reportStatus = 'invalid'
            errorType = $_.Exception.GetType().Name
            errorId = [string]$_.FullyQualifiedErrorId
            errorLine = [int]$_.InvocationInfo.ScriptLineNumber
        }
        if ([bool]$step.process.timedOut) {
            $step.status = 'blocked'
            $step.classification.reason = 'excel_driver_timeout'
        }
        else {
            $step.status = 'infrastructure_error'
            $step.classification.reason = 'excel_result_invalid'
        }
    }

    return $step
}
