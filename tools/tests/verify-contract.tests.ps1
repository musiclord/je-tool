#requires -Version 7.4

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$utf8 = [Text.UTF8Encoding]::new($false, $true)
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runnerPath = Join-Path $repositoryRoot 'tools/verify.ps1'
$harnessModulePath = Join-Path $repositoryRoot 'tools/harness/JetHarness.psm1'
$packageVerifierPath = Join-Path $repositoryRoot 'tools/harness/JetPackageVerifier.ps1'
$documentationVerifierPath = Join-Path $repositoryRoot 'tools/harness/JetDocumentationCheck.ps1'
$pwshPath = [Environment]::ProcessPath
$suiteId = '{0}-{1}' -f ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff')), ([Guid]::NewGuid().ToString('N'))
$suiteRelativeRoot = "artifacts/harness/contract-tests/$suiteId"
$suiteRoot = Join-Path $repositoryRoot $suiteRelativeRoot
$runsRelativeRoot = "$suiteRelativeRoot/runs"
$controlRoot = Join-Path $repositoryRoot 'artifacts/harness/control'
$holderPath = Join-Path $controlRoot 'holder.json'
$assertionCount = 0
$scenarioNames = New-Object 'System.Collections.Generic.List[string]'
$startedUtc = [DateTime]::UtcNow

function Assert-Contract {
    param(
        [Parameter(Mandatory = $true)] [bool] $Condition,
        [Parameter(Mandatory = $true)] [string] $Message
    )

    $script:assertionCount++
    if (-not $Condition) {
        throw [InvalidOperationException]::new($Message)
    }
}

function Invoke-Runner {
    param(
        [Parameter(Mandatory = $true)] [string[]] $Arguments,
        [Parameter(Mandatory = $true)] [string] $Label
    )

    $stderrPath = Join-Path $suiteRoot "$Label.runner.stderr.log"
    $stdoutLines = @(& $pwshPath -NoProfile -File $runnerPath @Arguments 2> $stderrPath)
    $nativeExitCode = $LASTEXITCODE
    $stderr = if (Test-Path -LiteralPath $stderrPath -PathType Leaf) {
        Get-Content -LiteralPath $stderrPath -Raw -Encoding utf8
    }
    else {
        ''
    }

    Assert-Contract -Condition ($stdoutLines.Count -eq 1) -Message "$Label must emit exactly one stdout envelope."
    $envelope = $stdoutLines[0] | ConvertFrom-Json -Depth 20
    return [pscustomobject]@{
        Label = $Label
        NativeExitCode = $nativeExitCode
        Envelope = $envelope
        Stderr = $stderr
    }
}

function Get-Receipt {
    param([Parameter(Mandatory = $true)] $Run)

    Assert-Contract -Condition (-not [string]::IsNullOrWhiteSpace([string]$Run.Envelope.receipt)) `
        -Message "$($Run.Label) must expose a receipt path."
    Assert-Contract -Condition (-not [IO.Path]::IsPathRooted([string]$Run.Envelope.receipt)) `
        -Message "$($Run.Label) receipt path must remain repository-relative."
    $receiptPath = Join-Path $repositoryRoot ([string]$Run.Envelope.receipt)
    Assert-Contract -Condition (Test-Path -LiteralPath $receiptPath -PathType Leaf) `
        -Message "$($Run.Label) receipt file is missing."
    return Get-Content -LiteralPath $receiptPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 20
}

function Invoke-PackagePreflight {
    param(
        [Parameter(Mandatory = $true)] [string] $SourceRoot,
        [Parameter(Mandatory = $true)] [string] $ManifestPath,
        [Parameter(Mandatory = $true)] [string] $Label
    )

    $stderrPath = Join-Path $suiteRoot "$Label.package.stderr.log"
    $stdoutLines = @(& $pwshPath -NoProfile -File $packageVerifierPath `
            -Mode SourcePreflight `
            -SourceRoot $SourceRoot `
            -ManifestPath $ManifestPath 2> $stderrPath)
    $nativeExitCode = $LASTEXITCODE
    $stderr = if (Test-Path -LiteralPath $stderrPath -PathType Leaf) {
        Get-Content -LiteralPath $stderrPath -Raw -Encoding utf8
    }
    else {
        ''
    }
    Assert-Contract -Condition ($stdoutLines.Count -eq 1) `
        -Message "$Label package preflight must emit one stdout envelope."
    return [pscustomobject]@{
        NativeExitCode = $nativeExitCode
        Envelope = $stdoutLines[0] | ConvertFrom-Json -Depth 20
        Stdout = [string]$stdoutLines[0]
        Stderr = $stderr
        Manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 20
        ManifestText = Get-Content -LiteralPath $ManifestPath -Raw -Encoding utf8
    }
}

function Invoke-PackageVerificationFixture {
    param(
        [Parameter(Mandatory = $true)] [string] $SourceRoot,
        [Parameter(Mandatory = $true)] [string] $CandidateRoot,
        [Parameter(Mandatory = $true)] [string] $OwnedRoot,
        [Parameter(Mandatory = $true)] [string] $ManifestPath,
        [Parameter(Mandatory = $true)] [string] $Label
    )

    $stderrPath = Join-Path $suiteRoot "$Label.package.stderr.log"
    $stdoutLines = @(& $pwshPath -NoProfile -File $packageVerifierPath `
            -Mode Verify `
            -SourceRoot $SourceRoot `
            -ManifestPath $ManifestPath `
            -CandidatePath $CandidateRoot `
            -OwnedRoot $OwnedRoot `
            -RegistryPath (Join-Path $repositoryRoot 'tools/harness/lanes.json') 2> $stderrPath)
    $nativeExitCode = $LASTEXITCODE
    $stderr = if (Test-Path -LiteralPath $stderrPath -PathType Leaf) {
        Get-Content -LiteralPath $stderrPath -Raw -Encoding utf8
    }
    else {
        ''
    }
    Assert-Contract -Condition ($stdoutLines.Count -eq 1) `
        -Message "$Label package verification must emit one stdout envelope."
    return [pscustomobject]@{
        NativeExitCode = $nativeExitCode
        Envelope = $stdoutLines[0] | ConvertFrom-Json -Depth 20
        Stderr = $stderr
        Manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 20
    }
}

function Invoke-DocumentationFixture {
    param(
        [Parameter(Mandatory = $true)] [string] $FixtureRoot,
        [Parameter(Mandatory = $true)] [string] $ReportPath,
        [Parameter(Mandatory = $true)] [string] $PolicyPath,
        [Parameter(Mandatory = $true)] [string] $Label
    )

    $stderrPath = Join-Path $suiteRoot "$Label.documentation.stderr.log"
    $stdoutLines = @(& $pwshPath -NoProfile -File $documentationVerifierPath `
            -RepositoryRoot $FixtureRoot `
            -ReportPath $ReportPath `
            -PolicyPath $PolicyPath 2> $stderrPath)
    $nativeExitCode = $LASTEXITCODE
    $stderr = if (Test-Path -LiteralPath $stderrPath -PathType Leaf) {
        Get-Content -LiteralPath $stderrPath -Raw -Encoding utf8
    }
    else {
        ''
    }
    Assert-Contract -Condition ($stdoutLines.Count -eq 1) `
        -Message "$Label documentation check must emit one stdout envelope."
    return [pscustomobject]@{
        NativeExitCode = $nativeExitCode
        Envelope = $stdoutLines[0] | ConvertFrom-Json -Depth 20
        Stdout = [string]$stdoutLines[0]
        Stderr = $stderr
        Report = Get-Content -LiteralPath $ReportPath -Raw -Encoding utf8 | ConvertFrom-Json -Depth 20
        ReportText = Get-Content -LiteralPath $ReportPath -Raw -Encoding utf8
    }
}

function Assert-CommonReceipt {
    param(
        [Parameter(Mandatory = $true)] $Receipt,
        [Parameter(Mandatory = $true)] [string] $ExpectedStatus
    )

    Assert-Contract -Condition ($Receipt.schemaVersion -eq 1) -Message 'Receipt schema must be 1.'
    Assert-Contract -Condition ($Receipt.status -ceq $ExpectedStatus) -Message "Expected receipt status $ExpectedStatus."
    Assert-Contract -Condition ($Receipt.privateData.pathInspected -eq $false) `
        -Message 'Non-private harness receipts must keep privateData.pathInspected=false.'
    Assert-Contract -Condition ($Receipt.cleanup.status -ceq 'passed') -Message 'Owned scratch cleanup must pass.'
    Assert-Contract -Condition (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot "$($Receipt.runDirectory)/scratch"))) `
        -Message 'Owned scratch directory must be absent after cleanup.'
}

function Start-LockHolder {
    param([Parameter(Mandatory = $true)] [string] $Label)

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $pwshPath
    $startInfo.WorkingDirectory = $repositoryRoot
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    foreach ($argument in @(
            '-NoProfile',
            '-File', $runnerPath,
            '-Command', 'Contract',
            '-ContractScenario', 'HoldLock',
            # The contender starts in a fresh PowerShell process. On a cold or busy
            # desktop, module loading can take longer than five seconds after the
            # holder has published its PID, so keep the ownership window explicit
            # but comfortably beyond process startup.
            '-ProbeSeconds', '20',
            '-TimeoutSeconds', '35',
            '-EvidenceRoot', $runsRelativeRoot)) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    Assert-Contract -Condition $process.Start() -Message "$Label holder process did not start."
    return $process
}

try {
    [void][IO.Directory]::CreateDirectory($suiteRoot)
    $suiteOwner = [ordered]@{
        schemaVersion = 1
        kind = 'jet-harness-contract-suite'
        suiteId = $suiteId
        processId = $PID
        createdUtc = $startedUtc.ToString('O')
    }
    [IO.File]::WriteAllText(
        (Join-Path $suiteRoot '.jet-harness-contract-suite.json'),
        ($suiteOwner | ConvertTo-Json -Depth 6) + "`n",
        $utf8)

    $harnessModule = Import-Module -Name $harnessModulePath -Force -PassThru
    $indexFixtureRoot = Join-Path $suiteRoot 'source-index-fixture'
    [void][IO.Directory]::CreateDirectory($indexFixtureRoot)
    $gitPath = Get-Command git -CommandType Application -ErrorAction Stop |
        Select-Object -First 1 -ExpandProperty Source
    & $gitPath -c "safe.directory=$($indexFixtureRoot.Replace('\', '/'))" `
        -c 'core.excludesFile=' -c 'core.autocrlf=false' `
        -C $indexFixtureRoot init --quiet --initial-branch=main
    Assert-Contract -Condition ($LASTEXITCODE -eq 0) `
        -Message 'The source-index fixture repository must initialize.'
    [IO.File]::WriteAllText((Join-Path $indexFixtureRoot '.gitignore'), "artifacts/`n", $utf8)
    [IO.File]::WriteAllText((Join-Path $indexFixtureRoot 'fixture.txt'), "synthetic candidate`n", $utf8)
    [IO.File]::WriteAllText(
        (Join-Path $indexFixtureRoot 'candidate.txt'),
        ".gitignore`ncandidate.txt`nfixture.txt`n",
        $utf8)
    $fixtureIndexBefore = & $harnessModule {
        param($Root)
        Get-JetSourceIndexFingerprint -RepositoryRoot $Root
    } $indexFixtureRoot
    $fixtureRegistry = [pscustomobject]@{
        releaseCandidateSettings = [pscustomobject]@{
            candidateManifest = 'candidate.txt'
            snapshotWorkspaceRoot = 'artifacts/harness/rc'
        }
    }
    $fixtureSnapshot = & $harnessModule {
        param($Root, $RunId, $Registry)
        New-JetReleaseCandidateSnapshot -RepositoryRoot $Root -RunId $RunId -Registry $Registry
    } $indexFixtureRoot $suiteId $fixtureRegistry
    $fixtureIndexAfterSnapshot = & $harnessModule {
        param($Root)
        Get-JetSourceIndexFingerprint -RepositoryRoot $Root
    } $indexFixtureRoot
    Assert-Contract -Condition ($fixtureSnapshot.Step.status -ceq 'passed' -and
        $fixtureSnapshot.Step.evidence.sourceIndexMeasured -eq $true -and
        $fixtureSnapshot.Step.evidence.sourceIndexMutated -eq $false -and
        $fixtureIndexBefore.EntryCount -eq 0 -and
        $fixtureIndexAfterSnapshot.EntryCount -eq 0 -and
        $fixtureIndexBefore.Sha256 -ceq $fixtureIndexAfterSnapshot.Sha256) `
        -Message 'Candidate snapshot creation must measure and preserve source index entries.'
    & $gitPath -c "safe.directory=$($indexFixtureRoot.Replace('\', '/'))" `
        -c 'core.excludesFile=' -c 'core.autocrlf=false' `
        -C $indexFixtureRoot add -- fixture.txt
    Assert-Contract -Condition ($LASTEXITCODE -eq 0) `
        -Message 'The synthetic index mutation must stage its exact fixture path.'
    $fixtureIndexAfterStage = & $harnessModule {
        param($Root)
        Get-JetSourceIndexFingerprint -RepositoryRoot $Root
    } $indexFixtureRoot
    Assert-Contract -Condition ($fixtureIndexAfterStage.EntryCount -eq 1 -and
        $fixtureIndexAfterStage.Sha256 -cne $fixtureIndexBefore.Sha256) `
        -Message 'The source index fingerprint must detect an actual staged-entry change.'
    $scenarioNames.Add('SourceIndexMeasurement')

    $registry = Get-Content -LiteralPath (Join-Path $repositoryRoot 'tools/harness/lanes.json') -Raw -Encoding utf8 |
        ConvertFrom-Json -AsHashtable -Depth 20
    $enabledLanes = @($registry.lanes.Keys | Where-Object { [bool]$registry.lanes[$_].enabled })
    Assert-Contract -Condition ((@($enabledLanes | Sort-Object) -join ',') -ceq 'Excel,Focused,Foundation,Gui,Package,PrivateCase,Provider,Public,ReleaseCandidate') `
        -Message 'The enabled lanes must match the currently implemented Harness boundary.'
    Assert-Contract -Condition ([bool]$registry.lanes.PrivateCase.enabled -and
        [bool]$registry.commands.PrivateCase.enabled -and
        [bool]$registry.commands.PrivateCase.requiresExclusiveLock -and
        [bool]$registry.commands.PrivateCase.privateDataAccess) `
        -Message 'PrivateCase must remain an explicit, exclusive, private-data command.'
    Assert-Contract -Condition ([bool]$registry.lanes.ReleaseCandidate.enabled -and
        [bool]$registry.commands.ReleaseCandidate.enabled -and
        [bool]$registry.commands.ReleaseCandidate.requiresExclusiveLock -and
        -not [bool]$registry.commands.ReleaseCandidate.privateDataAccess) `
        -Message 'ReleaseCandidate must be an explicit, exclusive, public-data command.'
    Assert-Contract -Condition ((@($registry.releaseCandidateSettings.steps | ForEach-Object {
                    "$($_.name):$($_.command):$($_.configuration):$([bool]$_.noRestore)"
                }) -join ',') -ceq
            'contract:Contract:Release:False,documentation:Documentation:Release:False,public:Public:Release:False,package:Package:Release:True,gui:Gui:AgentGuiTest:False,excel:Excel:Release:False') `
        -Message 'ReleaseCandidate must retain its six reviewed public-candidate children.'
    Assert-Contract -Condition ([string]$registry.releaseCandidateSettings.candidateManifest -ceq
        'docs/first-root-commit-candidate.txt' -and
        [string]$registry.releaseCandidateSettings.snapshotWorkspaceRoot -ceq 'artifacts/harness/rc' -and
        -not [bool]$registry.releaseCandidateSettings.privateCaseIncluded -and
        -not [bool]$registry.releaseCandidateSettings.liveProviderIncluded) `
        -Message 'ReleaseCandidate must use the first-root inventory and short owned workspace without PrivateCase or live Provider.'
    Assert-Contract -Condition ([string]$registry.runnerContractVersion -ceq '8.0') `
        -Message 'The current ReleaseCandidate composition requires runner contract 8.0.'
    Assert-Contract -Condition ([int]$registry.testSettings.privateCaseMinimumExpectedTests -eq 2) `
        -Message 'PrivateCase must run both the manifest and full acceptance tests.'
    Assert-Contract -Condition ((@(
                [string]$registry.testSettings.privateCaseRootEnvironmentVariable,
                [string]$registry.testSettings.privateCaseManifestEnvironmentVariable,
                [string]$registry.testSettings.privateCaseProviderEnvironmentVariable) -join ',') -ceq
            'JET_PRIVATE_CASE_ROOT,JET_PRIVATE_CASE_MANIFEST,JET_PRIVATE_CASE_PROVIDER') `
        -Message 'PrivateCase must use only the reviewed explicit input boundary.'
    Assert-Contract -Condition ((@($registry.guiSettings.scenarios.name) -join ',') -ceq `
            'startup-smoke,synthetic-sqlite-create,mapping-required-sync,edited-report-still-loads') `
        -Message 'The GUI lane must contain only the four reviewed scenarios.'
    Assert-Contract -Condition ([string]$registry.excelSettings.scenario -ceq 'synthetic-report-roundtrip') `
        -Message 'The Excel lane must retain its one reviewed synthetic scenario.'
    Assert-Contract -Condition ((@($registry.excelSettings.reportKinds) -join ',') -ceq `
            'accountMapping,criteriaSelectionReport,infReport,prescreenReport,validationReport,workingPaper') `
        -Message 'The Excel lane must remain limited to the six synthetic product outputs.'
    foreach ($requiredPattern in @('*ReportArtifactTrustJourneyTests*', '*ProjectReportArtifactStoreTests*')) {
        Assert-Contract -Condition (@($registry.testSettings.packageMethodPatterns) -ccontains $requiredPattern) `
            -Message "Package must include the report storage regression family $requiredPattern."
    }
    $workbookJourneyMethod = 'AccountMappingHandoff_PreservesValidationRun_AndPublishesFiveReportsAndTemplate'
    Assert-Contract -Condition ([string]$registry.excelSettings.fixtureMethodPattern -ceq `
        "*SixReportWorkflowJourneyTests.$workbookJourneyMethod*") `
        -Message 'Excel must select the current five-report and editable-template journey.'
    $workbookJourneySource = Get-Content -LiteralPath (Join-Path $repositoryRoot `
        'src/JET/tests/JET.Tests/Application/SixReportWorkflowJourneyTests.cs') -Raw -Encoding utf8
    Assert-Contract -Condition ($workbookJourneySource.Contains("Task $workbookJourneyMethod()", [StringComparison]::Ordinal)) `
        -Message 'The Excel fixture filter must resolve to the current test method.'
    $publicSkipPolicy = Get-Content -LiteralPath (Join-Path $repositoryRoot `
        'tools/harness/public-skip-policy.json') -Raw -Encoding utf8 | ConvertFrom-Json
    Assert-Contract -Condition (@($publicSkipPolicy.capabilityGroups).Count -eq 0) `
        -Message 'Retired filesystem-link tests must not retain a public skip allowance.'

    $protectedWorkspacePatterns = @($registry.testSettings.protectedWorkspaceMethodPatterns)
    Assert-Contract -Condition (@(
            $protectedWorkspacePatterns |
                Where-Object { [string]$_ -cnotmatch '^\*[A-Za-z0-9_.+]+Tests\.\*$' }
        ).Count -eq 0) `
        -Message 'Protected-workspace filters must identify exact test classes.'
    Assert-Contract -Condition ($protectedWorkspacePatterns -cnotcontains '*JET.Tests.Infrastructure.LegacyAuditParity*') `
        -Message 'A broad LegacyAuditParity filter must not hide synthetic oracle tests.'
    Assert-Contract -Condition (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot `
                    'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityCrossCaseVerification.cs'))) `
        -Message 'The retired two-case PrivateParity driver must not return.'
    foreach ($retiredLegacyBatch in @(
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityReportBoundaryCorrectionBatch.cs',
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityStage6Audit.cs',
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityStage6AuditTests.cs',
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityStage6ClassificationCatalog.cs',
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityStage6Finalizer.cs',
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityStage7FieldInfoCorrectionBatch.cs')) {
        Assert-Contract -Condition (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $retiredLegacyBatch))) `
            -Message "$retiredLegacyBatch is an old batch correction and must not return."
    }
    foreach ($safeLegacyOracleClass in @(
            'JET.Tests.Infrastructure.LegacyAuditParityComparatorTests',
            'JET.Tests.Infrastructure.LegacyAuditParityControlTests',
            'JET.Tests.Infrastructure.LegacyAuditParityJourneyInputFactoryTests',
            'JET.Tests.Infrastructure.LegacyAuditParityJourneyTests',
            'JET.Tests.Infrastructure.LegacyAuditParityLegacyObservationFactoryTests',
            'JET.Tests.Infrastructure.LegacyAuditParityMetricDependenciesTests',
            'JET.Tests.Infrastructure.LegacyAuditParityObservationFactoryTests',
            'JET.Tests.Infrastructure.LegacyAuditParityObservationsTests',
            'JET.Tests.Infrastructure.LegacyAuditParityProfileTests',
            'JET.Tests.Infrastructure.LegacyWorkbookContentComparisonTests',
            'JET.Tests.Infrastructure.LegacyWorkbookHeaderCatalogTests')) {
        $probeIdentity = "$safeLegacyOracleClass.AnyMethod"
        Assert-Contract -Condition (@(
                $protectedWorkspacePatterns | Where-Object { $probeIdentity -clike [string]$_ }
            ).Count -eq 0) `
            -Message "$safeLegacyOracleClass must remain in the public synthetic test set."
    }
    foreach ($retiredLegacyOraclePath in @(
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityCrossCaseContent.cs',
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityCrossCaseContentDecisionCatalog.cs',
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityCrossCaseContentTests.cs',
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityStage8ContentFingerprint.cs',
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityStage8ContentFingerprintTests.cs',
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityStage9AppearanceDecisionCatalog.cs',
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityStage9AppearanceDecisionCatalogTests.cs',
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityTbModeAggregateEvidence.cs',
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityTbModeAggregateEvidenceTests.cs',
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityTbModeDecisions.cs',
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityTbModeDecisionsTests.cs',
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityBatch.cs',
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityBatchTests.cs',
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityFinalRegistry.cs',
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityFinalRegistryTests.cs',
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityScenarioRefresh.cs',
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityScenarioRefreshBatch.cs',
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityScenarioRefreshJourney.cs',
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityScenarioRefreshTests.cs',
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityScenarioShapeArtifact.cs',
            'src/JET/tests/JET.Tests/Infrastructure/LegacyAuditParityScenarioShapeArtifactTests.cs')) {
        Assert-Contract -Condition (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $retiredLegacyOraclePath))) `
            -Message "$retiredLegacyOraclePath must not return as a new PrivateCase dependency."
    }
    $scenarioNames.Add('LegacyOracleBoundary')

    $toolDirectories = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tools') -Directory)
    Assert-Contract -Condition ((@($toolDirectories.Name | Sort-Object) -join ',') -ceq 'harness,tests') `
        -Message 'The tools root must contain only the harness implementation and its tests.'
    $harnessDirectories = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tools/harness') -Directory)
    $invalidHarnessDirectoryNames = @(
        $harnessDirectories | Where-Object { $_.Name -cnotmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$' }
    )
    Assert-Contract -Condition ($invalidHarnessDirectoryNames.Count -eq 0) `
        -Message 'Harness subdirectory names must use lowercase kebab-case.'

    $jetConvergePaths = @(
        '.agents/harness/convergence-and-memory.md',
        '.agents/skills/jet-converge/SKILL.md',
        '.agents/skills/jet-converge/agents/openai.yaml',
        '.agents/skills/jet-converge/references/session-synthesis.md',
        '.agents/skills/jet-converge/references/upstream-provenance.md',
        '.claude/skills/jet-converge/SKILL.md'
    )
    foreach ($jetConvergePath in $jetConvergePaths) {
        Assert-Contract -Condition (Test-Path -LiteralPath (Join-Path $repositoryRoot $jetConvergePath) -PathType Leaf) `
            -Message "$jetConvergePath must exist for the reviewed convergence boundary."
    }
    $jetConvergeSkillText = Get-Content -LiteralPath (Join-Path $repositoryRoot `
        '.agents/skills/jet-converge/SKILL.md') -Raw -Encoding utf8
    $jetConvergePolicyText = Get-Content -LiteralPath (Join-Path $repositoryRoot `
        '.agents/skills/jet-converge/agents/openai.yaml') -Raw -Encoding utf8
    $jetConvergeHarnessText = Get-Content -LiteralPath (Join-Path $repositoryRoot `
        '.agents/harness/convergence-and-memory.md') -Raw -Encoding utf8
    $jetConvergeClaudeText = Get-Content -LiteralPath (Join-Path $repositoryRoot `
        '.claude/skills/jet-converge/SKILL.md') -Raw -Encoding utf8
    $agentsText = Get-Content -LiteralPath (Join-Path $repositoryRoot 'AGENTS.md') -Raw -Encoding utf8
    Assert-Contract -Condition ($jetConvergeSkillText.Contains('name: jet-converge', [StringComparison]::Ordinal) -and
        $jetConvergeSkillText.Contains('.agents/harness/convergence-and-memory.md', [StringComparison]::Ordinal) -and
        $jetConvergeSkillText.Contains('docs/project-context.md', [StringComparison]::Ordinal) -and
        $jetConvergeSkillText.Contains('docs/idea-replacement-scope.md', [StringComparison]::Ordinal)) `
        -Message 'The canonical skill must retain its name, memory harness, and core-business anchors.'
    Assert-Contract -Condition ($jetConvergePolicyText.Contains('allow_implicit_invocation: false', [StringComparison]::Ordinal) -and
        $jetConvergePolicyText.Contains('Use $jet-converge', [StringComparison]::Ordinal)) `
        -Message 'jet-converge must remain explicit-only and expose a usable Codex prompt.'
    Assert-Contract -Condition ($jetConvergeClaudeText.Contains('disable-model-invocation: true', [StringComparison]::Ordinal) -and
        $jetConvergeClaudeText.Contains('.agents/skills/jet-converge/SKILL.md', [StringComparison]::Ordinal)) `
        -Message 'The Claude adapter must stay explicit-only and point to the canonical skill.'
    Assert-Contract -Condition ($jetConvergeHarnessText.Contains('repository 是專案記憶', [StringComparison]::Ordinal) -and
        $jetConvergeHarnessText.Contains('已知但延後', [StringComparison]::Ordinal) -and
        $jetConvergeHarnessText.Contains('重啟條件', [StringComparison]::Ordinal)) `
        -Message 'The convergence harness must retain repository authority and deferred-item restart conditions.'
    Assert-Contract -Condition ($agentsText.Contains('.agents/skills/jet-converge/SKILL.md', [StringComparison]::Ordinal) -and
        $agentsText.Contains('.agents/harness/convergence-and-memory.md', [StringComparison]::Ordinal)) `
        -Message 'AGENTS.md must route explicit convergence and cross-session memory to the reviewed files.'
    Assert-Contract -Condition (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot '.reversa')) -and
        -not (Test-Path -LiteralPath (Join-Path $repositoryRoot '_reversa_sdd'))) `
        -Message 'External Reversa state roots must not become a second JET memory system.'
    $scenarioNames.Add('JetConvergeBoundary')

    Assert-Contract -Condition (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot 'tools/ExcelAcceptanceDriver'))) `
        -Message 'The old PascalCase Excel acceptance driver directory must not return.'
    $excelDriverRoot = Join-Path $repositoryRoot 'tools/harness/excel-driver'
    $excelDriverFiles = @(
        'DriverContracts.cs',
        'DriverManifest.cs',
        'ExcelAutomation.cs',
        'ExcelDriver.csproj',
        'ExcelProcessCatalog.cs',
        'ExcelRunner.cs',
        'OwnedExcelRun.cs',
        'PdfStructureInspector.cs',
        'Program.cs'
    )
    Assert-Contract -Condition ((@(
                Get-ChildItem -LiteralPath $excelDriverRoot -File |
                    Select-Object -ExpandProperty Name |
                    Sort-Object
            ) -join ',') -ceq ((@($excelDriverFiles | Sort-Object)) -join ',')) `
        -Message 'The Excel driver source inventory must contain only the reviewed Phase 5 files.'
    $excelProjectText = Get-Content -LiteralPath (Join-Path $excelDriverRoot 'ExcelDriver.csproj') -Raw -Encoding utf8
    Assert-Contract -Condition (-not $excelProjectText.Contains('PackageReference', [StringComparison]::Ordinal)) `
        -Message 'The Excel driver must not add third-party packages.'
    $excelSourceText = @(
        Get-ChildItem -LiteralPath $excelDriverRoot -Filter '*.cs' -File |
            Sort-Object Name |
            ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw -Encoding utf8 }
    ) -join "`n"
    foreach ($requiredExcelContract in @(
            'CalculateFullRebuild',
            'SaveCopyAs',
            'LinkSources',
            'AutomationSecurity',
            'GetWindowThreadProcessId',
            'ApplicationHwnd',
            'baseline.Processes.ContainsKey',
            'sourceWorkbookUnchanged',
            'savedCopyReopened',
            'pdfParseable')) {
        Assert-Contract -Condition ($excelSourceText.Contains($requiredExcelContract, [StringComparison]::OrdinalIgnoreCase)) `
            -Message "The Excel driver is missing the reviewed contract: $requiredExcelContract"
    }
    foreach ($fixedArgument in @('--owned-root', '--run-id', '--manifest', '--report-kind', '--timeout-seconds')) {
        Assert-Contract -Condition ($excelSourceText.Contains($fixedArgument, [StringComparison]::Ordinal)) `
            -Message "The Excel driver is missing the fixed argument: $fixedArgument"
    }
    Assert-Contract -Condition ($excelSourceText.Contains('args.Length != 10', [StringComparison]::Ordinal)) `
        -Message 'The Excel driver must accept exactly five name/value argument pairs.'
    Assert-Contract -Condition (-not $excelSourceText.Contains('--workbook', [StringComparison]::OrdinalIgnoreCase)) `
        -Message 'The Excel driver must not accept an arbitrary workbook path.'
    Assert-Contract -Condition ($excelSourceText -notmatch 'CountExistingExcelProcesses|ExistingExcelProcess') `
        -Message 'The Excel lane must not require the user to close pre-existing Excel processes.'
    Assert-Contract -Condition ($excelSourceText.Contains('PreExistingProcessesAllowed { get; init; } = true', [StringComparison]::Ordinal)) `
        -Message 'The Excel receipt must state that pre-existing Excel processes are allowed.'
    Assert-Contract -Condition ($excelSourceText.Contains('CleanupAutomation(automation, ownedIdentity is not null)', [StringComparison]::Ordinal) -and
        $excelSourceText.Contains('if (ownershipVerified)', [StringComparison]::Ordinal)) `
        -Message 'The Excel driver may request Quit only after exact process ownership is proven.'
    $excelAutomationText = Get-Content -LiteralPath (Join-Path $excelDriverRoot 'ExcelAutomation.cs') -Raw -Encoding utf8
    $disposeMatch = [regex]::Match(
        $excelAutomationText,
        'public void Dispose\(\)\s*\{(?<body>.*?)\r?\n\s*\}\r?\n\r?\n\s*private int CountLinks',
        [Text.RegularExpressions.RegexOptions]::Singleline)
    Assert-Contract -Condition ($disposeMatch.Success -and
        -not $disposeMatch.Groups['body'].Value.Contains('Quit(', [StringComparison]::Ordinal)) `
        -Message 'Releasing COM references must not implicitly quit an unverified Excel process.'
    Assert-Contract -Condition ($excelSourceText -notmatch 'data[/\\](test-case|temporary-test-case|legacy-parity-work)') `
        -Message 'The Phase 5 Excel driver cannot reference private data roots.'
    $harnessSourceText = Get-Content -LiteralPath (Join-Path $repositoryRoot 'tools/harness/JetHarness.psm1') -Raw -Encoding utf8
    $processRunnerSourceText = Get-Content -LiteralPath (Join-Path $repositoryRoot 'tools/harness/JetHarness.Process.cs') -Raw -Encoding utf8
    Assert-Contract -Condition ($harnessSourceText.Contains('EnvironmentVariablesToSet', [StringComparison]::OrdinalIgnoreCase) -and
        $processRunnerSourceText.Contains('EnvironmentVariablesToSet', [StringComparison]::OrdinalIgnoreCase)) `
        -Message 'The runner must inject the one synthetic fixture root without inheriting private roots.'
    Assert-Contract -Condition ($harnessSourceText.Contains(
            'jet-harness-release-candidate-workspace',
            [StringComparison]::Ordinal) -and
        $harnessSourceText.Contains('Remove-JetReleaseCandidateWorkspace', [StringComparison]::Ordinal) -and
        $harnessSourceText.Contains('release-candidate-snapshot-cleanup', [StringComparison]::Ordinal)) `
        -Message 'ReleaseCandidate must own, verify, and remove its short candidate workspace.'
    $excelHarnessStart = $harnessSourceText.IndexOf(
        'function Invoke-JetExcelScenarioStep',
        [StringComparison]::Ordinal)
    Assert-Contract -Condition ($excelHarnessStart -ge 0) `
        -Message 'The Excel scenario verifier is missing.'
    $excelHarnessText = $harnessSourceText.Substring($excelHarnessStart)
    Assert-Contract -Condition ($excelHarnessText.Contains(
            '([bool]$manifest.process.killAttempted -and -not [bool]$manifest.process.killSucceeded)',
            [StringComparison]::Ordinal)) `
        -Message 'A successful exact-PID cleanup fallback may pass, but a failed fallback must not.'
    $guiHarnessStart = $harnessSourceText.IndexOf(
        'function Invoke-JetGuiScenarioStep',
        [StringComparison]::Ordinal)
    Assert-Contract -Condition ($guiHarnessStart -ge 0 -and $guiHarnessStart -lt $excelHarnessStart) `
        -Message 'The GUI scenario verifier must remain separate from the Excel verifier.'
    $guiHarnessText = $harnessSourceText.Substring(
        $guiHarnessStart,
        $excelHarnessStart - $guiHarnessStart)
    foreach ($requiredAssertion in @('modifiedOutsideVisible', 'workpaperExportEnabled', 'cleanupPanelAbsent')) {
        Assert-Contract -Condition ($guiHarnessText.Contains(
            ('[bool]$manifest.assertions.' + $requiredAssertion), [StringComparison]::Ordinal)) `
            -Message "GUI verification must require the current report workflow assertion $requiredAssertion."
    }
    Assert-Contract -Condition ($guiHarnessText.Contains(
            '[bool]$manifest.process.killAttempted -or',
            [StringComparison]::Ordinal)) `
        -Message 'A GUI pass must still require graceful process exit without a kill fallback.'
    foreach ($ioScript in @(
            'tools/harness/JetHarness.psm1',
            'tools/harness/JetPackageVerifier.ps1',
            'tools/harness/JetDocumentationCheck.ps1')) {
        $ioScriptText = Get-Content -LiteralPath (Join-Path $repositoryRoot $ioScript) -Raw -Encoding utf8
        Assert-Contract -Condition ($ioScriptText.Contains('using namespace System.IO', [StringComparison]::Ordinal)) `
            -Message "$ioScript must resolve its System.IO exception types."
    }
    $scenarioNames.Add('ExcelBoundary')

    Assert-Contract -Condition (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot 'tools/harness/gui-smoke-driver'))) `
        -Message 'The obsolete scenario-specific GUI driver directory must not return.'
    $guiDriverRoot = Join-Path $repositoryRoot 'tools/harness/gui-driver'
    $guiDriverFiles = @(
        'GuiDriver.csproj',
        'DriverContracts.cs',
        'CdpSession.cs',
        'OwnedGuiRun.cs',
        'GuiRunner.cs',
        'GuiScenarios.cs',
        'Program.cs'
    )
    foreach ($guiDriverFile in $guiDriverFiles) {
        Assert-Contract -Condition (Test-Path -LiteralPath (Join-Path $guiDriverRoot $guiDriverFile) -PathType Leaf) `
            -Message "GUI driver file is missing: $guiDriverFile"
    }
    $guiProjectText = Get-Content -LiteralPath (Join-Path $guiDriverRoot 'GuiDriver.csproj') -Raw -Encoding utf8
    Assert-Contract -Condition (-not $guiProjectText.Contains('PackageReference', [StringComparison]::Ordinal)) `
        -Message 'The GUI driver must not add browser-driver packages.'
    $guiSourceText = @(
        Get-ChildItem -LiteralPath $guiDriverRoot -Filter '*.cs' -File |
            Sort-Object Name |
            ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw -Encoding utf8 }
    ) -join "`n"
    Assert-Contract -Condition ($guiSourceText -notmatch 'OpenQA\.Selenium|msedgedriver|EdgeDriver') `
        -Message 'The GUI driver must not depend on Selenium or EdgeDriver.'
    Assert-Contract -Condition ($guiSourceText.Contains('https://app.jet.local/index.html', [StringComparison]::Ordinal)) `
        -Message 'The GUI driver must target the fixed JET virtual host.'
    Assert-Contract -Condition ($guiSourceText.Contains('IPAddress.Loopback', [StringComparison]::Ordinal)) `
        -Message 'The GUI driver must keep DevTools communication on the loopback interface.'
    Assert-Contract -Condition ($guiSourceText.Contains('"mouseReleased", x, y, "left", 0, 1', [StringComparison]::Ordinal)) `
        -Message 'The closed GUI click must release the left button with no buttons held.'
    Assert-Contract -Condition ($guiSourceText.Contains('"Input.dispatchKeyEvent"', [StringComparison]::Ordinal)) `
        -Message 'The synthetic create scenario must enter form values through closed keyboard events.'
    Assert-Contract -Condition (-not $guiSourceText.Contains('"Input.insertText"', [StringComparison]::Ordinal)) `
        -Message 'The synthetic create scenario must not replace keyboard entry with text injection.'
    Assert-Contract -Condition ($guiSourceText.Contains('[data-action="picker-new"]', [StringComparison]::Ordinal) -and
        $guiSourceText.Contains('[data-bind="create-form"]', [StringComparison]::Ordinal)) `
        -Message 'The synthetic create scenario must use the visible project picker and create form.'
    Assert-Contract -Condition ($guiSourceText -notmatch 'JetApi\.projectCreate|"project\.create"') `
        -Message 'The GUI driver must not bypass the visible form with a direct create action.'
    Assert-Contract -Condition ($guiSourceText -notmatch 'data[/\\](test-case|temporary-test-case|legacy-parity-work)') `
        -Message 'The GUI driver cannot reference private data roots.'
    $scenarioNames.Add('GuiBoundary')

    $forbiddenCoreReferences = Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src/JET/tests/JET.Tests') `
        -Filter '*.cs' -Recurse -File |
        Select-String -Pattern 'tools[/\\]verify\.ps1|artifacts[/\\]harness|\.agents[/\\]|JET_HARNESS_' -CaseSensitive
    Assert-Contract -Condition (@($forbiddenCoreReferences).Count -eq 0) `
        -Message 'Core JET.Tests must not reverse-depend on the new harness.'

    $help = Invoke-Runner -Label 'help' -Arguments @('-Command', 'Help')
    $scenarioNames.Add('Help')
    Assert-Contract -Condition ($help.NativeExitCode -eq 0) -Message 'Help native exit code must be 0.'
    Assert-Contract -Condition ($help.Envelope.status -ceq 'passed') -Message 'Help must pass.'
    Assert-Contract -Condition ((@($help.Envelope.enabledLanes | Sort-Object) -join ',') -ceq 'Excel,Focused,Foundation,Gui,Package,PrivateCase,Provider,Public,ReleaseCandidate') `
        -Message 'Help must expose all nine enabled lanes through Phase 7.'
    Assert-Contract -Condition ([string]::IsNullOrEmpty($help.Stderr)) -Message 'Help must keep stderr empty.'
    Assert-Contract -Condition (@($help.Envelope.commands) -ccontains 'Documentation') `
        -Message 'Help must expose the Documentation command.'
    Assert-Contract -Condition (@($help.Envelope.commands) -ccontains 'Gui') `
        -Message 'Help must expose the Gui command.'
    Assert-Contract -Condition (@($help.Envelope.commands) -ccontains 'Excel') `
        -Message 'Help must expose the Excel command.'
    Assert-Contract -Condition (@($help.Envelope.commands) -ccontains 'PrivateCase') `
        -Message 'Help must expose the PrivateCase command.'
    Assert-Contract -Condition (@($help.Envelope.commands) -ccontains 'ReleaseCandidate') `
        -Message 'Help must expose the ReleaseCandidate command.'

    $excelWrongConfiguration = Invoke-Runner -Label 'excel-wrong-configuration' -Arguments @(
        '-Command', 'Excel',
        '-Configuration', 'Debug',
        '-TimeoutSeconds', '210')
    $scenarioNames.Add('ExcelUsageBoundary')
    Assert-Contract -Condition ($excelWrongConfiguration.NativeExitCode -eq 3 -and
        $excelWrongConfiguration.Envelope.status -ceq 'usage_error') `
        -Message 'Excel must reject any configuration other than Release before native automation starts.'
    $excelShortTimeout = Invoke-Runner -Label 'excel-short-timeout' -Arguments @(
        '-Command', 'Excel',
        '-Configuration', 'Release',
        '-TimeoutSeconds', '209')
    Assert-Contract -Condition ($excelShortTimeout.NativeExitCode -eq 3 -and
        $excelShortTimeout.Envelope.status -ceq 'usage_error') `
        -Message 'Excel must reject a timeout shorter than the driver and cleanup bound.'

    $documentation = Invoke-Runner -Label 'documentation' -Arguments @(
        '-Command', 'Documentation',
        '-TimeoutSeconds', '30',
        '-EvidenceRoot', $runsRelativeRoot)
    $scenarioNames.Add('Documentation')
    Assert-Contract -Condition ($documentation.NativeExitCode -eq 0) `
        -Message 'The current documentation check must pass.'
    $documentationReceipt = Get-Receipt -Run $documentation
    Assert-CommonReceipt -Receipt $documentationReceipt -ExpectedStatus 'passed'
    Assert-Contract -Condition ($documentationReceipt.steps.Count -eq 1) `
        -Message 'Documentation must run exactly one bounded check.'
    Assert-Contract -Condition ($documentationReceipt.steps[0].documentation.manualReviewRequired -eq $true) `
        -Message 'Automated wording checks cannot replace human review.'
    Assert-Contract -Condition (
        $documentationReceipt.steps[0].documentation.counters.filesChecked -eq
        $documentationReceipt.steps[0].documentation.counters.filesDeclared) `
        -Message 'Documentation must inspect every declared current file.'

    $documentationFixtureRoot = Join-Path $suiteRoot 'documentation-fixture'
    [void][IO.Directory]::CreateDirectory($documentationFixtureRoot)
    $syntheticForbiddenClaim = 'contract-generated-forbidden-claim'
    $syntheticStyleWarning = 'contract-generated-style-warning'
    $syntheticRequiredMarker = 'contract-generated-required-marker'
    [IO.File]::WriteAllText(
        (Join-Path $documentationFixtureRoot 'README.md'),
        "$syntheticRequiredMarker and $syntheticForbiddenClaim.`n",
        $utf8)
    $documentationPolicyPath = Join-Path $documentationFixtureRoot 'documentation-check.json'
    $documentationReportPath = Join-Path $documentationFixtureRoot 'documentation-forbidden-report.json'
    $documentationPolicy = [ordered]@{
        schemaVersion = 2
        files = @('README.md')
        forbiddenClaims = @([ordered]@{ id = 'synthetic-forbidden'; text = $syntheticForbiddenClaim })
        styleWarnings = @([ordered]@{ id = 'synthetic-style'; text = $syntheticStyleWarning })
        requiredMarkers = @([ordered]@{
            id = 'synthetic-marker'
            file = 'README.md'
            text = $syntheticRequiredMarker
        })
        manualReview = @('Read the changed paragraph once.')
    }
    [IO.File]::WriteAllText(
        $documentationPolicyPath,
        ($documentationPolicy | ConvertTo-Json -Depth 8) + "`n",
        $utf8)
    $documentationFixture = Invoke-DocumentationFixture `
        -FixtureRoot $documentationFixtureRoot `
        -ReportPath $documentationReportPath `
        -PolicyPath $documentationPolicyPath `
        -Label 'documentation-forbidden-claim'
    Assert-Contract -Condition ($documentationFixture.NativeExitCode -eq 1) `
        -Message 'A confirmed false claim must fail the documentation check.'
    Assert-Contract -Condition ($documentationFixture.Report.status -ceq 'failed') `
        -Message 'A confirmed false claim must be reported as failed.'
    Assert-Contract -Condition ($documentationFixture.Report.errors[0].code -ceq 'forbidden_claim') `
        -Message 'The documentation report must retain the false-claim classification.'
    Assert-Contract -Condition (-not (($documentationFixture.Stdout + $documentationFixture.Stderr +
                $documentationFixture.ReportText).Contains($syntheticForbiddenClaim, [StringComparison]::Ordinal))) `
        -Message 'Documentation evidence must identify a factual rule without copying its matched text.'

    [IO.File]::WriteAllText(
        (Join-Path $documentationFixtureRoot 'README.md'),
        "$syntheticRequiredMarker and $syntheticStyleWarning.`n",
        $utf8)
    $styleReportPath = Join-Path $documentationFixtureRoot 'documentation-style-report.json'
    $styleFixture = Invoke-DocumentationFixture `
        -FixtureRoot $documentationFixtureRoot `
        -ReportPath $styleReportPath `
        -PolicyPath $documentationPolicyPath `
        -Label 'documentation-style-warning'
    Assert-Contract -Condition ($styleFixture.NativeExitCode -eq 0 -and
        $styleFixture.Report.status -ceq 'passed' -and
        [int]$styleFixture.Report.counters.errors -eq 0 -and
        [int]$styleFixture.Report.counters.warnings -eq 1) `
        -Message 'A style phrase must warn without failing the documentation check.'
    Assert-Contract -Condition ($styleFixture.Report.warnings[0].code -ceq 'style_warning') `
        -Message 'The documentation report must retain the style-warning classification.'
    Assert-Contract -Condition (-not (($styleFixture.Stdout + $styleFixture.Stderr +
                $styleFixture.ReportText).Contains($syntheticStyleWarning, [StringComparison]::Ordinal))) `
        -Message 'Documentation evidence must identify a style rule without copying its matched text.'

    [IO.File]::WriteAllText(
        (Join-Path $documentationFixtureRoot 'README.md'),
        "A document without the required adapter marker.`n",
        $utf8)
    $markerReportPath = Join-Path $documentationFixtureRoot 'documentation-marker-report.json'
    $markerFixture = Invoke-DocumentationFixture `
        -FixtureRoot $documentationFixtureRoot `
        -ReportPath $markerReportPath `
        -PolicyPath $documentationPolicyPath `
        -Label 'documentation-required-marker'
    Assert-Contract -Condition ($markerFixture.NativeExitCode -eq 1 -and
        $markerFixture.Report.status -ceq 'failed' -and
        $markerFixture.Report.errors[0].code -ceq 'required_marker_missing') `
        -Message 'A missing agent adapter marker must fail the documentation check.'

    $normal = Invoke-Runner -Label 'normal' -Arguments @(
        '-Command', 'Contract',
        '-ContractScenario', 'Normal',
        '-TimeoutSeconds', '30',
        '-EvidenceRoot', $runsRelativeRoot)
    $scenarioNames.Add('Normal')
    Assert-Contract -Condition ($normal.NativeExitCode -eq 0) -Message 'Normal native exit code must be 0.'
    Assert-Contract -Condition ($normal.Envelope.ok -eq $true) -Message 'Normal envelope must be ok.'
    Assert-Contract -Condition ([string]::IsNullOrEmpty($normal.Stderr)) -Message 'Normal must keep runner stderr empty.'
    $normalReceipt = Get-Receipt -Run $normal
    Assert-CommonReceipt -Receipt $normalReceipt -ExpectedStatus 'passed'
    Assert-Contract -Condition ($normalReceipt.lock.acquired -eq $true) -Message 'Normal Contract must acquire the lock.'
    Assert-Contract -Condition ($normalReceipt.lock.cleanup.released -eq $true) -Message 'Normal Contract must release the lock.'
    Assert-Contract -Condition ($null -eq $normalReceipt.firstRed) -Message 'Normal Contract cannot have firstRed.'
    $normalStdoutPath = Join-Path $repositoryRoot ([string]$normalReceipt.steps[0].stdout.path)
    $normalStdout = Get-Content -LiteralPath $normalStdoutPath -Raw -Encoding utf8
    Assert-Contract -Condition ($normalReceipt.steps[0].stdout.sourceCodePage -eq 65001) `
        -Message 'The UTF-8 contract probe must record its source code page.'
    Assert-Contract -Condition ($normalStdout.Contains('[repository]', [StringComparison]::Ordinal)) `
        -Message 'Captured output must replace the repository root with a stable marker.'
    Assert-Contract -Condition (-not $normalStdout.Contains($repositoryRoot, [StringComparison]::OrdinalIgnoreCase)) `
        -Message 'Captured output must not retain the absolute repository root.'
    $userProfileDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    Assert-Contract -Condition (-not $normalStdout.Contains($userProfileDirectory, [StringComparison]::OrdinalIgnoreCase)) `
        -Message 'Captured output must not retain the user profile path.'

    $sensitiveOutput = Invoke-Runner -Label 'sensitive-output' -Arguments @(
        '-Command', 'Contract',
        '-ContractScenario', 'SensitiveOutput',
        '-TimeoutSeconds', '30',
        '-EvidenceRoot', $runsRelativeRoot)
    $scenarioNames.Add('SensitiveOutput')
    Assert-Contract -Condition ($sensitiveOutput.NativeExitCode -eq 0) `
        -Message 'Sensitive-output probe must pass.'
    $sensitiveReceipt = Get-Receipt -Run $sensitiveOutput
    Assert-CommonReceipt -Receipt $sensitiveReceipt -ExpectedStatus 'passed'
    foreach ($streamName in @('stdout', 'stderr')) {
        $streamPath = Join-Path $repositoryRoot ([string]$sensitiveReceipt.steps[0].$streamName.path)
        $streamText = Get-Content -LiteralPath $streamPath -Raw -Encoding utf8
        Assert-Contract -Condition (-not $streamText.Contains(
                'JET-HARNESS-CONTRACT-SECRET-20260828',
                [StringComparison]::Ordinal)) `
            -Message "$streamName must remove explicitly declared sensitive values."
        Assert-Contract -Condition ($streamText.Contains('[sensitive]', [StringComparison]::Ordinal)) `
            -Message "$streamName must retain a stable redaction marker."
    }

    $usage = Invoke-Runner -Label 'usage' -Arguments @('-Command', 'NotACommand')
    $scenarioNames.Add('UsageError')
    Assert-Contract -Condition ($usage.NativeExitCode -eq 3) -Message 'Usage native exit code must be 3.'
    Assert-Contract -Condition ($usage.Envelope.status -ceq 'usage_error') -Message 'Unknown command must be usage_error.'
    Assert-Contract -Condition ($usage.Envelope.privateData.pathInspected -eq $false) `
        -Message 'Usage error cannot inspect private data.'

    $focusedWithoutFilter = Invoke-Runner -Label 'usage-focused-filter-required' -Arguments @(
        '-Command', 'Focused')
    Assert-Contract -Condition ($focusedWithoutFilter.NativeExitCode -eq 3) `
        -Message 'Focused without a filter must be a usage error.'
    Assert-Contract -Condition ($focusedWithoutFilter.Envelope.status -ceq 'usage_error') `
        -Message 'Focused without a filter cannot start a test run.'

    $publicWithFilter = Invoke-Runner -Label 'usage-public-filter-rejected' -Arguments @(
        '-Command', 'Public',
        '-Filter', 'ScaffoldTests')
    Assert-Contract -Condition ($publicWithFilter.NativeExitCode -eq 3) `
        -Message 'Public must reject a Focused-only filter.'
    Assert-Contract -Condition ($publicWithFilter.Envelope.status -ceq 'usage_error') `
        -Message 'Public with a filter cannot start a test run.'

    $packageDebug = Invoke-Runner -Label 'usage-package-release-required' -Arguments @(
        '-Command', 'Package',
        '-Configuration', 'Debug')
    Assert-Contract -Condition ($packageDebug.NativeExitCode -eq 3) `
        -Message 'Package must reject Debug before creating a run.'
    Assert-Contract -Condition ($packageDebug.Envelope.status -ceq 'usage_error') `
        -Message 'Package Debug must be a usage error.'

    $guiDebug = Invoke-Runner -Label 'usage-gui-agent-configuration-required' -Arguments @(
        '-Command', 'Gui',
        '-Configuration', 'Debug')
    Assert-Contract -Condition ($guiDebug.NativeExitCode -eq 3) `
        -Message 'Gui must reject Debug before creating a run.'
    Assert-Contract -Condition ($guiDebug.Envelope.status -ceq 'usage_error') `
        -Message 'Gui Debug must be a usage error.'

    $guiShortTimeout = Invoke-Runner -Label 'usage-gui-timeout-required' -Arguments @(
        '-Command', 'Gui',
        '-Configuration', 'AgentGuiTest',
        '-TimeoutSeconds', '149')
    Assert-Contract -Condition ($guiShortTimeout.NativeExitCode -eq 3) `
        -Message 'Gui must reject a timeout shorter than its cleanup allowance.'
    Assert-Contract -Condition ($guiShortTimeout.Envelope.status -ceq 'usage_error') `
        -Message 'Gui short timeout must be a usage error.'

    $guiNoRestore = Invoke-Runner -Label 'usage-gui-no-restore-rejected' -Arguments @(
        '-Command', 'Gui',
        '-Configuration', 'AgentGuiTest',
        '-NoRestore')
    Assert-Contract -Condition ($guiNoRestore.NativeExitCode -eq 3) `
        -Message 'Gui must restore both the application and its driver for each formal run.'
    Assert-Contract -Condition ($guiNoRestore.Envelope.status -ceq 'usage_error') `
        -Message 'Gui NoRestore must be a usage error.'

    $privateVariableNames = @(
        [string]$registry.testSettings.privateCaseRootEnvironmentVariable,
        [string]$registry.testSettings.privateCaseManifestEnvironmentVariable,
        [string]$registry.testSettings.privateCaseProviderEnvironmentVariable)
    $priorPrivateValues = @{}
    try {
        foreach ($variableName in $privateVariableNames) {
            $priorPrivateValues[$variableName] = [Environment]::GetEnvironmentVariable(
                $variableName,
                [EnvironmentVariableTarget]::Process)
            [Environment]::SetEnvironmentVariable(
                $variableName,
                $null,
                [EnvironmentVariableTarget]::Process)
        }
        $privateCaseMissingInput = Invoke-Runner -Label 'private-case-missing-input' -Arguments @(
            '-Command', 'PrivateCase',
            '-NoRestore',
            '-TimeoutSeconds', '30',
            '-EvidenceRoot', $runsRelativeRoot)
    }
    finally {
        foreach ($variableName in $privateVariableNames) {
            [Environment]::SetEnvironmentVariable(
                $variableName,
                $priorPrivateValues[$variableName],
                [EnvironmentVariableTarget]::Process)
        }
    }
    $scenarioNames.Add('PrivateCasePreflight')
    Assert-Contract -Condition ($privateCaseMissingInput.NativeExitCode -eq 2) `
        -Message 'PrivateCase without all three explicit inputs must be blocked.'
    $privateCaseReceipt = Get-Receipt -Run $privateCaseMissingInput
    Assert-CommonReceipt -Receipt $privateCaseReceipt -ExpectedStatus 'blocked'
    Assert-Contract -Condition ($privateCaseReceipt.steps.Count -eq 1) `
        -Message 'Blocked PrivateCase preflight cannot start build or tests.'
    Assert-Contract -Condition ($privateCaseReceipt.firstRed.reason -ceq 'private_case_input_not_configured') `
        -Message 'PrivateCase must retain its missing-input reason.'
    Assert-Contract -Condition ($privateCaseReceipt.privateData.state -ceq 'selected' -and
        $privateCaseReceipt.privateData.pathInspected -eq $false -and
        $privateCaseReceipt.privateData.outputsRetained -eq $false) `
        -Message 'Blocked PrivateCase must record selection without claiming data access or retained output.'
    Assert-Contract -Condition ($privateCaseReceipt.testBoundary.reportComparison -ceq 'content-and-appearance' -and
        $privateCaseReceipt.testBoundary.infAcceptance -ceq 'rules-and-effective-population' -and
        $privateCaseReceipt.testBoundary.cleanupPolicy -ceq 'always-delete') `
        -Message 'PrivateCase receipts must retain the three fixed acceptance decisions.'

    $releaseWrongConfiguration = Invoke-Runner -Label 'release-candidate-release-required' -Arguments @(
        '-Command', 'ReleaseCandidate',
        '-Configuration', 'Debug',
        '-TimeoutSeconds', '210')
    $releaseNoRestore = Invoke-Runner -Label 'release-candidate-no-restore-rejected' -Arguments @(
        '-Command', 'ReleaseCandidate',
        '-Configuration', 'Release',
        '-NoRestore',
        '-TimeoutSeconds', '210')
    $releaseShortTimeout = Invoke-Runner -Label 'release-candidate-timeout-boundary' -Arguments @(
        '-Command', 'ReleaseCandidate',
        '-Configuration', 'Release',
        '-TimeoutSeconds', '209')
    $scenarioNames.Add('ReleaseCandidateUsageBoundary')
    Assert-Contract -Condition ($releaseWrongConfiguration.NativeExitCode -eq 3 -and
        $releaseNoRestore.NativeExitCode -eq 3 -and
        $releaseShortTimeout.NativeExitCode -eq 3) `
        -Message 'ReleaseCandidate must require Release, its own restore plan, and a sufficient per-child timeout.'

    $connectionVariable = [string]$registry.testSettings.providerConnectionEnvironmentVariable
    $priorConnection = [Environment]::GetEnvironmentVariable(
        $connectionVariable,
        [EnvironmentVariableTarget]::Process)
    try {
        [Environment]::SetEnvironmentVariable(
            $connectionVariable,
            $null,
            [EnvironmentVariableTarget]::Process)
        $providerMissingConnection = Invoke-Runner -Label 'provider-missing-connection' -Arguments @(
            '-Command', 'Provider',
            '-NoRestore',
            '-TimeoutSeconds', '30',
            '-EvidenceRoot', $runsRelativeRoot)
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            $connectionVariable,
            $priorConnection,
            [EnvironmentVariableTarget]::Process)
    }
    $scenarioNames.Add('ProviderPreflight')
    Assert-Contract -Condition ($providerMissingConnection.NativeExitCode -eq 2) `
        -Message 'Provider without its explicit connection must be blocked.'
    $providerReceipt = Get-Receipt -Run $providerMissingConnection
    Assert-CommonReceipt -Receipt $providerReceipt -ExpectedStatus 'blocked'
    Assert-Contract -Condition ($providerReceipt.steps.Count -eq 1) `
        -Message 'Blocked Provider preflight cannot start build or tests.'
    Assert-Contract -Condition ($providerReceipt.firstRed.reason -ceq 'sqlserver_connection_not_configured') `
        -Message 'Provider must retain its missing-connection reason.'
    Assert-Contract -Condition ($providerReceipt.testBoundary.connectionValueRecorded -eq $false) `
        -Message 'Provider receipts cannot record the connection value.'

    $packageFixtureRoot = Join-Path $suiteRoot 'package-preflight-fixtures'
    $unsafeSourceRoot = Join-Path $packageFixtureRoot 'unsafe'
    $safeSourceRoot = Join-Path $packageFixtureRoot 'safe'
    [void][IO.Directory]::CreateDirectory($unsafeSourceRoot)
    [void][IO.Directory]::CreateDirectory($safeSourceRoot)
    $syntheticSecret = 'contract-fixture-password'
    [IO.File]::WriteAllText(
        (Join-Path $unsafeSourceRoot 'appsettings.json'),
        "{`"Sql`":{`"Password`":`"$syntheticSecret`"}}`n",
        $utf8)
    [IO.File]::WriteAllText(
        (Join-Path $safeSourceRoot 'appsettings.json'),
        "{`"Sql`":{`"Password`":`"`"}}`n",
        $utf8)
    $unsafeManifestPath = Join-Path $packageFixtureRoot 'unsafe-manifest.json'
    $safeManifestPath = Join-Path $packageFixtureRoot 'safe-manifest.json'
    $unsafePreflight = Invoke-PackagePreflight `
        -SourceRoot $unsafeSourceRoot `
        -ManifestPath $unsafeManifestPath `
        -Label 'package-unsafe'
    $safePreflight = Invoke-PackagePreflight `
        -SourceRoot $safeSourceRoot `
        -ManifestPath $safeManifestPath `
        -Label 'package-safe'
    $scenarioNames.Add('PackageSourcePreflight')
    Assert-Contract -Condition ($unsafePreflight.NativeExitCode -eq 1) `
        -Message 'Tracked synthetic credentials must fail package preflight.'
    Assert-Contract -Condition ($unsafePreflight.Manifest.errors[0].code -ceq 'tracked_credential_present') `
        -Message 'Unsafe package preflight must use the credential classification.'
    Assert-Contract -Condition (-not (($unsafePreflight.Stdout + $unsafePreflight.Stderr + $unsafePreflight.ManifestText).Contains(
                $syntheticSecret,
                [StringComparison]::Ordinal))) `
        -Message 'Package preflight evidence cannot retain a credential value.'
    Assert-Contract -Condition ($safePreflight.NativeExitCode -eq 0) `
        -Message 'An empty password placeholder must pass package source preflight.'
    Assert-Contract -Condition ($safePreflight.Manifest.privateData.pathInspected -eq $false) `
        -Message 'Package source preflight cannot inspect private data.'

    $verificationFixtureRoot = Join-Path $suiteRoot 'package-verification-fixture'
    $verificationSourceRoot = Join-Path $verificationFixtureRoot 'source'
    $verificationOwnedRoot = Join-Path $verificationFixtureRoot 'owned'
    $verificationCandidateRoot = Join-Path $verificationOwnedRoot 'publish'
    [void][IO.Directory]::CreateDirectory((Join-Path $verificationSourceRoot 'wwwroot'))
    [void][IO.Directory]::CreateDirectory($verificationCandidateRoot)
    [IO.File]::WriteAllText(
        (Join-Path $verificationSourceRoot 'appsettings.json'),
        "{`"Sql`":{`"Database`":`"JET`"}}`n",
        $utf8)
    [IO.File]::WriteAllText(
        (Join-Path $verificationSourceRoot 'wwwroot/index.html'),
        "fixture`n",
        $utf8)
    foreach ($templateRelative in @($registry.packageSettings.templateRelativePaths)) {
        $sourceTemplate = Join-Path $verificationSourceRoot ([string]$templateRelative)
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $sourceTemplate))
        [IO.File]::WriteAllBytes($sourceTemplate, [byte[]](1, 2, 3))
    }
    foreach ($sourceFile in @(Get-ChildItem -LiteralPath $verificationSourceRoot -Recurse -File)) {
        $relative = [IO.Path]::GetRelativePath($verificationSourceRoot, $sourceFile.FullName)
        $candidateFile = Join-Path $verificationCandidateRoot $relative
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $candidateFile))
        [IO.File]::Copy($sourceFile.FullName, $candidateFile, $true)
    }
    [IO.File]::WriteAllBytes((Join-Path $verificationCandidateRoot 'JET.exe'), [byte[]](1, 2, 3, 4))
    $verificationManifestPath = Join-Path $verificationFixtureRoot 'manifest.json'
    $verificationFixture = Invoke-PackageVerificationFixture `
        -SourceRoot $verificationSourceRoot `
        -CandidateRoot $verificationCandidateRoot `
        -OwnedRoot $verificationOwnedRoot `
        -ManifestPath $verificationManifestPath `
        -Label 'package-verification-fixture'
    Assert-Contract -Condition ($verificationFixture.NativeExitCode -eq 1) `
        -Message 'A structurally readable but invalid package must fail validation, not the verifier.'
    Assert-Contract -Condition ($verificationFixture.Manifest.status -ceq 'failed') `
        -Message 'An invalid synthetic executable must produce a failed package manifest.'
    Assert-Contract -Condition (@($verificationFixture.Manifest.errors.code) -ccontains 'jet_executable_not_mz') `
        -Message 'The package verifier must reach executable validation.'
    Assert-Contract -Condition (@($verificationFixture.Manifest.errors.code) -cnotcontains 'package_verifier_exception') `
        -Message 'An empty display prefix cannot crash package verification.'
    Assert-Contract -Condition (@($verificationFixture.Manifest.errors.code) -cnotcontains 'source_template_missing') `
        -Message 'The synthetic source must contain every declared package template.'
    Assert-Contract -Condition (@($verificationFixture.Manifest.errors.code) -cnotcontains 'runtime_data_forbidden') `
        -Message 'Declared templates cannot be mistaken for runtime workbooks.'
    Assert-Contract -Condition (@($verificationFixture.Manifest.errors.code) -cnotcontains 'package_file_missing') `
        -Message 'Windows path separators must not make copied package files appear missing.'
    Assert-Contract -Condition (@($verificationFixture.Manifest.errors.code) -cnotcontains 'package_file_extra') `
        -Message 'Windows path separators must not make copied package files appear extra.'

    $sanitationRoot = Join-Path $suiteRoot 'sensitive-test-evidence'
    [void][IO.Directory]::CreateDirectory($sanitationRoot)
    [IO.File]::WriteAllText(
        (Join-Path $sanitationRoot 'result.trx'),
        "<TestRun><Result>$syntheticSecret</Result></TestRun>",
        $utf8)
    [IO.File]::WriteAllText(
        (Join-Path $sanitationRoot 'provider.log'),
        "provider:$syntheticSecret`n",
        $utf8)
    $harnessModule = Import-Module (Join-Path $repositoryRoot 'tools/harness/JetHarness.psm1') -Force -PassThru
    $sanitation = & $harnessModule {
        param($resultsDirectory, $secret)
        Protect-JetSensitiveTestEvidence `
            -ResultsDirectory $resultsDirectory `
            -SensitiveValues @($secret)
    } $sanitationRoot $syntheticSecret
    $scenarioNames.Add('SensitiveTestEvidence')
    Assert-Contract -Condition ($sanitation.applied -eq $true -and $sanitation.files -eq 2) `
        -Message 'Sensitive test-evidence sanitation must cover every declared text artifact.'
    foreach ($file in @(Get-ChildItem -LiteralPath $sanitationRoot -File)) {
        $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding utf8
        Assert-Contract -Condition (-not $text.Contains($syntheticSecret, [StringComparison]::Ordinal)) `
            -Message 'Sanitized test evidence cannot retain the declared secret.'
        Assert-Contract -Condition ($text.Contains('[sensitive]', [StringComparison]::Ordinal)) `
            -Message 'Sanitized test evidence must retain a stable marker.'
    }

    # 失敗收據要能直接看出哪個測試為何失敗；訊息一行、遮蔽儲存庫根目錄，私人案件只留名稱。
    $firstFailureRoot = Join-Path $suiteRoot 'first-failure-evidence'
    [void][IO.Directory]::CreateDirectory($firstFailureRoot)
    $syntheticTrx = @"
<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testId="t-1" testName="Fails" outcome="Failed">
      <Output><ErrorInfo><Message>Assert.Equal() Failure
Expected: 1
Actual: 2 at $repositoryRoot\src\Synthetic.cs</Message></ErrorInfo></Output>
    </UnitTestResult>
    <UnitTestResult testId="t-2" testName="Passes" outcome="Passed" />
  </Results>
  <TestDefinitions>
    <UnitTest id="t-1" name="Fails"><TestMethod className="Synthetic.Suite" name="Fails" /></UnitTest>
    <UnitTest id="t-2" name="Passes"><TestMethod className="Synthetic.Suite" name="Passes" /></UnitTest>
  </TestDefinitions>
</TestRun>
"@
    [IO.File]::WriteAllText((Join-Path $firstFailureRoot 'result.trx'), $syntheticTrx, $utf8)
    $firstFailure = & $harnessModule {
        param($repositoryRoot, $runDirectory)
        Read-JetTrxResult `
            -RepositoryRoot $repositoryRoot `
            -RunDirectory $runDirectory `
            -TrxPath (Join-Path $runDirectory 'result.trx') `
            -SummaryPath (Join-Path $runDirectory 'test-summary.json')
    } $repositoryRoot $firstFailureRoot
    $scenarioNames.Add('FirstFailureEvidence')
    Assert-Contract -Condition ($firstFailure.Receipt.counters.failed -eq 1) `
        -Message 'The synthetic TRX must report exactly one failed test.'
    Assert-Contract -Condition ([string]$firstFailure.FirstFailure.fqn -ceq 'Synthetic.Suite.Fails') `
        -Message 'The first failure must name the failed test by class and method.'
    Assert-Contract -Condition ([string]$firstFailure.FirstFailure.message -ceq
            'Assert.Equal() Failure Expected: 1 Actual: 2 at [repository]\src\Synthetic.cs') `
        -Message 'The first failure message must be one line with the repository root masked.'
    Assert-Contract -Condition ([string]$firstFailure.Receipt.firstFailure.fqn -ceq 'Synthetic.Suite.Fails') `
        -Message 'The test receipt must carry the first failure.'
    $withheldFailure = & $harnessModule {
        param($repositoryRoot, $runDirectory)
        Read-JetTrxResult `
            -RepositoryRoot $repositoryRoot `
            -RunDirectory $runDirectory `
            -TrxPath (Join-Path $runDirectory 'result.trx') `
            -SummaryPath (Join-Path $runDirectory 'test-summary.json') `
            -WithholdFailureMessages
    } $repositoryRoot $firstFailureRoot
    Assert-Contract -Condition ($null -eq $withheldFailure.FirstFailure.message -and
            $withheldFailure.FirstFailure.messageWithheld -eq $true -and
            [string]$withheldFailure.FirstFailure.fqn -ceq 'Synthetic.Suite.Fails') `
        -Message 'Private-case failure messages must be withheld while the test name stays.'

    # 斷言變動只警示不判定：相對 HEAD 移除的 Assert、移除的 [Fact]/[Theory] 與新增的 Skip 都要數出來。
    $driftRepo = Join-Path $suiteRoot 'assertion-drift-repo'
    [void][IO.Directory]::CreateDirectory((Join-Path $driftRepo 'src/JET/tests'))
    $driftFile = Join-Path $driftRepo 'src/JET/tests/Synthetic.cs'
    [IO.File]::WriteAllText(
        $driftFile,
        "[Fact]`npublic void A()`n{`n    Assert.Equal(1, 1);`n    Assert.True(true);`n}`n",
        $utf8)
    & git -C $driftRepo init -q 2>$null
    & git -C $driftRepo add -- src/JET/tests/Synthetic.cs 2>$null
    & git -C $driftRepo -c user.name=jet -c user.email=jet@example.invalid commit -q -m init 2>$null
    [IO.File]::WriteAllText(
        $driftFile,
        "[Fact(Skip = `"synthetic`")]`npublic void A()`n{`n    Assert.True(true);`n}`n",
        $utf8)
    $drift = & $harnessModule { param($root) Get-JetTestAssertionDrift -RepositoryRoot $root } $driftRepo
    Remove-Item -LiteralPath (Join-Path $driftRepo '.git') -Recurse -Force -ErrorAction SilentlyContinue
    $scenarioNames.Add('AssertionDrift')
    Assert-Contract -Condition ($drift.measured -eq $true -and $drift.assertionsRemoved -eq 1 -and
            $drift.skipsAdded -eq 1 -and $drift.needsExplanation -eq $true) `
        -Message 'Assertion drift must count removed assertions and added skips against HEAD.'
    Assert-Contract -Condition (@($drift.files) -ccontains 'src/JET/tests/Synthetic.cs') `
        -Message 'Assertion drift must name the changed test file.'

    $unsupportedEvidenceRoot = Join-Path $suiteRoot 'unsupported-sensitive-evidence'
    [void][IO.Directory]::CreateDirectory($unsupportedEvidenceRoot)
    [IO.File]::WriteAllBytes((Join-Path $unsupportedEvidenceRoot 'attachment.bin'), [byte[]](1, 2, 3))
    $unsupportedRejected = $false
    try {
        & $harnessModule {
            param($resultsDirectory, $secret)
            Protect-JetSensitiveTestEvidence `
                -ResultsDirectory $resultsDirectory `
                -SensitiveValues @($secret)
        } $unsupportedEvidenceRoot $syntheticSecret
    }
    catch {
        $unsupportedRejected = $true
    }
    Assert-Contract -Condition $unsupportedRejected `
        -Message 'Unknown sensitive-test attachments must fail closed.'

    $emptySanitationRoot = Join-Path $suiteRoot 'empty-sensitive-evidence'
    [void][IO.Directory]::CreateDirectory($emptySanitationRoot)
    $emptySanitation = & $harnessModule {
        param($resultsDirectory)
        [string[]]$noSensitiveValues = @()
        Protect-JetSensitiveTestEvidence `
            -ResultsDirectory $resultsDirectory `
            -SensitiveValues $noSensitiveValues
    } $emptySanitationRoot
    Assert-Contract -Condition ($emptySanitation.applied -eq $false -and $emptySanitation.files -eq 0) `
        -Message 'Ordinary tests must accept an explicitly empty sensitive-value set.'

    $unusedWait = Invoke-Runner -Label 'usage-unused-wait' -Arguments @(
        '-Command', 'Build',
        '-WaitSeconds', '1')
    Assert-Contract -Condition ($unusedWait.NativeExitCode -eq 3) `
        -Message 'A non-locking command must reject a non-zero WaitSeconds value.'
    Assert-Contract -Condition ($unusedWait.Envelope.status -ceq 'usage_error') `
        -Message 'An unused WaitSeconds value must be a usage error.'

    $escape = Invoke-Runner -Label 'evidence-escape' -Arguments @(
        '-Command', 'Contract',
        '-EvidenceRoot', '../outside-harness')
    $scenarioNames.Add('EvidenceContainment')
    Assert-Contract -Condition ($escape.NativeExitCode -eq 3) -Message 'Evidence escape must be usage error.'
    Assert-Contract -Condition ($escape.Envelope.status -ceq 'usage_error') -Message 'Evidence escape must fail closed.'

    $childFailure = Invoke-Runner -Label 'child-failure' -Arguments @(
        '-Command', 'Contract',
        '-ContractScenario', 'ChildFailure',
        '-TimeoutSeconds', '30',
        '-EvidenceRoot', $runsRelativeRoot)
    $scenarioNames.Add('ChildFailure')
    Assert-Contract -Condition ($childFailure.NativeExitCode -eq 1) -Message 'Child failure native exit code must be 1.'
    $childFailureReceipt = Get-Receipt -Run $childFailure
    Assert-CommonReceipt -Receipt $childFailureReceipt -ExpectedStatus 'failed'
    Assert-Contract -Condition ($childFailureReceipt.firstRed.name -ceq 'contract-probe') `
        -Message 'Child failure must retain the contract probe as firstRed.'
    Assert-Contract -Condition ($childFailureReceipt.firstRed.exitCode -eq 23) `
        -Message 'Child failure must retain the original child exit code.'

    $nugetUnavailable = Invoke-Runner -Label 'nuget-unavailable' -Arguments @(
        '-Command', 'Contract',
        '-ContractScenario', 'NuGetUnavailable',
        '-TimeoutSeconds', '30',
        '-EvidenceRoot', $runsRelativeRoot)
    $scenarioNames.Add('NuGetUnavailable')
    Assert-Contract -Condition ($nugetUnavailable.NativeExitCode -eq 2) `
        -Message 'An unavailable NuGet source must use the blocked exit code.'
    $nugetUnavailableReceipt = Get-Receipt -Run $nugetUnavailable
    Assert-CommonReceipt -Receipt $nugetUnavailableReceipt -ExpectedStatus 'blocked'
    Assert-Contract -Condition ($nugetUnavailableReceipt.steps[0].classification.reason -ceq 'nuget_source_unavailable') `
        -Message 'The blocked step must retain the NuGet classification.'
    Assert-Contract -Condition ($nugetUnavailableReceipt.error.code -ceq 'nuget_source_unavailable') `
        -Message 'The run must expose the NuGet blocked reason.'

    $timeout = Invoke-Runner -Label 'timeout' -Arguments @(
        '-Command', 'Contract',
        '-ContractScenario', 'Timeout',
        '-ProbeSeconds', '3',
        '-TimeoutSeconds', '10',
        '-EvidenceRoot', $runsRelativeRoot)
    $scenarioNames.Add('Timeout')
    Assert-Contract -Condition ($timeout.NativeExitCode -eq 2) -Message 'Timeout native exit code must be 2.'
    $timeoutReceipt = Get-Receipt -Run $timeout
    Assert-CommonReceipt -Receipt $timeoutReceipt -ExpectedStatus 'blocked'
    Assert-Contract -Condition ($timeoutReceipt.steps[0].process.timedOut -eq $true) `
        -Message 'Timeout must be recorded as timedOut.'
    Assert-Contract -Condition ($timeoutReceipt.steps[0].process.killAttempted -eq $true) `
        -Message 'Timeout must attempt exact process-tree cleanup.'

    $outputLimit = Invoke-Runner -Label 'output-limit' -Arguments @(
        '-Command', 'Contract',
        '-ContractScenario', 'OutputLimit',
        '-TimeoutSeconds', '30',
        '-EvidenceRoot', $runsRelativeRoot)
    $scenarioNames.Add('OutputLimit')
    Assert-Contract -Condition ($outputLimit.NativeExitCode -eq 0) -Message 'OutputLimit native exit code must be 0.'
    $outputLimitReceipt = Get-Receipt -Run $outputLimit
    Assert-CommonReceipt -Receipt $outputLimitReceipt -ExpectedStatus 'passed'
    foreach ($streamName in @('stdout', 'stderr')) {
        $stream = $outputLimitReceipt.steps[0].$streamName
        Assert-Contract -Condition ($stream.truncated -eq $true) -Message "$streamName must be truncated."
        Assert-Contract -Condition ($stream.bytes -le [int]$registry.limits.maximumCapturedBytesPerStream) `
            -Message "$streamName exceeded its byte limit."
        $streamPath = Join-Path $repositoryRoot ([string]$stream.path)
        Assert-Contract -Condition ((Get-Item -LiteralPath $streamPath).Length -eq $stream.bytes) `
            -Message "$streamName receipt bytes must match the file."
    }

    $readOnlyCleanup = Invoke-Runner -Label 'read-only-cleanup' -Arguments @(
        '-Command', 'Contract',
        '-ContractScenario', 'ReadOnlyCleanup',
        '-TimeoutSeconds', '30',
        '-EvidenceRoot', $runsRelativeRoot)
    $scenarioNames.Add('ReadOnlyCleanup')
    Assert-Contract -Condition ($readOnlyCleanup.NativeExitCode -eq 0) `
        -Message 'Owned read-only files must not block cleanup.'
    $readOnlyCleanupReceipt = Get-Receipt -Run $readOnlyCleanup
    Assert-CommonReceipt -Receipt $readOnlyCleanupReceipt -ExpectedStatus 'passed'
    Assert-Contract -Condition ($readOnlyCleanupReceipt.cleanup.removed -eq $true) `
        -Message 'Read-only cleanup must remove the owned scratch directory.'
    Assert-Contract -Condition (-not (Test-Path -LiteralPath (
                Join-Path $repositoryRoot "$($readOnlyCleanupReceipt.runDirectory)/scratch"))) `
        -Message 'Read-only cleanup cannot leave its scratch directory behind.'

    $cleanupFailure = Invoke-Runner -Label 'cleanup-failure' -Arguments @(
        '-Command', 'Contract',
        '-ContractScenario', 'CleanupFailure',
        '-TimeoutSeconds', '30',
        '-EvidenceRoot', $runsRelativeRoot)
    $scenarioNames.Add('CleanupFailure')
    Assert-Contract -Condition ($cleanupFailure.NativeExitCode -eq 4) `
        -Message 'Cleanup failure native exit code must be 4.'
    $cleanupFailureReceipt = Get-Receipt -Run $cleanupFailure
    Assert-Contract -Condition ($cleanupFailureReceipt.status -ceq 'infrastructure_error') `
        -Message 'Cleanup failure must be infrastructure_error.'
    Assert-Contract -Condition ($cleanupFailureReceipt.cleanup.reason -ceq 'owner_marker_mismatch') `
        -Message 'Cleanup failure must retain the ownership mismatch.'
    Assert-Contract -Condition (Test-Path -LiteralPath (Join-Path $repositoryRoot "$($cleanupFailureReceipt.runDirectory)/scratch")) `
        -Message 'Runner must refuse to delete a mismatched owned directory.'
    Assert-Contract -Condition ($cleanupFailureReceipt.lock.cleanup.released -eq $true) `
        -Message 'Cleanup failure must still release the shared lock.'

    $receiptFailure = Invoke-Runner -Label 'receipt-failure' -Arguments @(
        '-Command', 'Contract',
        '-ContractScenario', 'ReceiptFailure',
        '-TimeoutSeconds', '30',
        '-EvidenceRoot', $runsRelativeRoot)
    $scenarioNames.Add('ReceiptFailure')
    Assert-Contract -Condition ($receiptFailure.NativeExitCode -eq 4) `
        -Message 'Receipt failure native exit code must be 4.'
    Assert-Contract -Condition ($receiptFailure.Envelope.status -ceq 'infrastructure_error') `
        -Message 'Receipt failure must emit an infrastructure fallback.'
    Assert-Contract -Condition ($receiptFailure.Envelope.error.code -ceq 'receipt_write_failed') `
        -Message 'Receipt failure must retain its error code.'
    Assert-Contract -Condition ($null -eq $receiptFailure.Envelope.receipt) `
        -Message 'Receipt failure cannot claim a receipt exists.'
    $receiptFailureRun = Join-Path (Join-Path $repositoryRoot $runsRelativeRoot) ([string]$receiptFailure.Envelope.runId)
    Assert-Contract -Condition (@(Get-ChildItem -LiteralPath $receiptFailureRun -Filter 'receipt.json.tmp-*' -Force).Count -eq 0) `
        -Message 'Atomic receipt failure must remove its temporary file.'

    $holder = Start-LockHolder -Label 'lock-holder'
    try {
        $holderReady = $false
        $holderDeadline = [DateTime]::UtcNow.AddSeconds(10)
        while ([DateTime]::UtcNow -lt $holderDeadline) {
            if (Test-Path -LiteralPath $holderPath -PathType Leaf) {
                try {
                    $holderState = Get-Content -LiteralPath $holderPath -Raw -Encoding utf8 | ConvertFrom-Json
                    if ($holderState.processId -eq $holder.Id) {
                        $holderReady = $true
                        break
                    }
                }
                catch {
                }
            }
            Start-Sleep -Milliseconds 100
        }
        Assert-Contract -Condition $holderReady -Message 'Lock holder did not publish matching ownership.'

        $contender = Invoke-Runner -Label 'lock-contender' -Arguments @(
            '-Command', 'Contract',
            '-ContractScenario', 'Normal',
            '-WaitSeconds', '0',
            '-TimeoutSeconds', '30',
            '-EvidenceRoot', $runsRelativeRoot)
        $scenarioNames.Add('LockBusy')
        Assert-Contract -Condition ($contender.NativeExitCode -eq 2) -Message 'Lock contender native exit code must be 2.'
        $contenderReceipt = Get-Receipt -Run $contender
        Assert-CommonReceipt -Receipt $contenderReceipt -ExpectedStatus 'blocked'
        Assert-Contract -Condition ($contenderReceipt.error.code -ceq 'exclusive_lock_busy') `
            -Message 'Lock contender must report exclusive_lock_busy.'
        Assert-Contract -Condition ($contenderReceipt.lock.holder.processId -eq $holder.Id) `
            -Message 'Lock contender must report the exact holder PID.'

        Assert-Contract -Condition $holder.WaitForExit(30000) -Message 'Lock holder did not exit within its bound.'
        $holderStdout = $holder.StandardOutput.ReadToEnd()
        $holderStderr = $holder.StandardError.ReadToEnd()
        Assert-Contract -Condition ($holder.ExitCode -eq 0) -Message 'Lock holder must exit 0.'
        Assert-Contract -Condition ([string]::IsNullOrEmpty($holderStderr)) -Message 'Lock holder runner stderr must be empty.'
        $holderEnvelope = $holderStdout.Trim() | ConvertFrom-Json -Depth 20
        Assert-Contract -Condition ($holderEnvelope.status -ceq 'passed') -Message 'Lock holder must pass.'
    }
    finally {
        if (-not $holder.HasExited) {
            $holder.Kill($true)
            [void]$holder.WaitForExit(10000)
        }
        $holder.Dispose()
    }

    Assert-Contract -Condition (-not (Test-Path -LiteralPath $holderPath)) `
        -Message 'Holder marker must be absent after release.'
    $reacquired = Invoke-Runner -Label 'lock-reacquired' -Arguments @(
        '-Command', 'Contract',
        '-ContractScenario', 'Normal',
        '-TimeoutSeconds', '30',
        '-EvidenceRoot', $runsRelativeRoot)
    $scenarioNames.Add('LockReacquired')
    Assert-Contract -Condition ($reacquired.NativeExitCode -eq 0) -Message 'Lock must be reacquirable.'
    $reacquiredReceipt = Get-Receipt -Run $reacquired
    Assert-CommonReceipt -Receipt $reacquiredReceipt -ExpectedStatus 'passed'

    $completedUtc = [DateTime]::UtcNow
    $summary = [ordered]@{
        schemaVersion = 1
        ok = $true
        status = 'passed'
        suiteId = $suiteId
        startedUtc = $startedUtc.ToString('O')
        completedUtc = $completedUtc.ToString('O')
        durationSeconds = [Math]::Round(($completedUtc - $startedUtc).TotalSeconds, 3)
        assertions = $assertionCount
        scenarios = @($scenarioNames)
        evidenceRoot = $suiteRelativeRoot
        privateData = [ordered]@{ pathInspected = $false }
    }
    [IO.File]::WriteAllText(
        (Join-Path $suiteRoot 'summary.json'),
        ($summary | ConvertTo-Json -Depth 10) + "`n",
        $utf8)
    [Console]::Out.WriteLine(($summary | ConvertTo-Json -Depth 10 -Compress))
    exit 0
}
catch {
    $completedUtc = [DateTime]::UtcNow
    $summary = [ordered]@{
        schemaVersion = 1
        ok = $false
        status = 'failed'
        suiteId = $suiteId
        startedUtc = $startedUtc.ToString('O')
        completedUtc = $completedUtc.ToString('O')
        durationSeconds = [Math]::Round(($completedUtc - $startedUtc).TotalSeconds, 3)
        assertions = $assertionCount
        scenarios = @($scenarioNames)
        evidenceRoot = $suiteRelativeRoot
        firstRed = [ordered]@{
            type = $_.Exception.GetType().Name
            message = $_.Exception.Message.Replace($repositoryRoot, '<repo>', [StringComparison]::OrdinalIgnoreCase)
            scriptStackTrace = $_.ScriptStackTrace.Replace($repositoryRoot, '<repo>', [StringComparison]::OrdinalIgnoreCase)
        }
        privateData = [ordered]@{ pathInspected = $false }
    }
    [IO.File]::WriteAllText(
        (Join-Path $suiteRoot 'summary.json'),
        ($summary | ConvertTo-Json -Depth 10) + "`n",
        $utf8)
    [Console]::Out.WriteLine(($summary | ConvertTo-Json -Depth 10 -Compress))
    exit 1
}
