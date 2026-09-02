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

function Write-JetEnvelope {
    param([Parameter(Mandatory = $true)] $Value)

    [Console]::Out.WriteLine(($Value | ConvertTo-Json -Depth 16 -Compress))
}

function Get-JetRelativePath {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $Path
    )

    return [IO.Path]::GetRelativePath($RepositoryRoot, $Path).Replace('\', '/')
}

function Test-JetDescendantPath {
    param(
        [Parameter(Mandatory = $true)] [string] $Root,
        [Parameter(Mandatory = $true)] [string] $Candidate
    )

    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $candidateFull = [IO.Path]::GetFullPath($Candidate)
    $prefix = $rootFull + [IO.Path]::DirectorySeparatorChar
    return $candidateFull.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-JetNoExistingReparsePoint {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $Candidate
    )

    $repositoryFull = [IO.Path]::GetFullPath($RepositoryRoot)
    $candidateFull = [IO.Path]::GetFullPath($Candidate)
    if (-not (Test-JetDescendantPath -Root $repositoryFull -Candidate $candidateFull)) {
        throw [ArgumentException]::new('Harness-owned path must be a repository descendant.')
    }

    $relative = [IO.Path]::GetRelativePath($repositoryFull, $candidateFull)
    $current = $repositoryFull
    foreach ($segment in $relative.Split(
            @([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar),
            [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $segment
        if (-not (Test-Path -LiteralPath $current)) {
            continue
        }

        $item = Get-Item -LiteralPath $current -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw [ArgumentException]::new('Harness-owned paths cannot traverse a reparse point.')
        }
    }
}

function Clear-JetOwnedReadOnlyAttributes {
    param([Parameter(Mandatory = $true)] [string] $Root)

    $rootFull = [IO.Path]::GetFullPath($Root)
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($rootFull)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            $attributes = [IO.File]::GetAttributes($entry)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw [InvalidDataException]::new(
                    'Harness-owned cleanup refuses descendant reparse points.')
            }
            if (($attributes -band [IO.FileAttributes]::ReadOnly) -ne 0) {
                $attributes = [IO.FileAttributes](
                    [int]$attributes -band (-bnot [int][IO.FileAttributes]::ReadOnly))
                [IO.File]::SetAttributes($entry, $attributes)
            }
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                $pending.Push($entry)
            }
        }
    }
}

function Write-JetAtomicJson {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] $Value
    )

    $parent = Split-Path -Parent $Path
    [void][IO.Directory]::CreateDirectory($parent)
    $temporaryPath = "$Path.tmp-$([Guid]::NewGuid().ToString('N'))"
    try {
        $json = $Value | ConvertTo-Json -Depth 20
        [IO.File]::WriteAllText($temporaryPath, $json + "`n", $script:Utf8)
        [IO.File]::Move($temporaryPath, $Path, $true)
    }
    catch {
        if ([IO.File]::Exists($temporaryPath)) {
            [IO.File]::Delete($temporaryPath)
        }
        throw
    }
}

function Read-JetRegistry {
    $registryPath = Join-Path $PSScriptRoot 'lanes.json'
    $registry = Get-Content -LiteralPath $registryPath -Raw -Encoding utf8 |
        ConvertFrom-Json -AsHashtable -Depth 20

    if ([int]$registry.schemaVersion -ne 1) {
        throw [InvalidDataException]::new('Unsupported lane registry schema.')
    }

    foreach ($required in @('runnerContractVersion', 'limits', 'testSettings', 'packageSettings', 'guiSettings', 'excelSettings', 'documentationSettings', 'releaseCandidateSettings', 'configurations', 'contractScenarios', 'commands', 'lanes')) {
        if (-not $registry.ContainsKey($required)) {
            throw [InvalidDataException]::new("Lane registry is missing '$required'.")
        }
    }
    if ([string]$registry.runnerContractVersion -cne '8.0') {
        throw [InvalidDataException]::new('Unsupported runner contract version.')
    }

    $enabledLaneNames = @(
        $registry.lanes.Keys |
            Where-Object { [bool]$registry.lanes[$_].enabled } |
            Sort-Object
    )
    if (($enabledLaneNames -join ',') -cne 'Excel,Focused,Foundation,Gui,Package,PrivateCase,Provider,Public,ReleaseCandidate') {
        throw [InvalidDataException]::new(
            'The enabled lane set does not match the currently implemented Harness boundary.')
    }

    foreach ($commandName in $registry.commands.Keys) {
        $command = $registry.commands[$commandName]
        if (-not $registry.lanes.ContainsKey([string]$command.lane)) {
            throw [InvalidDataException]::new("Command '$commandName' references an unknown lane.")
        }
        if ([bool]$command.enabled -and -not [bool]$registry.lanes[[string]$command.lane].enabled) {
            throw [InvalidDataException]::new("Enabled command '$commandName' references a disabled lane.")
        }
        if ([bool]$command.enabled -and [bool]$command.privateDataAccess -and $commandName -cne 'PrivateCase') {
            throw [InvalidDataException]::new('Only the PrivateCase command may access private data.')
        }
    }
    if (-not $registry.commands.ContainsKey('PrivateCase') -or
        -not [bool]$registry.commands.PrivateCase.enabled -or
        [string]$registry.commands.PrivateCase.lane -cne 'PrivateCase' -or
        -not [bool]$registry.commands.PrivateCase.requiresExclusiveLock -or
        -not [bool]$registry.commands.PrivateCase.privateDataAccess) {
        throw [InvalidDataException]::new('The PrivateCase command boundary is invalid.')
    }
    if (-not $registry.commands.ContainsKey('ReleaseCandidate') -or
        -not [bool]$registry.commands.ReleaseCandidate.enabled -or
        [string]$registry.commands.ReleaseCandidate.lane -cne 'ReleaseCandidate' -or
        -not [bool]$registry.commands.ReleaseCandidate.requiresExclusiveLock -or
        [bool]$registry.commands.ReleaseCandidate.privateDataAccess) {
        throw [InvalidDataException]::new('The ReleaseCandidate command boundary is invalid.')
    }

    $testSettings = $registry.testSettings
    foreach ($required in @(
            'project',
            'targetFramework',
            'seed',
            'publicMinimumExpectedTests',
            'publicSkipPolicy',
            'providerConnectionEnvironmentVariable',
            'providerLocalMinimumExpectedTests',
            'providerSqlMinimumExpectedTests',
            'providerLocalDbMinimumExpectedTests',
            'providerLocalParityMethodPatterns',
            'providerSqlTraits',
            'providerLocalDbTraits',
            'providerSkipPolicy',
            'privateCaseMinimumExpectedTests',
            'privateCaseRootEnvironmentVariable',
            'privateCaseManifestEnvironmentVariable',
            'privateCaseProviderEnvironmentVariable',
            'packageMinimumExpectedTests',
            'packageMethodPatterns',
            'excludedProfiles',
            'protectedWorkspaceMethodPatterns',
            'focusedGuardNamespaces',
            'environmentVariablesToRemove',
            'providerEnvironmentVariablesToRemove')) {
        if (-not $testSettings.ContainsKey($required)) {
            throw [InvalidDataException]::new("Test settings are missing '$required'.")
        }
    }
    if ([IO.Path]::IsPathRooted([string]$testSettings.project) -or
        [string]::IsNullOrWhiteSpace([string]$testSettings.project)) {
        throw [InvalidDataException]::new('The test project must be repository-relative.')
    }
    if ([string]$testSettings.project -cne 'src/JET/tests/JET.Tests/JET.Tests.csproj') {
        throw [InvalidDataException]::new('The test project must remain inside the public product-test tree.')
    }
    foreach ($minimumName in @(
            'publicMinimumExpectedTests',
            'providerLocalMinimumExpectedTests',
            'providerSqlMinimumExpectedTests',
            'providerLocalDbMinimumExpectedTests',
            'privateCaseMinimumExpectedTests',
            'packageMinimumExpectedTests')) {
        if ([int]$testSettings[$minimumName] -lt 1) {
            throw [InvalidDataException]::new('Test seed and minimum test counts must be positive.')
        }
    }
    if ([int]$testSettings.seed -lt 1) {
        throw [InvalidDataException]::new('Test seed and minimum test counts must be positive.')
    }
    foreach ($profile in @($testSettings.excludedProfiles)) {
        if ([string]::IsNullOrWhiteSpace([string]$profile) -or [string]$profile -notmatch '^[A-Za-z][A-Za-z0-9]+$') {
            throw [InvalidDataException]::new('Excluded test profiles contain an invalid name.')
        }
    }
    foreach ($pattern in @($testSettings.protectedWorkspaceMethodPatterns)) {
        if ([string]$pattern -notmatch '^\*[A-Za-z0-9_.+]+Tests\.\*$') {
            throw [InvalidDataException]::new(
                'Protected-workspace filters must name one exact test class instead of a broad family.')
        }
    }
    foreach ($pattern in @($testSettings.providerLocalParityMethodPatterns) + @($testSettings.packageMethodPatterns)) {
        if ([string]$pattern -notmatch '^\*[A-Za-z0-9_.+]+\*$') {
            throw [InvalidDataException]::new('Provider and package test patterns must be bounded wildcard method names.')
        }
    }
    foreach ($trait in @($testSettings.providerSqlTraits) + @($testSettings.providerLocalDbTraits)) {
        if ([string]$trait -notmatch '^[A-Za-z][A-Za-z0-9]+=[A-Za-z][A-Za-z0-9]+$') {
            throw [InvalidDataException]::new('Provider trait filters contain an invalid value.')
        }
    }
    foreach ($namespace in @($testSettings.focusedGuardNamespaces)) {
        if ([string]$namespace -notmatch '^[A-Za-z][A-Za-z0-9_.]+$') {
            throw [InvalidDataException]::new('Focused guard namespaces contain an invalid name.')
        }
    }
    $environmentNames = @($testSettings.environmentVariablesToRemove)
    if (@($environmentNames | Sort-Object -Unique).Count -ne $environmentNames.Count) {
        throw [InvalidDataException]::new('Sanitized environment-variable names must be unique.')
    }
    foreach ($name in $environmentNames) {
        if ([string]$name -notmatch '^[A-Z][A-Z0-9_]+$') {
            throw [InvalidDataException]::new('Sanitized environment-variable names contain an invalid name.')
        }
    }
    $privateEnvironmentNames = @(
        [string]$testSettings.privateCaseRootEnvironmentVariable,
        [string]$testSettings.privateCaseManifestEnvironmentVariable,
        [string]$testSettings.privateCaseProviderEnvironmentVariable)
    if (($privateEnvironmentNames -join ',') -cne
            'JET_PRIVATE_CASE_ROOT,JET_PRIVATE_CASE_MANIFEST,JET_PRIVATE_CASE_PROVIDER' -or
        @($privateEnvironmentNames | Sort-Object -Unique).Count -ne 3) {
        throw [InvalidDataException]::new('PrivateCase environment-variable names are invalid.')
    }
    foreach ($name in $privateEnvironmentNames) {
        if ($environmentNames -cnotcontains $name) {
            throw [InvalidDataException]::new(
                'General commands must remove every PrivateCase environment variable.')
        }
    }
    $providerConnectionVariable = [string]$testSettings.providerConnectionEnvironmentVariable
    if ($providerConnectionVariable -cne 'JET_SQLSERVER_CONNECTION' -or
        $environmentNames -cnotcontains $providerConnectionVariable) {
        throw [InvalidDataException]::new(
            'Provider connection variable must use the declared JET SQL Server boundary.')
    }
    $providerEnvironmentNames = @($testSettings.providerEnvironmentVariablesToRemove)
    if (@($providerEnvironmentNames | Sort-Object -Unique).Count -ne $providerEnvironmentNames.Count -or
        $providerEnvironmentNames -ccontains $providerConnectionVariable) {
        throw [InvalidDataException]::new('Provider environment sanitation is invalid.')
    }
    foreach ($name in $providerEnvironmentNames) {
        if ([string]$name -notmatch '^[A-Z][A-Z0-9_]+$') {
            throw [InvalidDataException]::new('Provider environment sanitation contains an invalid name.')
        }
    }
    $expectedProviderEnvironment = @($environmentNames | Where-Object { $_ -cne $providerConnectionVariable } | Sort-Object)
    if (($expectedProviderEnvironment -join ',') -cne (@($providerEnvironmentNames | Sort-Object) -join ',')) {
        throw [InvalidDataException]::new('Provider sanitation may preserve only its declared connection variable.')
    }
    foreach ($policyName in @([string]$testSettings.publicSkipPolicy, [string]$testSettings.providerSkipPolicy)) {
        if ($policyName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]+\.json$') {
            throw [InvalidDataException]::new('Skip-policy file name is invalid.')
        }
    }

    $documentationSettings = $registry.documentationSettings
    foreach ($required in @('verifier', 'policy')) {
        if (-not $documentationSettings.ContainsKey($required)) {
            throw [InvalidDataException]::new("Documentation settings are missing '$required'.")
        }
    }
    if ([string]$documentationSettings.verifier -cne 'tools/harness/JetDocumentationCheck.ps1' -or
        [string]$documentationSettings.policy -cne 'tools/harness/documentation-check.json') {
        throw [InvalidDataException]::new('Documentation checks must use the fixed verifier and file list.')
    }

    $releaseCandidateSettings = $registry.releaseCandidateSettings
    foreach ($required in @('candidateManifest', 'snapshotWorkspaceRoot', 'privateCaseIncluded', 'liveProviderIncluded', 'steps')) {
        if (-not $releaseCandidateSettings.ContainsKey($required)) {
            throw [InvalidDataException]::new("ReleaseCandidate settings are missing '$required'.")
        }
    }
    if ([string]$releaseCandidateSettings.candidateManifest -cne 'docs/first-root-commit-candidate.txt' -or
        [string]$releaseCandidateSettings.snapshotWorkspaceRoot -cne 'artifacts/harness/rc' -or
        [bool]$releaseCandidateSettings.privateCaseIncluded -or
        [bool]$releaseCandidateSettings.liveProviderIncluded) {
        throw [InvalidDataException]::new(
            'ReleaseCandidate must use the reviewed candidate manifest and short owned workspace without PrivateCase or live Provider.')
    }
    $expectedReleaseCandidateSteps = @(
        [ordered]@{ name = 'contract'; command = 'Contract'; configuration = 'Release'; noRestore = $false },
        [ordered]@{ name = 'documentation'; command = 'Documentation'; configuration = 'Release'; noRestore = $false },
        [ordered]@{ name = 'public'; command = 'Public'; configuration = 'Release'; noRestore = $false },
        [ordered]@{ name = 'package'; command = 'Package'; configuration = 'Release'; noRestore = $true },
        [ordered]@{ name = 'gui'; command = 'Gui'; configuration = 'AgentGuiTest'; noRestore = $false },
        [ordered]@{ name = 'excel'; command = 'Excel'; configuration = 'Release'; noRestore = $false }
    )
    $releaseCandidateSteps = @($releaseCandidateSettings.steps)
    if ($releaseCandidateSteps.Count -ne $expectedReleaseCandidateSteps.Count) {
        throw [InvalidDataException]::new('ReleaseCandidate must retain the six reviewed public-candidate steps.')
    }
    for ($index = 0; $index -lt $expectedReleaseCandidateSteps.Count; $index++) {
        $actual = $releaseCandidateSteps[$index]
        $expected = $expectedReleaseCandidateSteps[$index]
        foreach ($required in @('name', 'command', 'configuration', 'noRestore')) {
            if (-not $actual.ContainsKey($required)) {
                throw [InvalidDataException]::new("ReleaseCandidate step is missing '$required'.")
            }
        }
        if ([string]$actual.name -cne [string]$expected.name -or
            [string]$actual.command -cne [string]$expected.command -or
            [string]$actual.configuration -cne [string]$expected.configuration -or
            [bool]$actual.noRestore -ne [bool]$expected.noRestore) {
            throw [InvalidDataException]::new('ReleaseCandidate step order or arguments changed.')
        }
    }

    $packageSettings = $registry.packageSettings
    foreach ($required in @('project', 'sourceRoot', 'publishProfile', 'runtimeIdentifier', 'verifier', 'templateRelativePaths')) {
        if (-not $packageSettings.ContainsKey($required)) {
            throw [InvalidDataException]::new("Package settings are missing '$required'.")
        }
    }
    foreach ($pathName in @('project', 'sourceRoot', 'verifier')) {
        $relativePath = [string]$packageSettings[$pathName]
        if ([string]::IsNullOrWhiteSpace($relativePath) -or [IO.Path]::IsPathRooted($relativePath) -or
            $relativePath -eq '..' -or $relativePath.StartsWith('../', [StringComparison]::Ordinal)) {
            throw [InvalidDataException]::new('Package paths must be repository-relative.')
        }
    }
    if ([string]$packageSettings.project -cne 'src/JET/JET/JET.csproj' -or
        [string]$packageSettings.sourceRoot -cne 'src/JET/JET' -or
        [string]$packageSettings.verifier -cne 'tools/harness/JetPackageVerifier.ps1' -or
        [string]$packageSettings.publishProfile -cne 'FolderProfile' -or
        [string]$packageSettings.runtimeIdentifier -cne 'win-x64') {
        throw [InvalidDataException]::new('Package settings escaped the fixed product and FolderProfile boundary.')
    }
    $templatePaths = @($packageSettings.templateRelativePaths)
    if ($templatePaths.Count -eq 0 -or @($templatePaths | Sort-Object -Unique).Count -ne $templatePaths.Count) {
        throw [InvalidDataException]::new('Package template paths must be non-empty and unique.')
    }
    foreach ($templatePath in $templatePaths) {
        if ([string]::IsNullOrWhiteSpace([string]$templatePath) -or [IO.Path]::IsPathRooted([string]$templatePath) -or
            [string]$templatePath -match '(^|/)\.\.(/|$)') {
            throw [InvalidDataException]::new('Package template paths must remain inside the product source root.')
        }
    }

    $guiSettings = $registry.guiSettings
    foreach ($required in @(
            'applicationProject',
            'applicationExecutable',
            'driverProject',
            'driverAssembly',
            'configuration',
            'scenarios')) {
        if (-not $guiSettings.ContainsKey($required)) {
            throw [InvalidDataException]::new("GUI settings are missing '$required'.")
        }
    }
    foreach ($pathName in @('applicationProject', 'applicationExecutable', 'driverProject', 'driverAssembly')) {
        $relativePath = [string]$guiSettings[$pathName]
        if ([string]::IsNullOrWhiteSpace($relativePath) -or [IO.Path]::IsPathRooted($relativePath) -or
            $relativePath -eq '..' -or $relativePath.StartsWith('../', [StringComparison]::Ordinal) -or
            $relativePath -match '(^|/)\.\.(/|$)') {
            throw [InvalidDataException]::new('GUI paths must be repository-relative descendants.')
        }
    }
    if ([string]$guiSettings.applicationProject -cne 'src/JET/JET/JET.csproj' -or
        [string]$guiSettings.applicationExecutable -cne 'src/JET/JET/bin/AgentGuiTest/net10.0-windows/JET.exe' -or
        [string]$guiSettings.driverProject -cne 'tools/harness/gui-driver/GuiDriver.csproj' -or
        [string]$guiSettings.driverAssembly -cne 'tools/harness/gui-driver/bin/Release/net10.0-windows/Jet.GuiDriver.dll' -or
        [string]$guiSettings.configuration -cne 'AgentGuiTest') {
        throw [InvalidDataException]::new('GUI settings escaped the fixed application and driver boundary.')
    }

    $guiScenarios = @($guiSettings.scenarios)
    $expectedGuiScenarios = @(
        [ordered]@{ name = 'startup-smoke'; timeoutSeconds = 120; actionBudget = 4; expectedActionCount = 1; screenshotBudget = 0 },
        [ordered]@{ name = 'synthetic-sqlite-create'; timeoutSeconds = 150; actionBudget = 16; expectedActionCount = 15; screenshotBudget = 0 },
        [ordered]@{ name = 'mapping-required-sync'; timeoutSeconds = 180; actionBudget = 12; expectedActionCount = 10; screenshotBudget = 0 },
        [ordered]@{ name = 'conflicted-journal-recovery'; timeoutSeconds = 180; actionBudget = 8; expectedActionCount = 5; screenshotBudget = 0 }
    )
    if ($guiScenarios.Count -ne $expectedGuiScenarios.Count) {
        throw [InvalidDataException]::new('GUI scenarios must contain only the four reviewed scenarios.')
    }
    for ($index = 0; $index -lt $expectedGuiScenarios.Count; $index++) {
        $scenario = $guiScenarios[$index]
        $expectedScenario = $expectedGuiScenarios[$index]
        foreach ($required in @('name', 'timeoutSeconds', 'actionBudget', 'expectedActionCount', 'screenshotBudget')) {
            if (-not $scenario.ContainsKey($required)) {
                throw [InvalidDataException]::new("GUI scenario is missing '$required'.")
            }
        }
        if ([string]$scenario.name -cne [string]$expectedScenario.name -or
            [int]$scenario.timeoutSeconds -ne [int]$expectedScenario.timeoutSeconds -or
            [int]$scenario.actionBudget -ne [int]$expectedScenario.actionBudget -or
            [int]$scenario.expectedActionCount -ne [int]$expectedScenario.expectedActionCount -or
            [int]$scenario.screenshotBudget -ne 0) {
            throw [InvalidDataException]::new('GUI scenario name or budget escaped the reviewed boundary.')
        }
    }

    $excelSettings = $registry.excelSettings
    foreach ($required in @(
            'driverProject',
            'driverAssembly',
            'configuration',
            'fixtureMethodPattern',
            'fixtureEnvironmentVariable',
            'scenario',
            'timeoutSeconds',
            'reportKinds')) {
        if (-not $excelSettings.ContainsKey($required)) {
            throw [InvalidDataException]::new("Excel settings are missing '$required'.")
        }
    }
    foreach ($pathName in @('driverProject', 'driverAssembly')) {
        $relativePath = [string]$excelSettings[$pathName]
        if ([string]::IsNullOrWhiteSpace($relativePath) -or [IO.Path]::IsPathRooted($relativePath) -or
            $relativePath -eq '..' -or $relativePath.StartsWith('../', [StringComparison]::Ordinal) -or
            $relativePath -match '(^|/)\.\.(/|$)') {
            throw [InvalidDataException]::new('Excel paths must be repository-relative descendants.')
        }
    }
    if ([string]$excelSettings.driverProject -cne 'tools/harness/excel-driver/ExcelDriver.csproj' -or
        [string]$excelSettings.driverAssembly -cne 'tools/harness/excel-driver/bin/Release/net10.0-windows/Jet.ExcelDriver.dll' -or
        [string]$excelSettings.configuration -cne 'Release' -or
        [string]$excelSettings.fixtureMethodPattern -cne '*SixReportWorkflowJourneyTests.AccountMappingHandoff_PreservesValidationRun_AndPublishesSixCurrentArtifacts*' -or
        [string]$excelSettings.fixtureEnvironmentVariable -cne 'JET_SIX_REPORT_EVIDENCE_DIR' -or
        $environmentNames -cnotcontains ([string]$excelSettings.fixtureEnvironmentVariable) -or
        [string]$excelSettings.scenario -cne 'synthetic-report-roundtrip' -or
        [int]$excelSettings.timeoutSeconds -ne 180) {
        throw [InvalidDataException]::new('Excel settings escaped the fixed synthetic six-report boundary.')
    }
    $expectedExcelReportKinds = @(
        'accountMapping',
        'criteriaSelectionReport',
        'infReport',
        'prescreenReport',
        'validationReport',
        'workingPaper'
    )
    if ((@($excelSettings.reportKinds) -join ',') -cne ($expectedExcelReportKinds -join ',')) {
        throw [InvalidDataException]::new('Excel report kinds must match the six public product outputs.')
    }
    if ((@($registry.configurations | Sort-Object -Unique) -join ',') -cne 'AgentGuiTest,Debug,Release') {
        throw [InvalidDataException]::new('Harness configurations must be exactly Debug, Release, and AgentGuiTest.')
    }

    return $registry
}

function ConvertTo-JetInteger {
    param(
        [Parameter(Mandatory = $true)] [string] $Value,
        [Parameter(Mandatory = $true)] [string] $Name,
        [Parameter(Mandatory = $true)] [int] $Minimum,
        [Parameter(Mandatory = $true)] [int] $Maximum
    )

    $parsed = 0
    if (-not [int]::TryParse($Value, [ref] $parsed) -or $parsed -lt $Minimum -or $parsed -gt $Maximum) {
        throw [ArgumentException]::new("$Name must be between $Minimum and $Maximum.")
    }
    return $parsed
}

function Resolve-JetEvidenceRoot {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $EvidenceRoot
    )

    if ([string]::IsNullOrWhiteSpace($EvidenceRoot) -or [IO.Path]::IsPathRooted($EvidenceRoot)) {
        throw [ArgumentException]::new('EvidenceRoot must be a non-rooted repository-relative path.')
    }

    $repositoryFull = [IO.Path]::GetFullPath($RepositoryRoot)
    $harnessArtifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryFull 'artifacts/harness'))
    $evidenceFull = [IO.Path]::GetFullPath((Join-Path $repositoryFull $EvidenceRoot))
    if (-not (Test-JetDescendantPath -Root $harnessArtifactsRoot -Candidate $evidenceFull)) {
        throw [ArgumentException]::new('EvidenceRoot must be a child of artifacts/harness/.')
    }

    Assert-JetNoExistingReparsePoint -RepositoryRoot $repositoryFull -Candidate $evidenceFull
    [void][IO.Directory]::CreateDirectory($evidenceFull)
    Assert-JetNoExistingReparsePoint -RepositoryRoot $repositoryFull -Candidate $evidenceFull
    return $evidenceFull
}

function Get-JetRunnerIdentity {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] $Registry
    )

    $relativePaths = @(
        'tools/verify.ps1',
        'tools/harness/JetHarness.psm1',
        'tools/harness/JetHarness.Process.cs',
        'tools/harness/JetDocumentationCheck.ps1',
        'tools/harness/JetPackageVerifier.ps1',
        'tools/harness/gui-driver/GuiDriver.csproj',
        'tools/harness/gui-driver/DriverContracts.cs',
        'tools/harness/gui-driver/CdpSession.cs',
        'tools/harness/gui-driver/OwnedGuiRun.cs',
        'tools/harness/gui-driver/GuiRunner.cs',
        'tools/harness/gui-driver/GuiScenarios.cs',
        'tools/harness/gui-driver/Program.cs',
        'tools/harness/excel-driver/ExcelDriver.csproj',
        'tools/harness/excel-driver/DriverContracts.cs',
        'tools/harness/excel-driver/DriverManifest.cs',
        'tools/harness/excel-driver/OwnedExcelRun.cs',
        'tools/harness/excel-driver/ExcelProcessCatalog.cs',
        'tools/harness/excel-driver/ExcelAutomation.cs',
        'tools/harness/excel-driver/PdfStructureInspector.cs',
        'tools/harness/excel-driver/ExcelRunner.cs',
        'tools/harness/excel-driver/Program.cs',
        'tools/harness/documentation-check.json',
        'tools/harness/lanes.json',
        'tools/harness/public-skip-policy.json',
        'tools/harness/provider-skip-policy.json',
        'tools/harness/contract-probe.ps1'
    )
    $files = foreach ($relativePath in $relativePaths) {
        $fullPath = Join-Path $RepositoryRoot $relativePath
        [ordered]@{
            path = $relativePath
            sha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    return [ordered]@{
        contractVersion = [string]$Registry.runnerContractVersion
        files = @($files)
    }
}

function Get-JetRepositoryIdentity {
    param([Parameter(Mandatory = $true)] [string] $RepositoryRoot)

    $branchOutput = @(& git -C $RepositoryRoot branch --show-current 2>$null)
    $branchExit = $LASTEXITCODE
    $headOutput = @(& git -C $RepositoryRoot rev-parse --verify HEAD 2>$null)
    $headExit = $LASTEXITCODE

    return [ordered]@{
        branch = if ($branchExit -eq 0 -and $branchOutput.Count -gt 0) { [string]$branchOutput[-1] } else { $null }
        head = if ($headExit -eq 0 -and $headOutput.Count -gt 0) { [string]$headOutput[-1] } else { $null }
        unborn = $headExit -ne 0
    }
}

function New-JetRunContext {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $EvidenceRoot,
        [Parameter(Mandatory = $true)] [string] $Command
    )

    $startedUtc = [DateTime]::UtcNow
    $runId = '{0}-{1}' -f $startedUtc.ToString('yyyyMMdd-HHmmssfff'), ([Guid]::NewGuid().ToString('N'))
    $runDirectory = Join-Path $EvidenceRoot $runId
    if (Test-Path -LiteralPath $runDirectory) {
        throw [IOException]::new('Generated run directory already exists.')
    }

    [void][IO.Directory]::CreateDirectory($runDirectory)
    $owner = [ordered]@{
        schemaVersion = 1
        kind = 'jet-harness-run'
        runId = $runId
        processId = $PID
        command = $Command
        createdUtc = $startedUtc.ToString('O')
    }
    Write-JetAtomicJson -Path (Join-Path $runDirectory $script:OwnerMarkerName) -Value $owner

    $scratchDirectory = Join-Path $runDirectory 'scratch'
    [void][IO.Directory]::CreateDirectory($scratchDirectory)
    $scratchOwner = [ordered]@{
        schemaVersion = 1
        kind = 'jet-harness-scratch'
        runId = $runId
        processId = $PID
    }
    Write-JetAtomicJson -Path (Join-Path $scratchDirectory $script:OwnerMarkerName) -Value $scratchOwner

    return [pscustomobject]@{
        StartedUtc = $startedUtc
        RunId = $runId
        RunDirectory = $runDirectory
        ScratchDirectory = $scratchDirectory
        ReceiptPath = Join-Path $runDirectory 'receipt.json'
    }
}

function Remove-JetOwnedDirectory {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $RunDirectory,
        [Parameter(Mandatory = $true)] [string] $OwnedDirectory,
        [Parameter(Mandatory = $true)] [string] $RunId
    )

    if (-not (Test-Path -LiteralPath $OwnedDirectory)) {
        return [ordered]@{ status = 'passed'; removed = $false; reason = 'already_absent' }
    }

    $ownedFull = [IO.Path]::GetFullPath($OwnedDirectory)
    $runFull = [IO.Path]::GetFullPath($RunDirectory)
    if (-not (Test-JetDescendantPath -Root $runFull -Candidate $ownedFull)) {
        return [ordered]@{ status = 'failed'; removed = $false; reason = 'outside_run_root' }
    }

    try {
        Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $ownedFull
        $markerPath = Join-Path $ownedFull $script:OwnerMarkerName
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
            return [ordered]@{ status = 'failed'; removed = $false; reason = 'owner_marker_missing' }
        }
        $marker = Get-Content -LiteralPath $markerPath -Raw -Encoding utf8 | ConvertFrom-Json
        if ($marker.kind -cne 'jet-harness-scratch' -or $marker.runId -cne $RunId) {
            return [ordered]@{ status = 'failed'; removed = $false; reason = 'owner_marker_mismatch' }
        }
        Clear-JetOwnedReadOnlyAttributes -Root $ownedFull
        [IO.Directory]::Delete($ownedFull, $true)
        return [ordered]@{ status = 'passed'; removed = $true; reason = $null }
    }
    catch {
        return [ordered]@{
            status = 'failed'
            removed = $false
            reason = 'owned_cleanup_exception'
            errorType = $_.Exception.GetType().Name
        }
    }
}

function Remove-JetReleaseCandidateWorkspace {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $WorkspaceRoot,
        [Parameter(Mandatory = $true)] [string] $RunId,
        [Parameter(Mandatory = $true)] $Registry
    )

    if (-not (Test-Path -LiteralPath $WorkspaceRoot)) {
        return [ordered]@{ status = 'passed'; removed = $false; reason = 'already_absent' }
    }

    $repositoryFull = [IO.Path]::GetFullPath($RepositoryRoot)
    $workspaceBase = [IO.Path]::GetFullPath(
        [string]$Registry.releaseCandidateSettings.snapshotWorkspaceRoot,
        $repositoryFull)
    $workspaceFull = [IO.Path]::GetFullPath($WorkspaceRoot)
    $workspaceParent = [IO.Path]::GetDirectoryName($workspaceFull)
    if (-not [string]::Equals(
            $workspaceParent,
            $workspaceBase,
            [StringComparison]::OrdinalIgnoreCase)) {
        return [ordered]@{ status = 'failed'; removed = $false; reason = 'outside_release_candidate_root' }
    }

    try {
        Assert-JetNoExistingReparsePoint -RepositoryRoot $repositoryFull -Candidate $workspaceFull
        $markerPath = Join-Path $workspaceFull $script:OwnerMarkerName
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
            return [ordered]@{ status = 'failed'; removed = $false; reason = 'owner_marker_missing' }
        }
        $marker = Get-Content -LiteralPath $markerPath -Raw -Encoding utf8 | ConvertFrom-Json
        if ($marker.kind -cne 'jet-harness-release-candidate-workspace' -or
            $marker.runId -cne $RunId) {
            return [ordered]@{ status = 'failed'; removed = $false; reason = 'owner_marker_mismatch' }
        }
        Clear-JetOwnedReadOnlyAttributes -Root $workspaceFull
        [IO.Directory]::Delete($workspaceFull, $true)
        return [ordered]@{ status = 'passed'; removed = $true; reason = $null }
    }
    catch {
        return [ordered]@{
            status = 'failed'
            removed = $false
            reason = 'owned_cleanup_exception'
            errorType = $_.Exception.GetType().Name
        }
    }
}

function Enter-JetExclusiveLock {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $RunId,
        [Parameter(Mandatory = $true)] [string] $Command,
        [Parameter(Mandatory = $true)] [int] $WaitSeconds
    )

    $controlRoot = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'artifacts/harness/control'))
    Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $controlRoot
    [void][IO.Directory]::CreateDirectory($controlRoot)
    Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $controlRoot

    $lockPath = Join-Path $controlRoot 'exclusive.lock'
    $holderPath = Join-Path $controlRoot 'holder.json'
    $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
    $stream = $null

    do {
        try {
            $stream = [IO.FileStream]::new(
                $lockPath,
                [IO.FileMode]::OpenOrCreate,
                [IO.FileAccess]::ReadWrite,
                [IO.FileShare]::None)
            break
        }
        catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) {
                $holder = $null
                if (Test-Path -LiteralPath $holderPath -PathType Leaf) {
                    try {
                        $candidate = Get-Content -LiteralPath $holderPath -Raw -Encoding utf8 | ConvertFrom-Json
                        $holder = [ordered]@{
                            runId = $candidate.runId
                            processId = $candidate.processId
                            command = $candidate.command
                            acquiredUtc = $candidate.acquiredUtc
                        }
                    }
                    catch {
                        $holder = [ordered]@{ state = 'unreadable' }
                    }
                }
                return [pscustomobject]@{
                    Acquired = $false
                    Stream = $null
                    HolderPath = $holderPath
                    Holder = $holder
                }
            }
            Start-Sleep -Milliseconds 100
        }
    } while ($true)

    try {
        $holder = [ordered]@{
            schemaVersion = 1
            runId = $RunId
            processId = $PID
            command = $Command
            acquiredUtc = [DateTime]::UtcNow.ToString('O')
        }
        Write-JetAtomicJson -Path $holderPath -Value $holder
        return [pscustomobject]@{
            Acquired = $true
            Stream = $stream
            HolderPath = $holderPath
            Holder = $holder
        }
    }
    catch {
        $stream.Dispose()
        throw
    }
}

function Exit-JetExclusiveLock {
    param(
        [Parameter(Mandatory = $true)] $Lock,
        [Parameter(Mandatory = $true)] [string] $RunId
    )

    $result = [ordered]@{
        status = 'passed'
        released = $false
        holderRemoved = $false
        reason = $null
    }

    try {
        if (-not (Test-Path -LiteralPath $Lock.HolderPath -PathType Leaf)) {
            $result.status = 'failed'
            $result.reason = 'holder_missing'
        }
        else {
            $holder = Get-Content -LiteralPath $Lock.HolderPath -Raw -Encoding utf8 | ConvertFrom-Json
            if ($holder.runId -cne $RunId) {
                $result.status = 'failed'
                $result.reason = 'holder_mismatch'
            }
            else {
                [IO.File]::Delete($Lock.HolderPath)
                $result.holderRemoved = $true
            }
        }
    }
    catch {
        $result.status = 'failed'
        $result.reason = 'holder_cleanup_exception'
        $result.errorType = $_.Exception.GetType().Name
    }
    finally {
        try {
            $Lock.Stream.Dispose()
            $result.released = $true
        }
        catch {
            $result.status = 'failed'
            $result.reason = 'lock_release_exception'
            $result.errorType = $_.Exception.GetType().Name
        }
    }

    return $result
}

function Test-JetNuGetSourceUnavailable {
    param(
        [Parameter(Mandatory = $true)] [string[]] $Paths
    )

    foreach ($path in $Paths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            continue
        }

        $content = [IO.File]::ReadAllText($path)
        if ([regex]::IsMatch($content, '(?<![A-Z0-9])NU1301(?![A-Z0-9])', [Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Get-JetTextSha256 {
    param([Parameter(Mandatory = $true)] [AllowEmptyString()] [string] $Value)

    $bytes = $script:Utf8.GetBytes($Value)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
}

function Normalize-JetSkipReason {
    param([AllowNull()] [string] $Reason)

    if ([string]::IsNullOrWhiteSpace($Reason)) {
        return ''
    }

    $normalized = [Text.RegularExpressions.Regex]::Replace($Reason.Trim(), '\s+', ' ')
    foreach ($pattern in @(
            '^(?i:skip(?: reason)?)\s*:\s*',
            '^(?i:test skipped(?: because)?)\s*:\s*')) {
        $normalized = [Text.RegularExpressions.Regex]::Replace($normalized, $pattern, '')
    }
    return $normalized.Trim()
}

function Protect-JetSensitiveTestEvidence {
    param(
        [Parameter(Mandatory = $true)] [string] $ResultsDirectory,
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [string[]] $SensitiveValues
    )

    if ($SensitiveValues.Count -eq 0) {
        return [ordered]@{ applied = $false; files = 0 }
    }

    $files = New-Object 'System.Collections.Generic.List[IO.FileInfo]'
    foreach ($item in @(Get-ChildItem -LiteralPath $ResultsDirectory -Recurse -Force | Sort-Object FullName)) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw [InvalidDataException]::new('Sensitive test evidence cannot contain a reparse point.')
        }
        if ($item -is [IO.FileInfo]) {
            $files.Add($item)
        }
    }

    foreach ($file in $files) {
        if ($file.Length -gt 67108864) {
            throw [InvalidDataException]::new('Sensitive test evidence exceeds the 64 MiB sanitation limit.')
        }
        if (@('.trx', '.xml', '.json', '.log', '.txt') -cnotcontains $file.Extension.ToLowerInvariant()) {
            throw [InvalidDataException]::new('Sensitive test evidence contains an unsupported attachment type.')
        }

        $reader = [IO.StreamReader]::new($file.FullName, [Text.Encoding]::UTF8, $true)
        try {
            $content = $reader.ReadToEnd()
            $encoding = $reader.CurrentEncoding
        }
        finally {
            $reader.Dispose()
        }
        foreach ($sensitiveValue in $SensitiveValues) {
            if (-not [string]::IsNullOrWhiteSpace($sensitiveValue)) {
                $content = $content.Replace(
                    $sensitiveValue,
                    '[sensitive]',
                    [StringComparison]::OrdinalIgnoreCase)
            }
        }

        $temporaryPath = "$($file.FullName).sanitize-$([Guid]::NewGuid().ToString('N'))"
        try {
            [IO.File]::WriteAllText($temporaryPath, $content, $encoding)
            [IO.File]::Move($temporaryPath, $file.FullName, $true)
        }
        finally {
            if ([IO.File]::Exists($temporaryPath)) {
                [IO.File]::Delete($temporaryPath)
            }
        }
    }

    return [ordered]@{ applied = $true; files = $files.Count }
}

function Read-JetSkipPolicy {
    param(
        [Parameter(Mandatory = $true)] [string] $FileName,
        [Parameter(Mandatory = $true)] [string] $PolicyName
    )

    if ($FileName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]+\.json$') {
        throw [InvalidDataException]::new('Skip-policy file name is invalid.')
    }
    $path = Join-Path $PSScriptRoot $FileName
    $policy = Get-Content -LiteralPath $path -Raw -Encoding utf8 |
        ConvertFrom-Json -AsHashtable -Depth 20

    if ([int]$policy.schemaVersion -ne 1 -or -not $policy.ContainsKey('capabilityGroups')) {
        throw [InvalidDataException]::new("$PolicyName skip policy has an unsupported schema.")
    }

    $groupNames = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    $memberNames = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($group in @($policy.capabilityGroups)) {
        $name = [string]$group.name
        if ([string]::IsNullOrWhiteSpace($name) -or -not $groupNames.Add($name)) {
            throw [InvalidDataException]::new("$PolicyName skip-policy capability names must be non-empty and unique.")
        }
        if (-not [bool]$group.uniformOutcome) {
            throw [InvalidDataException]::new("$PolicyName capability '$name' must require a uniform outcome.")
        }
        if ([string]$group.skipReason.normalization -cne 'trimCollapseWhitespaceV1' -or
            [string]$group.skipReason.normalizedSha256 -notmatch '^[0-9A-F]{64}$') {
            throw [InvalidDataException]::new("$PolicyName capability '$name' has an invalid skip-reason policy.")
        }
        $members = @($group.members)
        if ($members.Count -eq 0) {
            throw [InvalidDataException]::new("$PolicyName capability '$name' has no test members.")
        }
        foreach ($member in $members) {
            $memberName = [string]$member
            if ($memberName -notmatch '^[A-Za-z_][A-Za-z0-9_.+]+$' -or -not $memberNames.Add($memberName)) {
                throw [InvalidDataException]::new("$PolicyName skip-policy members must be valid and globally unique.")
            }
        }
    }

    return $policy
}

function Read-JetTrxResult {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $RunDirectory,
        [Parameter(Mandatory = $true)] [string] $TrxPath,
        [Parameter(Mandatory = $true)] [string] $SummaryPath
    )

    if (-not (Test-JetDescendantPath -Root $RunDirectory -Candidate $TrxPath) -or
        -not (Test-JetDescendantPath -Root $RunDirectory -Candidate $SummaryPath)) {
        throw [InvalidDataException]::new('Test evidence must remain inside its run directory.')
    }
    if (-not (Test-Path -LiteralPath $TrxPath -PathType Leaf)) {
        throw [FileNotFoundException]::new('The test process did not produce its TRX report.')
    }
    $trxInfo = Get-Item -LiteralPath $TrxPath
    if ($trxInfo.Length -le 0 -or $trxInfo.Length -gt 67108864) {
        throw [InvalidDataException]::new('TRX size is empty or exceeds the 64 MiB safety limit.')
    }

    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 67108864
    $document = [Xml.XmlDocument]::new()
    $document.XmlResolver = $null
    $reader = $null
    try {
        $reader = [Xml.XmlReader]::Create($TrxPath, $settings)
        $document.Load($reader)
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
    }

    $namespace = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespace.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $definitions = @{}
    foreach ($unitTest in @($document.SelectNodes('//t:TestDefinitions/t:UnitTest', $namespace))) {
        $id = [string]$unitTest.Attributes['id']?.Value
        $method = $unitTest.SelectSingleNode('t:TestMethod', $namespace)
        if (-not [string]::IsNullOrWhiteSpace($id) -and $null -ne $method) {
            $definitions[$id] = [pscustomobject]@{
                ClassName = [string]$method.Attributes['className']?.Value
                MethodName = [string]$method.Attributes['name']?.Value
            }
        }
    }

    $rawRecords = New-Object 'System.Collections.Generic.List[object]'
    foreach ($result in @($document.SelectNodes('//t:Results/t:UnitTestResult', $namespace))) {
        $displayName = [string]$result.Attributes['testName']?.Value
        $outcomeValue = [string]$result.Attributes['outcome']?.Value
        $testId = [string]$result.Attributes['testId']?.Value
        $definition = if ($definitions.ContainsKey($testId)) { $definitions[$testId] } else { $null }
        $className = if ($null -ne $definition) { [string]$definition.ClassName } else { '' }
        $methodName = if ($null -ne $definition) { [string]$definition.MethodName } else { '' }
        if ([string]::IsNullOrWhiteSpace($methodName)) {
            $methodName = $displayName
        }
        $fqn = if ([string]::IsNullOrWhiteSpace($className)) { $methodName } else { "$className.$methodName" }
        $outcome = switch -Regex ($outcomeValue) {
            '^(Passed|Completed)$' { 'Passed'; break }
            '^Failed$' { 'Failed'; break }
            default { 'Skipped' }
        }

        $skipReasonHash = $null
        if ($outcome -ceq 'Skipped') {
            $message = [string]$result.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $namespace)?.InnerText
            if ([string]::IsNullOrWhiteSpace($message)) {
                $message = [string]$result.SelectSingleNode('t:Output/t:StdOut', $namespace)?.InnerText
            }
            if ([string]::IsNullOrWhiteSpace($message)) {
                $messages = @($result.SelectNodes('t:Output/t:TextMessages/t:TextMessage', $namespace) |
                    ForEach-Object InnerText)
                $message = [string]($messages -join ' ')
            }
            $skipReasonHash = Get-JetTextSha256 -Value (Normalize-JetSkipReason -Reason $message)
        }

        $rawRecords.Add([pscustomobject]@{
            Fqn = $fqn
            DisplayName = $displayName
            Outcome = $outcome
            SkipReasonSha256 = $skipReasonHash
        })
    }

    $records = New-Object 'System.Collections.Generic.List[object]'
    foreach ($group in @($rawRecords | Group-Object { "$($_.Fqn)`u{001f}$($_.DisplayName)" } | Sort-Object Name)) {
        $ordinal = 0
        foreach ($item in @($group.Group | Sort-Object Outcome, Fqn, DisplayName)) {
            $ordinal++
            $identity = "$($item.Fqn)`u{001f}$($item.DisplayName)`u{001f}$ordinal"
            $records.Add([pscustomobject]@{
                identity = $identity
                fqn = $item.Fqn
                displayName = $item.DisplayName
                ordinal = $ordinal
                outcome = $item.Outcome
                skipReasonSha256 = $item.SkipReasonSha256
            })
        }
    }

    $recordArray = $records.ToArray()
    $passed = @($recordArray | Where-Object outcome -CEQ 'Passed').Count
    $failed = @($recordArray | Where-Object outcome -CEQ 'Failed').Count
    $skipped = @($recordArray | Where-Object outcome -CEQ 'Skipped').Count
    $outcomeLines = @($recordArray | ForEach-Object { "$($_.identity)`t$($_.outcome)" } | Sort-Object)
    $summary = [ordered]@{
        schemaVersion = 1
        counters = [ordered]@{
            total = $recordArray.Count
            executed = $passed + $failed
            passed = $passed
            failed = $failed
            skipped = $skipped
        }
        inventorySha256 = Get-JetTextSha256 -Value ((@($recordArray.identity | Sort-Object) -join "`n") + "`n")
        outcomeSha256 = Get-JetTextSha256 -Value (($outcomeLines -join "`n") + "`n")
        trxSha256 = (Get-FileHash -LiteralPath $TrxPath -Algorithm SHA256).Hash.ToUpperInvariant()
        records = $recordArray
        privateData = [ordered]@{ pathInspected = $false }
    }
    Write-JetAtomicJson -Path $SummaryPath -Value $summary

    return [pscustomobject]@{
        Summary = $summary
        Records = $recordArray
        Receipt = [ordered]@{
            counters = $summary.counters
            inventorySha256 = $summary.inventorySha256
            outcomeSha256 = $summary.outcomeSha256
            trxSha256 = $summary.trxSha256
            trx = Get-JetRelativePath -RepositoryRoot $RepositoryRoot -Path $TrxPath
            summary = Get-JetRelativePath -RepositoryRoot $RepositoryRoot -Path $SummaryPath
            failedTests = @($recordArray | Where-Object outcome -CEQ 'Failed' | Select-Object -First 20 -ExpandProperty fqn)
            skippedTests = @($recordArray | Where-Object outcome -CEQ 'Skipped' | Select-Object -First 20 | ForEach-Object {
                    [ordered]@{ fqn = $_.fqn; reasonSha256 = $_.skipReasonSha256 }
                })
        }
    }
}

function Test-JetSkipPolicy {
    param(
        [Parameter(Mandatory = $true)] [object[]] $Records,
        [Parameter(Mandatory = $true)] $Policy
    )

    $allowedMembers = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($group in @($Policy.capabilityGroups)) {
        foreach ($member in @($group.members)) {
            [void]$allowedMembers.Add([string]$member)
        }
    }

    $unexpectedSkips = @($Records |
        Where-Object { $_.outcome -ceq 'Skipped' -and -not $allowedMembers.Contains([string]$_.fqn) } |
        Select-Object -ExpandProperty fqn -Unique)
    $capabilities = New-Object 'System.Collections.Generic.List[object]'
    $policyErrors = New-Object 'System.Collections.Generic.List[string]'
    if ($unexpectedSkips.Count -gt 0) {
        $policyErrors.Add('unexpected_skip')
    }

    foreach ($group in @($Policy.capabilityGroups)) {
        $memberRecords = New-Object 'System.Collections.Generic.List[object]'
        foreach ($member in @($group.members)) {
            $matches = @($Records | Where-Object fqn -CEQ ([string]$member))
            if ($matches.Count -ne 1) {
                $policyErrors.Add("capability_member_count:$($group.name)")
            }
            foreach ($match in $matches) {
                $memberRecords.Add($match)
            }
        }

        $outcomes = @($memberRecords | Select-Object -ExpandProperty outcome -Unique)
        $state = if ($memberRecords.Count -ne @($group.members).Count) {
            'invalid'
        }
        elseif ($outcomes.Count -eq 1 -and $outcomes[0] -ceq 'Passed') {
            'available'
        }
        elseif ($outcomes.Count -eq 1 -and $outcomes[0] -ceq 'Skipped') {
            $reasonMismatches = @($memberRecords | Where-Object {
                    [string]$_.skipReasonSha256 -cne [string]$group.skipReason.normalizedSha256
                })
            if ($reasonMismatches.Count -eq 0) { 'unavailable' } else { 'invalid' }
        }
        else {
            'invalid'
        }

        if ($state -ceq 'invalid') {
            $policyErrors.Add("capability_outcome:$($group.name)")
        }
        $capabilities.Add([ordered]@{
            name = [string]$group.name
            state = $state
            members = @($group.members).Count
        })
    }

    return [pscustomobject]@{
        Status = if ($policyErrors.Count -eq 0) { 'passed' } else { 'failed' }
        Reason = if ($policyErrors.Count -eq 0) { $null } else { [string]$policyErrors[0] }
        Errors = $policyErrors.ToArray()
        UnexpectedSkips = $unexpectedSkips
        Capabilities = $capabilities.ToArray()
    }
}

function Invoke-JetChildStep {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $RunDirectory,
        [Parameter(Mandatory = $true)] [string] $Name,
        [Parameter(Mandatory = $true)] [string] $FileName,
        [Parameter(Mandatory = $true)] [string[]] $Arguments,
        [Parameter(Mandatory = $true)] [string[]] $DisplayCommand,
        [Parameter(Mandatory = $true)] [int] $MaximumCapturedBytes,
        [Parameter(Mandatory = $true)] [int] $TimeoutSeconds,
        [AllowNull()] [string] $WorkingDirectory = $null,
        [string[]] $EnvironmentVariablesToRemove = @(),
        [hashtable] $EnvironmentVariablesToSet = @{},
        [string[]] $SensitiveValuesToRedact = @(),
        [int] $StandardStreamCodePage = 65001,
        [switch] $TreatNuGetSourceFailureAsBlocked
    )

    $startedUtc = [DateTime]::UtcNow
    $stdoutPath = Join-Path $RunDirectory "$Name.stdout.log"
    $stderrPath = Join-Path $RunDirectory "$Name.stderr.log"
    $userProfileDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    $environmentOverrides = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $EnvironmentVariablesToSet.GetEnumerator()) {
        if ([string]::IsNullOrWhiteSpace([string]$entry.Key) -or $null -eq $entry.Value) {
            throw [ArgumentException]::new('Environment-variable overrides must contain valid names and values.')
        }
        $environmentOverrides.Add([string]$entry.Key, [string]$entry.Value)
    }

    $effectiveWorkingDirectory = if ([string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $RepositoryRoot
    }
    else {
        [IO.Path]::GetFullPath($WorkingDirectory)
    }

    [string[]]$environmentRemovalNames = [string[]]::new(0)
    if ($null -ne $EnvironmentVariablesToRemove -and $EnvironmentVariablesToRemove.Count -gt 0) {
        $environmentRemovalNames = [string[]]@($EnvironmentVariablesToRemove)
    }
    [string[]]$redactionValues = [string[]]::new(0)
    if ($null -ne $SensitiveValuesToRedact -and $SensitiveValuesToRedact.Count -gt 0) {
        $redactionValues = [string[]]@($SensitiveValuesToRedact)
    }

    $result = [Jet.Harness.BoundedProcessRunner]::Run(
        $FileName,
        $Arguments,
        $effectiveWorkingDirectory,
        $userProfileDirectory,
        $stdoutPath,
        $stderrPath,
        $MaximumCapturedBytes,
        [TimeSpan]::FromSeconds($TimeoutSeconds),
        $environmentRemovalNames,
        $environmentOverrides,
        $redactionValues,
        $StandardStreamCodePage)
    $completedUtc = [DateTime]::UtcNow

    $status = if ($result.TimedOut -or $result.Cancelled) {
        'blocked'
    }
    elseif ($result.HasExitCode -and $result.ExitCode -eq 0) {
        'passed'
    }
    else {
        'failed'
    }

    $classificationReason = $null
    if ($status -ceq 'failed' -and $TreatNuGetSourceFailureAsBlocked -and
        (Test-JetNuGetSourceUnavailable -Paths @($stdoutPath, $stderrPath))) {
        $status = 'blocked'
        $classificationReason = 'nuget_source_unavailable'
    }

    return [ordered]@{
        name = $Name
        status = $status
        command = $DisplayCommand
        startedUtc = $startedUtc.ToString('O')
        completedUtc = $completedUtc.ToString('O')
        durationSeconds = [Math]::Round(($completedUtc - $startedUtc).TotalSeconds, 3)
        classification = [ordered]@{
            reason = $classificationReason
        }
        process = [ordered]@{
            processId = $result.ProcessId
            hasExitCode = $result.HasExitCode
            exitCode = if ($result.HasExitCode) { $result.ExitCode } else { $null }
            timedOut = $result.TimedOut
            cancelled = $result.Cancelled
            killAttempted = $result.KillAttempted
            killSucceeded = $result.KillSucceeded
            killError = $result.KillError
        }
        stdout = [ordered]@{
            path = Get-JetRelativePath -RepositoryRoot $RepositoryRoot -Path $stdoutPath
            bytes = $result.StandardOutputBytes
            truncated = $result.StandardOutputTruncated
            sourceCodePage = $StandardStreamCodePage
        }
        stderr = [ordered]@{
            path = Get-JetRelativePath -RepositoryRoot $RepositoryRoot -Path $stderrPath
            bytes = $result.StandardErrorBytes
            truncated = $result.StandardErrorTruncated
            sourceCodePage = $StandardStreamCodePage
        }
    }
}

function Invoke-JetBuildPipeline {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $RunDirectory,
        [Parameter(Mandatory = $true)] [string] $Configuration,
        [Parameter(Mandatory = $true)] [bool] $SkipRestore,
        [Parameter(Mandatory = $true)] $Registry,
        [Parameter(Mandatory = $true)] [int] $TimeoutSeconds
    )

    $results = New-Object 'System.Collections.Generic.List[object]'
    $dotnet = Get-Command dotnet -CommandType Application -ErrorAction Stop |
        Select-Object -First 1 -ExpandProperty Source
    if (-not $SkipRestore) {
        $restore = Invoke-JetChildStep `
            -RepositoryRoot $RepositoryRoot `
            -RunDirectory $RunDirectory `
            -Name 'dotnet-restore' `
            -FileName $dotnet `
            -Arguments @('restore', (Join-Path $RepositoryRoot 'src/JET/JET.slnx')) `
            -DisplayCommand @('dotnet', 'restore', 'src/JET/JET.slnx') `
            -MaximumCapturedBytes ([int]$Registry.limits.maximumCapturedBytesPerStream) `
            -TimeoutSeconds $TimeoutSeconds `
            -EnvironmentVariablesToRemove @($Registry.testSettings.environmentVariablesToRemove) `
            -TreatNuGetSourceFailureAsBlocked
        $results.Add($restore)
    }

    if ($results.Count -eq 0 -or $results[$results.Count - 1].status -ceq 'passed') {
        $build = Invoke-JetChildStep `
            -RepositoryRoot $RepositoryRoot `
            -RunDirectory $RunDirectory `
            -Name 'dotnet-build' `
            -FileName $dotnet `
            -Arguments @(
                'build', (Join-Path $RepositoryRoot 'src/JET/JET.slnx'),
                '--configuration', $Configuration,
                '--no-restore'
            ) `
            -DisplayCommand @(
                'dotnet', 'build', 'src/JET/JET.slnx',
                '--configuration', $Configuration, '--no-restore'
            ) `
            -MaximumCapturedBytes ([int]$Registry.limits.maximumCapturedBytesPerStream) `
            -TimeoutSeconds $TimeoutSeconds `
            -EnvironmentVariablesToRemove @($Registry.testSettings.environmentVariablesToRemove)
        $results.Add($build)
    }

    return $results.ToArray()
}

function Invoke-JetGuiBuildPipeline {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $RunDirectory,
        [Parameter(Mandatory = $true)] $Registry,
        [Parameter(Mandatory = $true)] [int] $TimeoutSeconds
    )

    $results = New-Object 'System.Collections.Generic.List[object]'
    $dotnet = Get-Command dotnet -CommandType Application -ErrorAction Stop |
        Select-Object -First 1 -ExpandProperty Source
    $applicationProject = Join-Path $RepositoryRoot ([string]$Registry.guiSettings.applicationProject)
    $driverProject = Join-Path $RepositoryRoot ([string]$Registry.guiSettings.driverProject)

    foreach ($restore in @(
            [ordered]@{ Name = 'gui-app-restore'; Project = $applicationProject; Display = [string]$Registry.guiSettings.applicationProject },
            [ordered]@{ Name = 'gui-driver-restore'; Project = $driverProject; Display = [string]$Registry.guiSettings.driverProject })) {
        if ($results.Count -gt 0 -and $results[$results.Count - 1].status -cne 'passed') {
            break
        }

        $step = Invoke-JetChildStep `
            -RepositoryRoot $RepositoryRoot `
            -RunDirectory $RunDirectory `
            -Name ([string]$restore.Name) `
            -FileName $dotnet `
            -Arguments @('restore', [string]$restore.Project) `
            -DisplayCommand @('dotnet', 'restore', [string]$restore.Display) `
            -MaximumCapturedBytes ([int]$Registry.limits.maximumCapturedBytesPerStream) `
            -TimeoutSeconds $TimeoutSeconds `
            -EnvironmentVariablesToRemove @($Registry.testSettings.environmentVariablesToRemove) `
            -TreatNuGetSourceFailureAsBlocked
        $results.Add($step)
    }

    if ($results.Count -eq 0 -or $results[$results.Count - 1].status -ceq 'passed') {
        $applicationBuild = Invoke-JetChildStep `
            -RepositoryRoot $RepositoryRoot `
            -RunDirectory $RunDirectory `
            -Name 'gui-app-build' `
            -FileName $dotnet `
            -Arguments @(
                'build', $applicationProject,
                '--configuration', ([string]$Registry.guiSettings.configuration),
                '--no-restore'
            ) `
            -DisplayCommand @(
                'dotnet', 'build', ([string]$Registry.guiSettings.applicationProject),
                '--configuration', ([string]$Registry.guiSettings.configuration), '--no-restore'
            ) `
            -MaximumCapturedBytes ([int]$Registry.limits.maximumCapturedBytesPerStream) `
            -TimeoutSeconds $TimeoutSeconds `
            -EnvironmentVariablesToRemove @($Registry.testSettings.environmentVariablesToRemove)
        $results.Add($applicationBuild)
    }

    if ($results[$results.Count - 1].status -ceq 'passed') {
        $driverBuild = Invoke-JetChildStep `
            -RepositoryRoot $RepositoryRoot `
            -RunDirectory $RunDirectory `
            -Name 'gui-driver-build' `
            -FileName $dotnet `
            -Arguments @('build', $driverProject, '--configuration', 'Release', '--no-restore') `
            -DisplayCommand @(
                'dotnet', 'build', ([string]$Registry.guiSettings.driverProject),
                '--configuration', 'Release', '--no-restore'
            ) `
            -MaximumCapturedBytes ([int]$Registry.limits.maximumCapturedBytesPerStream) `
            -TimeoutSeconds $TimeoutSeconds `
            -EnvironmentVariablesToRemove @($Registry.testSettings.environmentVariablesToRemove)
        $results.Add($driverBuild)
    }

    return $results.ToArray()
}

function Invoke-JetExcelBuildPipeline {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $RunDirectory,
        [Parameter(Mandatory = $true)] $Registry,
        [Parameter(Mandatory = $true)] [int] $TimeoutSeconds
    )

    $results = New-Object 'System.Collections.Generic.List[object]'
    $dotnet = Get-Command dotnet -CommandType Application -ErrorAction Stop |
        Select-Object -First 1 -ExpandProperty Source
    $solution = Join-Path $RepositoryRoot 'src/JET/JET.slnx'
    $driverProject = Join-Path $RepositoryRoot ([string]$Registry.excelSettings.driverProject)

    foreach ($restore in @(
            [ordered]@{ Name = 'excel-solution-restore'; Project = $solution; Display = 'src/JET/JET.slnx' },
            [ordered]@{ Name = 'excel-driver-restore'; Project = $driverProject; Display = [string]$Registry.excelSettings.driverProject })) {
        if ($results.Count -gt 0 -and $results[$results.Count - 1].status -cne 'passed') {
            break
        }

        $step = Invoke-JetChildStep `
            -RepositoryRoot $RepositoryRoot `
            -RunDirectory $RunDirectory `
            -Name ([string]$restore.Name) `
            -FileName $dotnet `
            -Arguments @('restore', [string]$restore.Project) `
            -DisplayCommand @('dotnet', 'restore', [string]$restore.Display) `
            -MaximumCapturedBytes ([int]$Registry.limits.maximumCapturedBytesPerStream) `
            -TimeoutSeconds $TimeoutSeconds `
            -EnvironmentVariablesToRemove @($Registry.testSettings.environmentVariablesToRemove) `
            -TreatNuGetSourceFailureAsBlocked
        $results.Add($step)
    }

    if ($results.Count -eq 0 -or $results[$results.Count - 1].status -ceq 'passed') {
        $solutionBuild = Invoke-JetChildStep `
            -RepositoryRoot $RepositoryRoot `
            -RunDirectory $RunDirectory `
            -Name 'excel-solution-build' `
            -FileName $dotnet `
            -Arguments @(
                'build', $solution,
                '--configuration', ([string]$Registry.excelSettings.configuration),
                '--no-restore'
            ) `
            -DisplayCommand @(
                'dotnet', 'build', 'src/JET/JET.slnx',
                '--configuration', ([string]$Registry.excelSettings.configuration), '--no-restore'
            ) `
            -MaximumCapturedBytes ([int]$Registry.limits.maximumCapturedBytesPerStream) `
            -TimeoutSeconds $TimeoutSeconds `
            -EnvironmentVariablesToRemove @($Registry.testSettings.environmentVariablesToRemove)
        $results.Add($solutionBuild)
    }

    if ($results[$results.Count - 1].status -ceq 'passed') {
        $driverBuild = Invoke-JetChildStep `
            -RepositoryRoot $RepositoryRoot `
            -RunDirectory $RunDirectory `
            -Name 'excel-driver-build' `
            -FileName $dotnet `
            -Arguments @(
                'build', $driverProject,
                '--configuration', ([string]$Registry.excelSettings.configuration), '--no-restore'
            ) `
            -DisplayCommand @(
                'dotnet', 'build', ([string]$Registry.excelSettings.driverProject),
                '--configuration', ([string]$Registry.excelSettings.configuration), '--no-restore'
            ) `
            -MaximumCapturedBytes ([int]$Registry.limits.maximumCapturedBytesPerStream) `
            -TimeoutSeconds $TimeoutSeconds `
            -EnvironmentVariablesToRemove @($Registry.testSettings.environmentVariablesToRemove)
        $results.Add($driverBuild)
    }

    return $results.ToArray()
}

function New-JetTestArguments {
    param(
        [Parameter(Mandatory = $true)] [string] $ResultsDirectory,
        [Parameter(Mandatory = $true)] [int] $MinimumExpectedTests,
        [Parameter(Mandatory = $true)] $Registry,
        [ValidateSet('None', 'Method', 'Namespace', 'Trait')] [string] $IncludeKind = 'None',
        [string[]] $IncludeValues = @(),
        [AllowNull()] [string[]] $ExcludedProfiles = $null,
        [AllowNull()] [string[]] $ProtectedMethodPatterns = $null
    )

    $arguments = New-Object 'System.Collections.Generic.List[string]'
    foreach ($argument in @(
            '--results-directory', $ResultsDirectory,
            '--report-trx',
            '--report-trx-filename', 'result.trx',
            '--minimum-expected-tests', ([string]$MinimumExpectedTests),
            '--zero-tests-policy', 'strict',
            '--no-ansi',
            '--progress', 'off',
            '--auto-reporters', 'off',
            '--output', 'Normal',
            '--show-stdout', 'Failed',
            '--show-stderr', 'Failed',
            '--parallel', 'none',
            '--seed', ([string]$Registry.testSettings.seed))) {
        $arguments.Add([string]$argument)
    }

    if ($IncludeKind -cne 'None') {
        if ($IncludeValues.Count -eq 0) {
            throw [ArgumentException]::new('An included test filter requires at least one value.')
        }
        $includeSwitch = switch -CaseSensitive ($IncludeKind) {
            'Method' { '--filter-method' }
            'Namespace' { '--filter-namespace' }
            'Trait' { '--filter-trait' }
        }
        $arguments.Add($includeSwitch)
        foreach ($value in $IncludeValues) {
            $arguments.Add([string]$value)
        }
    }

    if ($null -eq $ExcludedProfiles) {
        $ExcludedProfiles = @($Registry.testSettings.excludedProfiles)
    }
    if ($null -eq $ProtectedMethodPatterns) {
        $ProtectedMethodPatterns = @($Registry.testSettings.protectedWorkspaceMethodPatterns)
    }
    if ($ExcludedProfiles.Count -gt 0) {
        $arguments.Add('--filter-not-trait')
        foreach ($profile in $ExcludedProfiles) {
            $arguments.Add("TestProfile=$profile")
        }
    }
    if ($ProtectedMethodPatterns.Count -gt 0) {
        $arguments.Add('--filter-not-method')
        foreach ($pattern in $ProtectedMethodPatterns) {
            $arguments.Add([string]$pattern)
        }
    }
    return $arguments.ToArray()
}

function Invoke-JetTestStep {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $RunDirectory,
        [Parameter(Mandatory = $true)] [string] $Configuration,
        [Parameter(Mandatory = $true)] [string] $Name,
        [Parameter(Mandatory = $true)] [int] $MinimumExpectedTests,
        [Parameter(Mandatory = $true)] $Registry,
        [Parameter(Mandatory = $true)] [int] $TimeoutSeconds,
        [ValidateSet('None', 'Method', 'Namespace', 'Trait')] [string] $IncludeKind = 'None',
        [string[]] $IncludeValues = @(),
        [AllowNull()] [string[]] $ExcludedProfiles = $null,
        [AllowNull()] [string[]] $ProtectedMethodPatterns = $null,
        [AllowNull()] [string[]] $EnvironmentVariablesToRemove = $null,
        [hashtable] $EnvironmentVariablesToSet = @{},
        [string[]] $SensitiveValuesToRedact = @(),
        [ValidateSet('NoSkips', 'BlockOnSkip', 'ProviderRequired', 'PublicPolicy', 'ProviderPolicy')]
        [string] $SkipPolicy = 'NoSkips'
    )

    $testExecutableRelative = "src/JET/tests/JET.Tests/bin/$Configuration/$($Registry.testSettings.targetFramework)/JET.Tests.exe"
    $testExecutable = Join-Path $RepositoryRoot $testExecutableRelative
    Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $testExecutable
    if (-not (Test-Path -LiteralPath $testExecutable -PathType Leaf)) {
        throw [FileNotFoundException]::new('The current build did not produce the expected JET.Tests executable.')
    }

    $resultsDirectory = Join-Path $RunDirectory "test-results/$Name"
    Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $resultsDirectory
    [void][IO.Directory]::CreateDirectory($resultsDirectory)
    Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $resultsDirectory
    $arguments = New-JetTestArguments `
        -ResultsDirectory $resultsDirectory `
        -MinimumExpectedTests $MinimumExpectedTests `
        -Registry $Registry `
        -IncludeKind $IncludeKind `
        -IncludeValues $IncludeValues `
        -ExcludedProfiles $ExcludedProfiles `
        -ProtectedMethodPatterns $ProtectedMethodPatterns
    if ($null -eq $EnvironmentVariablesToRemove) {
        $EnvironmentVariablesToRemove = @($Registry.testSettings.environmentVariablesToRemove)
    }
    $displayArguments = foreach ($argument in $arguments) {
        if ($argument -ceq $resultsDirectory) {
            Get-JetRelativePath -RepositoryRoot $RepositoryRoot -Path $resultsDirectory
        }
        else {
            $argument
        }
    }
    $step = Invoke-JetChildStep `
        -RepositoryRoot $RepositoryRoot `
        -RunDirectory $RunDirectory `
        -Name $Name `
        -FileName $testExecutable `
        -Arguments $arguments `
        -DisplayCommand (@($testExecutableRelative) + @($displayArguments)) `
        -MaximumCapturedBytes ([int]$Registry.limits.maximumCapturedBytesPerStream) `
        -TimeoutSeconds $TimeoutSeconds `
        -EnvironmentVariablesToRemove $EnvironmentVariablesToRemove `
        -EnvironmentVariablesToSet $EnvironmentVariablesToSet `
        -SensitiveValuesToRedact $SensitiveValuesToRedact `
        -StandardStreamCodePage ([Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage)

    $trxPath = Join-Path $resultsDirectory 'result.trx'
    $summaryPath = Join-Path $resultsDirectory 'test-summary.json'
    try {
        $step['evidenceSanitization'] = Protect-JetSensitiveTestEvidence `
            -ResultsDirectory $resultsDirectory `
            -SensitiveValues $SensitiveValuesToRedact
        $parsed = Read-JetTrxResult `
            -RepositoryRoot $RepositoryRoot `
            -RunDirectory $RunDirectory `
            -TrxPath $trxPath `
            -SummaryPath $summaryPath
        $step['test'] = $parsed.Receipt

        if ($parsed.Summary.counters.total -lt $MinimumExpectedTests) {
            $step.status = 'failed'
            $step.classification.reason = 'test_count_below_minimum'
        }
        elseif ($parsed.Summary.counters.failed -gt 0 -and $step.status -ceq 'passed') {
            $step.status = 'failed'
            $step.classification.reason = 'trx_contains_failed_tests'
        }

        switch -CaseSensitive ($SkipPolicy) {
            'NoSkips' {
                if ($parsed.Summary.counters.skipped -gt 0 -and $step.status -ceq 'passed') {
                    $step.status = 'failed'
                    $step.classification.reason = 'required_test_skipped'
                }
            }
            'BlockOnSkip' {
                if ($parsed.Summary.counters.skipped -gt 0 -and $parsed.Summary.counters.failed -eq 0) {
                    $step.status = 'blocked'
                    $step.classification.reason = 'focused_selection_skipped'
                }
            }
            'ProviderRequired' {
                if ($parsed.Summary.counters.skipped -gt 0 -and $parsed.Summary.counters.failed -eq 0) {
                    $step.status = 'blocked'
                    $step.classification.reason = 'sqlserver_capability_unavailable'
                }
            }
            'PublicPolicy' {
                $assessment = Test-JetSkipPolicy `
                    -Records $parsed.Records `
                    -Policy (Read-JetSkipPolicy `
                        -FileName ([string]$Registry.testSettings.publicSkipPolicy) `
                        -PolicyName 'Public')
                $step.test['skipPolicy'] = [ordered]@{
                    status = $assessment.Status
                    reason = $assessment.Reason
                    errors = $assessment.Errors
                    unexpectedSkips = $assessment.UnexpectedSkips
                    capabilities = $assessment.Capabilities
                }
                if ($assessment.Status -cne 'passed' -and $step.status -ceq 'passed') {
                    $step.status = 'failed'
                    $step.classification.reason = $assessment.Reason
                }
            }
            'ProviderPolicy' {
                $assessment = Test-JetSkipPolicy `
                    -Records $parsed.Records `
                    -Policy (Read-JetSkipPolicy `
                        -FileName ([string]$Registry.testSettings.providerSkipPolicy) `
                        -PolicyName 'Provider')
                $step.test['skipPolicy'] = [ordered]@{
                    status = $assessment.Status
                    reason = $assessment.Reason
                    errors = $assessment.Errors
                    unexpectedSkips = $assessment.UnexpectedSkips
                    capabilities = $assessment.Capabilities
                }
                if ($assessment.Status -cne 'passed' -and $step.status -ceq 'passed') {
                    $step.status = 'failed'
                    $step.classification.reason = $assessment.Reason
                }
            }
        }
    }
    catch {
        $step['test'] = [ordered]@{
            reportStatus = 'invalid'
            errorType = $_.Exception.GetType().Name
        }
        $step.status = 'infrastructure_error'
        $step.classification.reason = 'test_report_invalid'
    }

    return $step
}

function New-JetSyntheticStep {
    param(
        [Parameter(Mandatory = $true)] [string] $Name,
        [Parameter(Mandatory = $true)]
        [ValidateSet('passed', 'failed', 'blocked', 'infrastructure_error')]
        [string] $Status,
        [AllowNull()] [string] $Reason,
        $Evidence = $null
    )

    $now = [DateTime]::UtcNow.ToString('O')
    return [ordered]@{
        name = $Name
        status = $Status
        command = @()
        startedUtc = $now
        completedUtc = $now
        durationSeconds = 0
        classification = [ordered]@{ reason = $Reason }
        process = [ordered]@{
            processId = $null
            hasExitCode = $false
            exitCode = $null
            timedOut = $false
            cancelled = $false
            killAttempted = $false
            killSucceeded = $false
            killError = $null
        }
        evidence = $Evidence
    }
}

function Get-JetProviderConnectionPreflight {
    param([Parameter(Mandatory = $true)] $Registry)

    $variableName = [string]$Registry.testSettings.providerConnectionEnvironmentVariable
    $connectionString = [Environment]::GetEnvironmentVariable(
        $variableName,
        [EnvironmentVariableTarget]::Process)
    if ([string]::IsNullOrWhiteSpace($connectionString)) {
        return [pscustomobject]@{
            Step = New-JetSyntheticStep `
                -Name 'provider-connection-preflight' `
                -Status 'blocked' `
                -Reason 'sqlserver_connection_not_configured' `
                -Evidence ([ordered]@{ variable = $variableName; configured = $false })
            SensitiveValues = @()
        }
    }

    try {
        $builder = [Data.Common.DbConnectionStringBuilder]::new()
        $builder.ConnectionString = $connectionString
        if ($builder.Count -eq 0) {
            throw [FormatException]::new('Connection string has no entries.')
        }

        $redactions = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        [void]$redactions.Add($connectionString)
        if (-not [string]::IsNullOrWhiteSpace($builder.ConnectionString)) {
            [void]$redactions.Add($builder.ConnectionString)
        }
        foreach ($keyObject in $builder.Keys) {
            $key = [string]$keyObject
            $value = [string]$builder[$keyObject]
            if ([string]::IsNullOrWhiteSpace($value)) {
                continue
            }
            [void]$redactions.Add("$key=$value")
            [void]$redactions.Add("$key = $value")
            if ($value.Length -ge 4 -and $key -match '^(?i:data source|server|address|addr|network address|initial catalog|database|user id|uid|password|pwd|attachdbfilename|application name)$') {
                [void]$redactions.Add($value)
            }
        }

        return [pscustomobject]@{
            Step = New-JetSyntheticStep `
                -Name 'provider-connection-preflight' `
                -Status 'passed' `
                -Reason $null `
                -Evidence ([ordered]@{ variable = $variableName; configured = $true; syntax = 'parsed' })
            SensitiveValues = @($redactions | Sort-Object)
        }
    }
    catch {
        return [pscustomobject]@{
            Step = New-JetSyntheticStep `
                -Name 'provider-connection-preflight' `
                -Status 'failed' `
                -Reason 'provider_connection_invalid' `
                -Evidence ([ordered]@{ variable = $variableName; configured = $true; syntax = 'invalid' })
            SensitiveValues = @($connectionString)
        }
    }
}

function Get-JetPrivateCasePreflight {
    param([Parameter(Mandatory = $true)] $Registry)

    $rootVariable = [string]$Registry.testSettings.privateCaseRootEnvironmentVariable
    $manifestVariable = [string]$Registry.testSettings.privateCaseManifestEnvironmentVariable
    $providerVariable = [string]$Registry.testSettings.privateCaseProviderEnvironmentVariable
    $connectionVariable = [string]$Registry.testSettings.providerConnectionEnvironmentVariable
    $rootValue = [Environment]::GetEnvironmentVariable(
        $rootVariable,
        [EnvironmentVariableTarget]::Process)
    $manifestValue = [Environment]::GetEnvironmentVariable(
        $manifestVariable,
        [EnvironmentVariableTarget]::Process)
    $providerValue = [Environment]::GetEnvironmentVariable(
        $providerVariable,
        [EnvironmentVariableTarget]::Process)

    $sensitiveValues = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($value in @($rootValue, $manifestValue)) {
        if (-not [string]::IsNullOrWhiteSpace([string]$value)) {
            [void]$sensitiveValues.Add([string]$value)
        }
    }

    $configured = [ordered]@{
        root = -not [string]::IsNullOrWhiteSpace($rootValue)
        manifest = -not [string]::IsNullOrWhiteSpace($manifestValue)
        provider = -not [string]::IsNullOrWhiteSpace($providerValue)
    }
    if (-not $configured.root -or -not $configured.manifest -or -not $configured.provider) {
        return [pscustomobject]@{
            Step = New-JetSyntheticStep `
                -Name 'private-case-preflight' `
                -Status 'blocked' `
                -Reason 'private_case_input_not_configured' `
                -Evidence ([ordered]@{ configured = $configured; pathsRecorded = $false })
            EnvironmentVariablesToSet = @{}
            SensitiveValues = @($sensitiveValues | Sort-Object)
            PathInspected = $false
            Provider = $null
        }
    }

    $provider = $providerValue.Trim().ToLowerInvariant()
    if ($provider -notin @('sqlite', 'duckdb', 'sqlserver')) {
        return [pscustomobject]@{
            Step = New-JetSyntheticStep `
                -Name 'private-case-preflight' `
                -Status 'failed' `
                -Reason 'private_case_provider_invalid' `
                -Evidence ([ordered]@{ configured = $configured; providerAccepted = $false; pathsRecorded = $false })
            EnvironmentVariablesToSet = @{}
            SensitiveValues = @($sensitiveValues | Sort-Object)
            PathInspected = $false
            Provider = $null
        }
    }

    $connectionValue = $null
    if ($provider -ceq 'sqlserver') {
        $connectionPreflight = Get-JetProviderConnectionPreflight -Registry $Registry
        foreach ($value in @($connectionPreflight.SensitiveValues)) {
            if (-not [string]::IsNullOrWhiteSpace([string]$value)) {
                [void]$sensitiveValues.Add([string]$value)
            }
        }
        if ([string]$connectionPreflight.Step.status -cne 'passed') {
            return [pscustomobject]@{
                Step = New-JetSyntheticStep `
                    -Name 'private-case-preflight' `
                    -Status ([string]$connectionPreflight.Step.status) `
                    -Reason ([string]$connectionPreflight.Step.classification.reason) `
                    -Evidence ([ordered]@{
                        configured = $configured
                        provider = 'sqlserver'
                        connectionConfigured = $false
                        pathsRecorded = $false
                    })
                EnvironmentVariablesToSet = @{}
                SensitiveValues = @($sensitiveValues | Sort-Object)
                PathInspected = $false
                Provider = 'sqlserver'
            }
        }
        $connectionValue = [Environment]::GetEnvironmentVariable(
            $connectionVariable,
            [EnvironmentVariableTarget]::Process)
    }

    try {
        if (-not [IO.Path]::IsPathFullyQualified($rootValue)) {
            throw [ArgumentException]::new('PrivateCase root must be fully qualified.')
        }
        $rootFull = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($rootValue))
        [void]$sensitiveValues.Add($rootFull)
    }
    catch {
        return [pscustomobject]@{
            Step = New-JetSyntheticStep `
                -Name 'private-case-preflight' `
                -Status 'failed' `
                -Reason 'private_case_root_unavailable' `
                -Evidence ([ordered]@{ configured = $configured; rootAccepted = $false; pathsRecorded = $false })
            EnvironmentVariablesToSet = @{}
            SensitiveValues = @($sensitiveValues | Sort-Object)
            PathInspected = $false
            Provider = $provider
        }
    }

    try {
        if ([IO.Path]::IsPathRooted($manifestValue) -or
            $manifestValue.Length -gt 1024 -or
            $manifestValue -match '[\x00-\x1F]') {
            throw [ArgumentException]::new('PrivateCase manifest path is invalid.')
        }
        $manifestFull = [IO.Path]::GetFullPath($manifestValue, $rootFull)
        if (-not (Test-JetDescendantPath -Root $rootFull -Candidate $manifestFull)) {
            throw [ArgumentException]::new('PrivateCase manifest path must stay below the root.')
        }
        [void]$sensitiveValues.Add($manifestFull)
    }
    catch {
        return [pscustomobject]@{
            Step = New-JetSyntheticStep `
                -Name 'private-case-preflight' `
                -Status 'failed' `
                -Reason 'private_case_manifest_invalid' `
                -Evidence ([ordered]@{ configured = $configured; manifestAccepted = $false; pathsRecorded = $false })
            EnvironmentVariablesToSet = @{}
            SensitiveValues = @($sensitiveValues | Sort-Object)
            PathInspected = $false
            Provider = $provider
        }
    }

    $pathInspected = $true
    try {
        $rootItem = Get-Item -LiteralPath $rootFull -Force -ErrorAction Stop
        if (-not $rootItem.PSIsContainer) {
            throw [DirectoryNotFoundException]::new('PrivateCase root is not a directory.')
        }
        if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            return [pscustomobject]@{
                Step = New-JetSyntheticStep `
                    -Name 'private-case-preflight' `
                    -Status 'failed' `
                    -Reason 'private_case_root_reparse' `
                    -Evidence ([ordered]@{ configured = $configured; rootAccepted = $false; pathsRecorded = $false })
                EnvironmentVariablesToSet = @{}
                SensitiveValues = @($sensitiveValues | Sort-Object)
                PathInspected = $pathInspected
                Provider = $provider
            }
        }
    }
    catch {
        return [pscustomobject]@{
            Step = New-JetSyntheticStep `
                -Name 'private-case-preflight' `
                -Status 'failed' `
                -Reason 'private_case_root_unavailable' `
                -Evidence ([ordered]@{ configured = $configured; rootAccepted = $false; pathsRecorded = $false })
            EnvironmentVariablesToSet = @{}
            SensitiveValues = @($sensitiveValues | Sort-Object)
            PathInspected = $pathInspected
            Provider = $provider
        }
    }

    $environmentVariables = @{
        $rootVariable = $rootFull
        $manifestVariable = $manifestValue
        $providerVariable = $provider
    }
    if ($provider -ceq 'sqlserver') {
        $environmentVariables[$connectionVariable] = $connectionValue
    }

    return [pscustomobject]@{
        Step = New-JetSyntheticStep `
            -Name 'private-case-preflight' `
            -Status 'passed' `
            -Reason $null `
            -Evidence ([ordered]@{
                configured = $configured
                provider = $provider
                rootAccepted = $true
                manifestPath = 'relative'
                pathsRecorded = $false
            })
        EnvironmentVariablesToSet = $environmentVariables
        SensitiveValues = @($sensitiveValues | Sort-Object)
        PathInspected = $pathInspected
        Provider = $provider
    }
}

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
            '--timeout-seconds', ([string]$driverTimeout)
        ) `
        -DisplayCommand @(
            'dotnet', ([string]$Registry.guiSettings.driverAssembly),
            '--app', ([string]$Registry.guiSettings.applicationExecutable),
            '--manifest', (Get-JetRelativePath -RepositoryRoot $RepositoryRoot -Path $manifestPath),
            '--scenario', $scenarioName,
            '--timeout-seconds', ([string]$driverTimeout)
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
            [int]$manifest.budget.screenshotCount -ne 0) {
            throw [InvalidDataException]::new('GUI scenario manifest is invalid.')
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
                    [bool]$manifest.assertions.createFormVisible -and
                    [bool]$manifest.assertions.requiredFieldsEntered -and
                    [bool]$manifest.assertions.sqliteSelected -and
                    [bool]$manifest.assertions.projectCreated -and
                    [bool]$manifest.assertions.importStepVisible -and
                    [bool]$manifest.assertions.projectCodeVisible -and
                    [bool]$manifest.assertions.projectJsonExists -and
                    [bool]$manifest.assertions.sqliteDatabaseExists -and
                    [bool]$manifest.assertions.storedProjectMatches
            }
            'mapping-required-sync' {
                [int]$manifest.budget.actionCount -eq [int]$Scenario.expectedActionCount -and
                    [bool]$manifest.assertions.mappingProjectLoaded -and
                    [bool]$manifest.assertions.mappingBaselineReady -and
                    [bool]$manifest.assertions.requiredRailBecameIncomplete -and
                    [bool]$manifest.assertions.requiredRailRecovered -and
                    [bool]$manifest.assertions.mappingFocusPreserved
            }
            'conflicted-journal-recovery' {
                [int]$manifest.budget.actionCount -eq [int]$Scenario.expectedActionCount -and
                    [bool]$manifest.assertions.conflictFeedbackVisible -and
                    [bool]$manifest.assertions.supportExportAvailable -and
                    [bool]$manifest.assertions.supportLogWritten -and
                    [bool]$manifest.assertions.supportLogSafe -and
                    [bool]$manifest.assertions.conflictedProjectDeleted
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

        $step['excelFixture'] = [ordered]@{
            source = 'same_run_synthetic_six_report_journey'
            workbookCount = $artifacts.Count
            reportKinds = $expectedKinds
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

function Test-JetOrdinalSequenceEqual {
    param(
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [string[]] $Left,
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [string[]] $Right
    )

    if ($Left.Count -ne $Right.Count) {
        return $false
    }
    for ($index = 0; $index -lt $Left.Count; $index++) {
        if (-not [string]::Equals($Left[$index], $Right[$index], [StringComparison]::Ordinal)) {
            return $false
        }
    }
    return $true
}

function Invoke-JetGitLines {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [string[]] $Arguments
    )

    $git = Get-Command git -CommandType Application -ErrorAction Stop |
        Select-Object -First 1 -ExpandProperty Source
    $safeRoot = [IO.Path]::GetFullPath($RepositoryRoot).Replace('\', '/')
    $nativeArguments = @(
        '-c', "safe.directory=$safeRoot",
        '-c', 'core.excludesFile=',
        '-c', 'core.quotepath=false',
        '-C', ([IO.Path]::GetFullPath($RepositoryRoot))
    ) + $Arguments
    $output = @(& $git @nativeArguments 2>$null)
    $exitCode = $LASTEXITCODE
    return [pscustomobject]@{
        ExitCode = $exitCode
        Lines = @($output | ForEach-Object { [string]$_ })
    }
}

function Get-JetSourceIndexFingerprint {
    param([Parameter(Mandatory = $true)] [string] $RepositoryRoot)

    $stagedEntries = Invoke-JetGitLines `
        -RepositoryRoot $RepositoryRoot `
        -Arguments @('ls-files', '--stage', '--')
    if ($stagedEntries.ExitCode -ne 0) {
        throw [InvalidOperationException]::new('Source Git index inventory failed.')
    }

    $aggregate = [Security.Cryptography.IncrementalHash]::CreateHash(
        [Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        foreach ($entry in [string[]]$stagedEntries.Lines) {
            $aggregate.AppendData($script:Utf8.GetBytes("$entry`n"))
        }
        $sha256 = [Convert]::ToHexString($aggregate.GetHashAndReset()).ToLowerInvariant()
    }
    finally {
        $aggregate.Dispose()
    }

    return [pscustomobject]@{
        EntryCount = $stagedEntries.Lines.Count
        Sha256 = $sha256
    }
}

function New-JetReleaseCandidateSnapshot {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $RunId,
        [Parameter(Mandatory = $true)] $Registry
    )

    $startedUtc = [DateTime]::UtcNow
    $failureStatus = 'failed'
    $failureReason = 'candidate_manifest_invalid'
    $workspaceRoot = $null
    $snapshotRoot = $null
    $sourceIndexBefore = $null
    $sourceIndexAfter = $null
    $sourceIndexMutated = $null
    try {
        $manifestRelativePath = [string]$Registry.releaseCandidateSettings.candidateManifest
        $manifestPath = [IO.Path]::GetFullPath($manifestRelativePath, $RepositoryRoot)
        if (-not (Test-JetDescendantPath -Root $RepositoryRoot -Candidate $manifestPath) -or
            -not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
            (Get-Item -LiteralPath $manifestPath -Force).Length -gt 4194304) {
            throw [InvalidDataException]::new('Candidate manifest is unavailable or too large.')
        }

        $manifestLines = [IO.File]::ReadAllLines($manifestPath, $script:Utf8)
        if ($manifestLines.Count -eq 0) {
            throw [InvalidDataException]::new('Candidate manifest is empty.')
        }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($relativePath in $manifestLines) {
            if ([string]::IsNullOrWhiteSpace($relativePath) -or
                $relativePath -cne $relativePath.Trim() -or
                $relativePath.Contains('\', [StringComparison]::Ordinal) -or
                $relativePath -match '[\x00-\x1F\x7F]' -or
                [IO.Path]::IsPathFullyQualified($relativePath) -or
                $relativePath.StartsWith('./', [StringComparison]::Ordinal) -or
                $relativePath -match '(^|/)\.\.(/|$)' -or
                -not $seen.Add($relativePath)) {
                throw [InvalidDataException]::new('Candidate manifest contains an invalid path.')
            }

            $normalized = $relativePath.ToLowerInvariant()
            if ($normalized -eq '.git' -or
                $normalized.StartsWith('.git/', [StringComparison]::Ordinal) -or
                $normalized.StartsWith('artifacts/', [StringComparison]::Ordinal) -or
                $normalized.StartsWith('data/test-case/', [StringComparison]::Ordinal) -or
                $normalized.StartsWith('data/temporary-test-case/', [StringComparison]::Ordinal) -or
                $normalized.StartsWith('data/legacy-parity-work/', [StringComparison]::Ordinal)) {
                throw [InvalidDataException]::new('Candidate manifest includes a forbidden path class.')
            }

            $fullPath = [IO.Path]::GetFullPath($relativePath, $RepositoryRoot)
            if (-not (Test-JetDescendantPath -Root $RepositoryRoot -Candidate $fullPath) -or
                -not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
                throw [InvalidDataException]::new('Candidate manifest references an unavailable file.')
            }
            Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $fullPath
        }

        $sortedManifest = [string[]]$manifestLines.Clone()
        [Array]::Sort($sortedManifest, [StringComparer]::Ordinal)
        if (-not (Test-JetOrdinalSequenceEqual -Left $manifestLines -Right $sortedManifest)) {
            throw [InvalidDataException]::new('Candidate manifest must use ordinal path order.')
        }

        $failureStatus = 'infrastructure_error'
        $failureReason = 'candidate_git_inventory_failed'
        $sourceIndexBefore = Get-JetSourceIndexFingerprint -RepositoryRoot $RepositoryRoot

        $head = Invoke-JetGitLines -RepositoryRoot $RepositoryRoot -Arguments @('rev-parse', '--verify', 'HEAD')
        $sourceMode = if ($head.ExitCode -eq 0) { 'committed-head' } else { 'first-root-candidate' }
        $tracked = Invoke-JetGitLines -RepositoryRoot $RepositoryRoot -Arguments @('ls-files', '--cached', '--')
        if ($tracked.ExitCode -ne 0) {
            $failureStatus = 'infrastructure_error'
            $failureReason = 'candidate_git_inventory_failed'
            throw [InvalidOperationException]::new('Tracked inventory failed.')
        }

        if ($sourceMode -ceq 'committed-head') {
            $status = Invoke-JetGitLines `
                -RepositoryRoot $RepositoryRoot `
                -Arguments @('status', '--porcelain=v1', '--untracked-files=all', '--')
            if ($status.ExitCode -ne 0) {
                $failureStatus = 'infrastructure_error'
                $failureReason = 'candidate_git_inventory_failed'
                throw [InvalidOperationException]::new('Git status failed.')
            }
            if ($status.Lines.Count -ne 0) {
                $failureReason = 'candidate_source_dirty'
                throw [InvalidDataException]::new('Committed source is not clean.')
            }
            $actualPaths = [string[]]$tracked.Lines
        }
        else {
            if ($tracked.Lines.Count -ne 0) {
                $failureReason = 'candidate_source_index_not_empty'
                throw [InvalidDataException]::new('Initial source index must remain untouched.')
            }
            $untracked = Invoke-JetGitLines `
                -RepositoryRoot $RepositoryRoot `
                -Arguments @('ls-files', '--others', '--exclude-standard', '--')
            if ($untracked.ExitCode -ne 0) {
                $failureStatus = 'infrastructure_error'
                $failureReason = 'candidate_git_inventory_failed'
                throw [InvalidOperationException]::new('Untracked inventory failed.')
            }
            $actualPaths = [string[]]$untracked.Lines
        }
        [Array]::Sort($actualPaths, [StringComparer]::Ordinal)
        if (-not (Test-JetOrdinalSequenceEqual -Left $manifestLines -Right $actualPaths)) {
            $failureReason = 'candidate_manifest_mismatch'
            throw [InvalidDataException]::new('Candidate manifest does not match the source inventory.')
        }

        $workspaceRelativeRoot = [string]$Registry.releaseCandidateSettings.snapshotWorkspaceRoot
        if ([IO.Path]::IsPathRooted($workspaceRelativeRoot)) {
            throw [InvalidDataException]::new('Candidate workspace root must remain repository-relative.')
        }
        $harnessArtifactsRoot = [IO.Path]::GetFullPath('artifacts/harness', $RepositoryRoot)
        $workspaceBase = [IO.Path]::GetFullPath($workspaceRelativeRoot, $RepositoryRoot)
        if (-not (Test-JetDescendantPath -Root $harnessArtifactsRoot -Candidate $workspaceBase)) {
            throw [InvalidDataException]::new('Candidate workspace root escaped harness artifacts.')
        }
        Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $workspaceBase
        [void][IO.Directory]::CreateDirectory($workspaceBase)
        Assert-JetNoExistingReparsePoint -RepositoryRoot $RepositoryRoot -Candidate $workspaceBase

        $workspaceToken = $RunId.Substring($RunId.Length - 12)
        if ($workspaceToken -cnotmatch '^[0-9a-f]{12}$') {
            throw [InvalidDataException]::new('Candidate workspace token is invalid.')
        }
        $workspaceRoot = Join-Path $workspaceBase $workspaceToken
        if (Test-Path -LiteralPath $workspaceRoot) {
            $failureStatus = 'infrastructure_error'
            $failureReason = 'candidate_snapshot_collision'
            throw [IOException]::new('Candidate workspace already exists.')
        }
        [void][IO.Directory]::CreateDirectory($workspaceRoot)
        $workspaceOwner = [ordered]@{
            schemaVersion = 1
            kind = 'jet-harness-release-candidate-workspace'
            runId = $RunId
            processId = $PID
            createdUtc = [DateTime]::UtcNow.ToString('O')
        }
        Write-JetAtomicJson -Path (Join-Path $workspaceRoot $script:OwnerMarkerName) -Value $workspaceOwner
        $snapshotRoot = Join-Path $workspaceRoot 's'
        [void][IO.Directory]::CreateDirectory($snapshotRoot)

        $aggregate = [Security.Cryptography.IncrementalHash]::CreateHash(
            [Security.Cryptography.HashAlgorithmName]::SHA256)
        $totalBytes = [long]0
        try {
            foreach ($relativePath in $manifestLines) {
                $sourcePath = [IO.Path]::GetFullPath($relativePath, $RepositoryRoot)
                $sourceItem = Get-Item -LiteralPath $sourcePath -Force
                if ($sourceItem.Length -gt 536870912) {
                    $failureReason = 'candidate_file_too_large'
                    throw [InvalidDataException]::new('Candidate file exceeds the interactive copy limit.')
                }
                $destinationPath = [IO.Path]::GetFullPath($relativePath, $snapshotRoot)
                if (-not (Test-JetDescendantPath -Root $snapshotRoot -Candidate $destinationPath)) {
                    throw [InvalidDataException]::new('Candidate copy escaped the snapshot root.')
                }
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destinationPath))
                [IO.File]::Copy($sourcePath, $destinationPath, $false)

                $sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
                $destinationHash = (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash
                if ($sourceHash -cne $destinationHash) {
                    $failureStatus = 'infrastructure_error'
                    $failureReason = 'candidate_snapshot_copy_mismatch'
                    throw [IOException]::new('Candidate copy hash mismatch.')
                }
                $totalBytes += [long]$sourceItem.Length
                $aggregate.AppendData($script:Utf8.GetBytes(
                    "$relativePath`t$($sourceItem.Length)`t$sourceHash`n"))
            }
            $aggregateSha256 = [Convert]::ToHexString($aggregate.GetHashAndReset()).ToLowerInvariant()
        }
        finally {
            $aggregate.Dispose()
        }

        $failureStatus = 'infrastructure_error'
        $failureReason = 'candidate_snapshot_git_failed'
        $initialize = Invoke-JetGitLines `
            -RepositoryRoot $snapshotRoot `
            -Arguments @('init', '--quiet', '--initial-branch=main')
        if ($initialize.ExitCode -ne 0) {
            throw [InvalidOperationException]::new('Candidate snapshot Git initialization failed.')
        }
        $index = Invoke-JetGitLines `
            -RepositoryRoot $snapshotRoot `
            -Arguments @('add', '--intent-to-add', '--all', '--')
        if ($index.ExitCode -ne 0) {
            throw [InvalidOperationException]::new('Candidate snapshot index creation failed.')
        }
        $snapshotTracked = Invoke-JetGitLines `
            -RepositoryRoot $snapshotRoot `
            -Arguments @('ls-files', '--cached', '--')
        $snapshotPaths = [string[]]$snapshotTracked.Lines
        [Array]::Sort($snapshotPaths, [StringComparer]::Ordinal)
        if ($snapshotTracked.ExitCode -ne 0 -or
            -not (Test-JetOrdinalSequenceEqual -Left $manifestLines -Right $snapshotPaths)) {
            throw [InvalidOperationException]::new('Candidate snapshot index does not match the manifest.')
        }

        $sourceIndexAfter = Get-JetSourceIndexFingerprint -RepositoryRoot $RepositoryRoot
        $sourceIndexMutated = $sourceIndexBefore.EntryCount -ne $sourceIndexAfter.EntryCount -or
            $sourceIndexBefore.Sha256 -cne $sourceIndexAfter.Sha256
        if ($sourceIndexMutated) {
            $failureReason = 'candidate_source_index_changed'
            throw [InvalidDataException]::new('Source Git index entries changed during candidate snapshot creation.')
        }

        $completedUtc = [DateTime]::UtcNow
        $step = New-JetSyntheticStep `
            -Name 'release-candidate-snapshot' `
            -Status 'passed' `
            -Reason $null `
            -Evidence ([ordered]@{
                sourceMode = $sourceMode
                manifest = $manifestRelativePath
                fileCount = $manifestLines.Count
                bytes = $totalBytes
                aggregateSha256 = $aggregateSha256
                sourceIndexMeasured = $true
                sourceIndexComparison = 'cached-stage-entries'
                sourceIndexEntryCountBefore = $sourceIndexBefore.EntryCount
                sourceIndexEntryCountAfter = $sourceIndexAfter.EntryCount
                sourceIndexSha256Before = $sourceIndexBefore.Sha256
                sourceIndexSha256After = $sourceIndexAfter.Sha256
                sourceIndexMutated = $sourceIndexMutated
                privateDataIncluded = $false
                snapshotRetained = $true
            })
        $step.startedUtc = $startedUtc.ToString('O')
        $step.completedUtc = $completedUtc.ToString('O')
        $step.durationSeconds = [Math]::Round(($completedUtc - $startedUtc).TotalSeconds, 3)
        return [pscustomobject]@{
            Step = $step
            SnapshotRoot = $snapshotRoot
            WorkspaceRoot = $workspaceRoot
        }
    }
    catch {
        if ($null -ne $sourceIndexBefore -and $null -eq $sourceIndexAfter) {
            try {
                $sourceIndexAfter = Get-JetSourceIndexFingerprint -RepositoryRoot $RepositoryRoot
                $sourceIndexMutated = $sourceIndexBefore.EntryCount -ne $sourceIndexAfter.EntryCount -or
                    $sourceIndexBefore.Sha256 -cne $sourceIndexAfter.Sha256
            }
            catch {
                $sourceIndexAfter = $null
                $sourceIndexMutated = $null
            }
        }
        $completedUtc = [DateTime]::UtcNow
        $step = New-JetSyntheticStep `
            -Name 'release-candidate-snapshot' `
            -Status $failureStatus `
            -Reason $failureReason `
            -Evidence ([ordered]@{
                errorType = $_.Exception.GetType().Name
                sourceIndexMeasured = $null -ne $sourceIndexBefore -and $null -ne $sourceIndexAfter
                sourceIndexComparison = 'cached-stage-entries'
                sourceIndexEntryCountBefore = if ($null -ne $sourceIndexBefore) { $sourceIndexBefore.EntryCount } else { $null }
                sourceIndexEntryCountAfter = if ($null -ne $sourceIndexAfter) { $sourceIndexAfter.EntryCount } else { $null }
                sourceIndexSha256Before = if ($null -ne $sourceIndexBefore) { $sourceIndexBefore.Sha256 } else { $null }
                sourceIndexSha256After = if ($null -ne $sourceIndexAfter) { $sourceIndexAfter.Sha256 } else { $null }
                sourceIndexMutated = $sourceIndexMutated
                privateDataIncluded = $false
                snapshotRetained = $null -ne $workspaceRoot -and (Test-Path -LiteralPath $workspaceRoot)
            })
        $step.startedUtc = $startedUtc.ToString('O')
        $step.completedUtc = $completedUtc.ToString('O')
        $step.durationSeconds = [Math]::Round(($completedUtc - $startedUtc).TotalSeconds, 3)
        return [pscustomobject]@{
            Step = $step
            SnapshotRoot = $null
            WorkspaceRoot = $workspaceRoot
        }
    }
}

function Invoke-JetReleaseCandidateChildStep {
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $SnapshotRoot,
        [Parameter(Mandatory = $true)] [string] $RunDirectory,
        [Parameter(Mandatory = $true)] $Definition,
        [Parameter(Mandatory = $true)] $Registry,
        [Parameter(Mandatory = $true)] [int] $TimeoutSeconds
    )

    $name = "release-candidate-$([string]$Definition.name)"
    $pwsh = Get-Command pwsh -CommandType Application -ErrorAction Stop |
        Select-Object -First 1 -ExpandProperty Source
    $arguments = [Collections.Generic.List[string]]::new()
    foreach ($value in @(
            '-NoProfile',
            '-File', (Join-Path $SnapshotRoot 'tools/verify.ps1'),
            '-Command', ([string]$Definition.command),
            '-Configuration', ([string]$Definition.configuration),
            '-TimeoutSeconds', ([string]$TimeoutSeconds),
            '-EvidenceRoot', 'artifacts/harness/runs')) {
        $arguments.Add($value)
    }
    if ([bool]$Definition.noRestore) {
        $arguments.Add('-NoRestore')
    }

    [string[]]$environmentVariablesToRemove = @($Registry.testSettings.environmentVariablesToRemove)
    [string[]]$sensitiveValues = [string[]]::new(0)
    $displayCommand = @(
        'pwsh', '-NoProfile', '-File', '<candidate>/tools/verify.ps1',
        '-Command', ([string]$Definition.command),
        '-Configuration', ([string]$Definition.configuration),
        '-TimeoutSeconds', ([string]$TimeoutSeconds)
    )
    if ([bool]$Definition.noRestore) {
        $displayCommand += '-NoRestore'
    }

    $step = Invoke-JetChildStep `
        -RepositoryRoot $RepositoryRoot `
        -RunDirectory $RunDirectory `
        -Name $name `
        -FileName $pwsh `
        -Arguments $arguments.ToArray() `
        -DisplayCommand $displayCommand `
        -MaximumCapturedBytes ([int]$Registry.limits.maximumCapturedBytesPerStream) `
        -TimeoutSeconds $TimeoutSeconds `
        -WorkingDirectory $SnapshotRoot `
        -EnvironmentVariablesToRemove $environmentVariablesToRemove `
        -SensitiveValuesToRedact $sensitiveValues

    if ([bool]$step.process.timedOut -or [bool]$step.process.cancelled) {
        $step.status = 'blocked'
        $step.classification.reason = 'release_candidate_child_timeout'
        return $step
    }

    try {
        if ([bool]$step.stdout.truncated) {
            throw [InvalidDataException]::new('ReleaseCandidate child envelope was truncated.')
        }
        $stdoutPath = [IO.Path]::GetFullPath([string]$step.stdout.path, $RepositoryRoot)
        $jsonLine = @(
            [IO.File]::ReadAllLines($stdoutPath, $script:Utf8) |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        ) | Select-Object -Last 1
        if ([string]::IsNullOrWhiteSpace($jsonLine)) {
            throw [InvalidDataException]::new('ReleaseCandidate child envelope is missing.')
        }
        $envelope = $jsonLine | ConvertFrom-Json -AsHashtable -Depth 20
        if ([int]$envelope.schemaVersion -ne $script:ReceiptSchemaVersion -or
            [string]::IsNullOrWhiteSpace([string]$envelope.runId) -or
            [string]::IsNullOrWhiteSpace([string]$envelope.receipt)) {
            throw [InvalidDataException]::new('ReleaseCandidate child envelope is invalid.')
        }

        $receiptRelativePath = [string]$envelope.receipt
        if ([IO.Path]::IsPathFullyQualified($receiptRelativePath) -or
            $receiptRelativePath -match '(^|/)\.\.(/|$)' -or
            $receiptRelativePath.Contains('\', [StringComparison]::Ordinal)) {
            throw [InvalidDataException]::new('ReleaseCandidate child receipt path is invalid.')
        }
        $receiptPath = [IO.Path]::GetFullPath($receiptRelativePath, $SnapshotRoot)
        $allowedReceiptRoot = [IO.Path]::GetFullPath('artifacts/harness/runs', $SnapshotRoot)
        if (-not (Test-JetDescendantPath -Root $allowedReceiptRoot -Candidate $receiptPath) -or
            -not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
            throw [InvalidDataException]::new('ReleaseCandidate child receipt escaped its evidence root.')
        }
        Assert-JetNoExistingReparsePoint -RepositoryRoot $SnapshotRoot -Candidate $receiptPath
        if ((Get-Item -LiteralPath $receiptPath -Force).Length -gt 16777216) {
            throw [InvalidDataException]::new('ReleaseCandidate child receipt is too large.')
        }
        $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding utf8 |
            ConvertFrom-Json -AsHashtable -Depth 30
        if ([int]$receipt.schemaVersion -ne $script:ReceiptSchemaVersion -or
            [string]$receipt.runId -cne [string]$envelope.runId -or
            [string]$receipt.command -cne [string]$Definition.command -or
            [string]$receipt.configuration -cne [string]$Definition.configuration -or
            [int]$receipt.exitCode -ne [int]$envelope.exitCode -or
            [int]$receipt.exitCode -ne [int]$step.process.exitCode -or
            [string]$receipt.status -notin @('passed', 'failed', 'blocked', 'infrastructure_error') -or
            [bool]$receipt.privateData.pathInspected -or
            [bool]$receipt.privateData.pathsRecorded) {
            throw [InvalidDataException]::new('ReleaseCandidate child receipt contract is invalid.')
        }
        $lockRequired = [bool]$Registry.commands[[string]$Definition.command].requiresExclusiveLock
        $cleanupComplete = [string]$receipt.cleanup.status -ceq 'passed' -and
            (-not $lockRequired -or [string]$receipt.lock.cleanup.status -ceq 'passed')
        if (-not $cleanupComplete) {
            throw [InvalidDataException]::new('ReleaseCandidate child cleanup is incomplete.')
        }

        $copiedReceiptPath = Join-Path $RunDirectory "$name.receipt.json"
        Write-JetAtomicJson -Path $copiedReceiptPath -Value $receipt
        $reason = if ($null -ne $receipt.firstRed -and
            -not [string]::IsNullOrWhiteSpace([string]$receipt.firstRed.reason)) {
            [string]$receipt.firstRed.reason
        }
        elseif ($null -ne $receipt.error -and
            -not [string]::IsNullOrWhiteSpace([string]$receipt.error.code)) {
            [string]$receipt.error.code
        }
        else {
            $null
        }
        $step.status = [string]$receipt.status
        $step.classification.reason = $reason
        $step['evidence'] = [ordered]@{
            command = [string]$receipt.command
            runId = [string]$receipt.runId
            receipt = Get-JetRelativePath -RepositoryRoot $RepositoryRoot -Path $copiedReceiptPath
            cleanupComplete = $cleanupComplete
            privatePathInspected = $false
        }
        return $step
    }
    catch {
        $step.status = 'infrastructure_error'
        $step.classification.reason = 'release_candidate_child_receipt_invalid'
        $step['evidence'] = [ordered]@{ errorType = $_.Exception.GetType().Name }
        return $step
    }
}

function Get-JetFirstRed {
    param(
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [object[]] $Steps,
        [Parameter(Mandatory = $true)] $Cleanup,
        $LockEvidence
    )

    foreach ($step in $Steps) {
        if ($step.status -cne 'passed') {
            return [ordered]@{
                kind = 'step'
                name = $step.name
                status = $step.status
                exitCode = $step.process.exitCode
                reason = $step.classification.reason
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

function Invoke-JetHarness {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [string] $Command,
        [Parameter(Mandatory = $true)] [string] $Configuration,
        [switch] $NoRestore,
        [Parameter(Mandatory = $true)] [AllowEmptyString()] [string] $Filter,
        [Parameter(Mandatory = $true)] [string] $WaitSeconds,
        [Parameter(Mandatory = $true)] [string] $TimeoutSeconds,
        [Parameter(Mandatory = $true)] [string] $EvidenceRoot,
        [Parameter(Mandatory = $true)] [string] $ContractScenario,
        [Parameter(Mandatory = $true)] [string] $ProbeSeconds
    )

    try {
        $repositoryFull = [IO.Path]::GetFullPath($RepositoryRoot)
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
                            -SkipPolicy BlockOnSkip
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
                            -SkipPolicy NoSkips
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
                            -SkipPolicy PublicPolicy
                        $steps.Add($public)
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
                            -SkipPolicy NoSkips
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
                            -SkipPolicy NoSkips
                        $steps.Add($packageTests)
                    }
                    $publishDirectory = Join-Path $context.ScratchDirectory 'publish'
                    if ($steps[$steps.Count - 1].status -ceq 'passed') {
                        Assert-JetNoExistingReparsePoint -RepositoryRoot $repositoryFull -Candidate $publishDirectory
                        $dotnet = Get-Command dotnet -CommandType Application -ErrorAction Stop |
                            Select-Object -First 1 -ExpandProperty Source
                        $publish = Invoke-JetChildStep `
                            -RepositoryRoot $repositoryFull `
                            -RunDirectory $context.RunDirectory `
                            -Name 'dotnet-publish' `
                            -FileName $dotnet `
                            -Arguments @(
                                'publish', (Join-Path $repositoryFull ([string]$registry.packageSettings.project)),
                                '--configuration', 'Release',
                                '--runtime', ([string]$registry.packageSettings.runtimeIdentifier),
                                '--no-restore',
                                '--output', $publishDirectory,
                                "/p:PublishProfile=$([string]$registry.packageSettings.publishProfile)"
                            ) `
                            -DisplayCommand @(
                                'dotnet', 'publish', ([string]$registry.packageSettings.project),
                                '--configuration', 'Release',
                                '--runtime', ([string]$registry.packageSettings.runtimeIdentifier),
                                '--no-restore',
                                '--output', (Get-JetRelativePath -RepositoryRoot $repositoryFull -Path $publishDirectory),
                                "/p:PublishProfile=$([string]$registry.packageSettings.publishProfile)"
                            ) `
                            -MaximumCapturedBytes ([int]$registry.limits.maximumCapturedBytesPerStream) `
                            -TimeoutSeconds $parsedTimeoutSeconds `
                            -EnvironmentVariablesToRemove @($registry.testSettings.environmentVariablesToRemove) `
                            -TreatNuGetSourceFailureAsBlocked
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
                    foreach ($guiScenario in @($registry.guiSettings.scenarios)) {
                        if ($steps.Count -gt 0 -and $steps[$steps.Count - 1].status -cne 'passed') {
                            break
                        }
                        $gui = Invoke-JetGuiScenarioStep `
                            -RepositoryRoot $repositoryFull `
                            -RunDirectory $context.RunDirectory `
                            -Registry $registry `
                            -Scenario $guiScenario `
                            -TimeoutSeconds ([int]$guiScenario.timeoutSeconds + 30)
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
        $errorEvidence = [ordered]@{
            code = 'runner_exception'
            type = $_.Exception.GetType().Name
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
        contractScenario = if ($Command -ceq 'Contract') { $ContractScenario } else { $null }
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
                    candidateManifest = [string]$registry.releaseCandidateSettings.candidateManifest
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
        Write-JetEnvelope ([ordered]@{
            schemaVersion = $script:ReceiptSchemaVersion
            ok = $receipt.ok
            status = $receipt.status
            exitCode = $receipt.exitCode
            runId = $context.RunId
            receipt = Get-JetRelativePath -RepositoryRoot $repositoryFull -Path $context.ReceiptPath
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
