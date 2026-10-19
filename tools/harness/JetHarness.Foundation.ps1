using namespace System.IO

# 由 JetHarness.psm1 點號載入。各命令共用的基礎：輸出結果、路徑檢查、原子寫入 JSON、讀取 lanes.json、證據目錄、執行身分與執行環境。

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
    if ([string]$registry.runnerContractVersion -cne '8.2') {
        throw [InvalidDataException]::new('Unsupported runner contract version.')
    }
    # 2026-10-13：建置、.NET 測試與前端測試的自有程序樹記憶體上限，至少 1024 MB。
    if (-not $registry.limits.ContainsKey('maximumProcessTreeMemoryMegabytes') -or
        [long]$registry.limits.maximumProcessTreeMemoryMegabytes -lt 1024) {
        throw [InvalidDataException]::new('Lane registry limits must set maximumProcessTreeMemoryMegabytes to at least 1024.')
    }

    $enabledLaneNames = @(
        $registry.lanes.Keys |
            Where-Object { [bool]$registry.lanes[$_].enabled } |
            Sort-Object
    )
    if (($enabledLaneNames -join ',') -cne 'Excel,Focused,Foundation,Gui,Mutation,Package,PrivateCase,Provider,Public,ReleaseCandidate') {
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
    if (-not $registry.commands.ContainsKey('Mutation') -or
        -not [bool]$registry.commands.Mutation.enabled -or
        [string]$registry.commands.Mutation.lane -cne 'Mutation' -or
        -not [bool]$registry.commands.Mutation.requiresExclusiveLock -or
        [bool]$registry.commands.Mutation.privateDataAccess) {
        throw [InvalidDataException]::new('The Mutation command boundary is invalid.')
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
    foreach ($required in @('snapshotWorkspaceRoot', 'privateCaseIncluded', 'liveProviderIncluded', 'steps')) {
        if (-not $releaseCandidateSettings.ContainsKey($required)) {
            throw [InvalidDataException]::new("ReleaseCandidate settings are missing '$required'.")
        }
    }
    if ([string]$releaseCandidateSettings.snapshotWorkspaceRoot -cne 'artifacts/harness/rc' -or
        [bool]$releaseCandidateSettings.privateCaseIncluded -or
        [bool]$releaseCandidateSettings.liveProviderIncluded) {
        throw [InvalidDataException]::new(
            'ReleaseCandidate must use the short owned workspace without PrivateCase or live Provider.')
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

    # 情境的逾時、操作上限、預期次數與截圖數只寫在 lanes.json，這裡只檢查格式。
    # 數值上限由 GUI 驅動程式與 AgentGuiTest 組態在執行時各自檢查。
    $guiScenarios = @($guiSettings.scenarios)
    if ($guiScenarios.Count -eq 0) {
        throw [InvalidDataException]::new('GUI settings must list at least one scenario.')
    }
    $guiScenarioNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($scenario in $guiScenarios) {
        if ($scenario -isnot [Collections.IDictionary]) {
            throw [InvalidDataException]::new('GUI scenarios must be objects.')
        }
        foreach ($required in @('name', 'timeoutSeconds', 'actionBudget', 'expectedActionCount', 'screenshotBudget')) {
            if (-not $scenario.ContainsKey($required)) {
                throw [InvalidDataException]::new("GUI scenario is missing '$required'.")
            }
        }
        $scenarioName = [string]$scenario.name
        if ($scenarioName -cnotmatch '^[a-z][a-z0-9]*(-[a-z0-9]+)*$' -or -not $guiScenarioNames.Add($scenarioName)) {
            throw [InvalidDataException]::new('GUI scenario names must be unique lowercase hyphenated words.')
        }
        foreach ($numberField in @('timeoutSeconds', 'actionBudget', 'expectedActionCount', 'screenshotBudget')) {
            if ($scenario[$numberField] -isnot [long] -and $scenario[$numberField] -isnot [int]) {
                throw [InvalidDataException]::new("GUI scenario '$scenarioName' field '$numberField' must be an integer.")
            }
        }
        if ([long]$scenario.timeoutSeconds -lt 1 -or
            [long]$scenario.actionBudget -lt 1 -or
            [long]$scenario.expectedActionCount -lt 1 -or
            [long]$scenario.expectedActionCount -gt [long]$scenario.actionBudget -or
            [long]$scenario.screenshotBudget -lt 0) {
            throw [InvalidDataException]::new(
                "GUI scenario '$scenarioName' needs positive limits, and its expected action count cannot exceed its action budget.")
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
        [string]$excelSettings.fixtureMethodPattern -cne '*SixReportWorkflowJourneyTests.AccountMappingHandoff_PreservesValidationRun_AndPublishesFiveReportsAndTemplate*' -or
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
        'tools/harness/JetHarness.Foundation.ps1',
        'tools/harness/JetHarness.Artifacts.ps1',
        'tools/harness/JetHarness.Results.ps1',
        'tools/harness/JetHarness.Steps.ps1',
        'tools/harness/JetHarness.Preflight.ps1',
        'tools/harness/JetHarness.Lanes.ps1',
        'tools/harness/JetHarness.ReleaseCandidate.ps1',
        'tools/harness/JetContext.ps1',
        'tools/harness/JetHarness.Process.cs',
        'tools/harness/JetDocumentationCheck.ps1',
        'tools/harness/JetPackageVerifier.ps1',
        'tools/harness/gui-driver/GuiDriver.csproj',
        'tools/harness/gui-driver/DriverContracts.cs',
        'tools/harness/gui-driver/CdpSession.cs',
        'tools/harness/gui-driver/OwnedGuiRun.cs',
        'tools/harness/gui-driver/GuiRunner.cs',
        'tools/harness/gui-driver/GuiScenarios.cs',
        'tools/harness/gui-driver/GuiFilterWorkflowScenarios.cs',
        'tools/harness/gui-driver/GuiFeedbackWorkflowScenario.cs',
        'tools/harness/gui-driver/GuiDataRecoveryScenarios.cs',
        'tools/harness/gui-driver/GuiSideMonthWorkflow.cs',
        'tools/harness/gui-driver/GuiUpstreamChangeScenario.cs',
        'tools/harness/gui-driver/GuiCaseToWorkpaperScenario.cs',
        'tools/harness/gui-driver/GuiWorkpaperInspection.cs',
        'tools/harness/gui-driver/GuiNestedVoucherWorkflow.cs',
        'tools/harness/gui-driver/GuiLegacyFormWorkflow.cs',
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
