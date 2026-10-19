using namespace System.IO

# 由 JetHarness.psm1 點號載入。執行子步驟：啟動子程序並限制記憶體、建置產品以及 Gui 與 Excel 的驅動程式、組測試參數、執行測試，以及產生不必啟動程序的步驟紀錄。

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
        [switch] $OwnProcessTree,
        # 2026-10-13：自有程序樹合計的記憶體上限（MB），0 表示不設；超過就整批結束，步驟判定失敗。
        [long] $MaximumProcessTreeMemoryMegabytes = 0,
        [switch] $TreatNuGetSourceFailureAsBlocked
    )

    if ($MaximumProcessTreeMemoryMegabytes -lt 0 -or ($MaximumProcessTreeMemoryMegabytes -gt 0 -and -not $OwnProcessTree)) {
        throw [ArgumentException]::new('A process-tree memory limit requires an owned process tree.')
    }
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
        $StandardStreamCodePage,
        [bool]$OwnProcessTree,
        [Threading.CancellationToken]::None,
        [long]($MaximumProcessTreeMemoryMegabytes * 1MB))
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
    if ($OwnProcessTree -and (-not $result.OwnedProcessTree -or $result.CleanupSucceeded -ne $true -or $result.BelowNormalApplied -ne $true)) {
        $status = 'failed'
        $classificationReason = 'owned_process_tree_incomplete'
    }
    elseif ($result.MemoryLimitReached) {
        $status = 'failed'
        $classificationReason = 'memory_limit_reached'
    }
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
            detail = $null
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
            ownedProcessTree = $result.OwnedProcessTree
            cleanupSucceeded = $result.CleanupSucceeded
            belowNormalApplied = $result.BelowNormalApplied
            memoryLimitMegabytes = if ($null -ne $result.ProcessTreeMemoryLimitBytes) { [long]($result.ProcessTreeMemoryLimitBytes / 1MB) } else { $null }
            peakMemoryMegabytes = if ($null -ne $result.PeakProcessTreeMemoryBytes) { [Math]::Round($result.PeakProcessTreeMemoryBytes / 1MB, 1) } else { $null }
            memoryLimitReached = $result.MemoryLimitReached
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

# 2026-10-13：建置、.NET 測試與前端測試放在自有程序樹裡執行，整個程序樹合計的已認可記憶體超過
# lanes.json 的 limits.maximumProcessTreeMemoryMegabytes 就整批結束，步驟判定失敗並記下 memory_limit_reached。
# 建置另加 --disable-build-servers：編譯伺服器與重複使用的 MSBuild 節點不會留在背景，也不會被別的建置共用到一半就隨程序樹結束。
function Get-JetMemoryBoundedTreeSettings {
    param([Parameter(Mandatory = $true)] $Registry)
    return @{
        OwnProcessTree = $true
        MaximumProcessTreeMemoryMegabytes = [long]$Registry.limits.maximumProcessTreeMemoryMegabytes
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
    $boundedTree = Get-JetMemoryBoundedTreeSettings -Registry $Registry
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
                '--no-restore', '--disable-build-servers'
            ) `
            -DisplayCommand @(
                'dotnet', 'build', 'src/JET/JET.slnx',
                '--configuration', $Configuration, '--no-restore', '--disable-build-servers'
            ) `
            -MaximumCapturedBytes ([int]$Registry.limits.maximumCapturedBytesPerStream) `
            -TimeoutSeconds $TimeoutSeconds `
            -EnvironmentVariablesToRemove @($Registry.testSettings.environmentVariablesToRemove) `
            @boundedTree
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
    $boundedTree = Get-JetMemoryBoundedTreeSettings -Registry $Registry
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
                '--no-restore', '--disable-build-servers'
            ) `
            -DisplayCommand @(
                'dotnet', 'build', ([string]$Registry.guiSettings.applicationProject),
                '--configuration', ([string]$Registry.guiSettings.configuration), '--no-restore', '--disable-build-servers'
            ) `
            -MaximumCapturedBytes ([int]$Registry.limits.maximumCapturedBytesPerStream) `
            -TimeoutSeconds $TimeoutSeconds `
            -EnvironmentVariablesToRemove @($Registry.testSettings.environmentVariablesToRemove) `
            @boundedTree
        $results.Add($applicationBuild)
    }

    if ($results[$results.Count - 1].status -ceq 'passed') {
        $driverBuild = Invoke-JetChildStep `
            -RepositoryRoot $RepositoryRoot `
            -RunDirectory $RunDirectory `
            -Name 'gui-driver-build' `
            -FileName $dotnet `
            -Arguments @('build', $driverProject, '--configuration', 'Release', '--no-restore', '--disable-build-servers') `
            -DisplayCommand @(
                'dotnet', 'build', ([string]$Registry.guiSettings.driverProject),
                '--configuration', 'Release', '--no-restore', '--disable-build-servers'
            ) `
            -MaximumCapturedBytes ([int]$Registry.limits.maximumCapturedBytesPerStream) `
            -TimeoutSeconds $TimeoutSeconds `
            -EnvironmentVariablesToRemove @($Registry.testSettings.environmentVariablesToRemove) `
            @boundedTree
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
    $boundedTree = Get-JetMemoryBoundedTreeSettings -Registry $Registry
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
                '--no-restore', '--disable-build-servers'
            ) `
            -DisplayCommand @(
                'dotnet', 'build', 'src/JET/JET.slnx',
                '--configuration', ([string]$Registry.excelSettings.configuration), '--no-restore', '--disable-build-servers'
            ) `
            -MaximumCapturedBytes ([int]$Registry.limits.maximumCapturedBytesPerStream) `
            -TimeoutSeconds $TimeoutSeconds `
            -EnvironmentVariablesToRemove @($Registry.testSettings.environmentVariablesToRemove) `
            @boundedTree
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
                '--configuration', ([string]$Registry.excelSettings.configuration), '--no-restore', '--disable-build-servers'
            ) `
            -DisplayCommand @(
                'dotnet', 'build', ([string]$Registry.excelSettings.driverProject),
                '--configuration', ([string]$Registry.excelSettings.configuration), '--no-restore', '--disable-build-servers'
            ) `
            -MaximumCapturedBytes ([int]$Registry.limits.maximumCapturedBytesPerStream) `
            -TimeoutSeconds $TimeoutSeconds `
            -EnvironmentVariablesToRemove @($Registry.testSettings.environmentVariablesToRemove) `
            @boundedTree
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
        [AllowNull()] [string[]] $ProtectedMethodPatterns = $null,
        # Collections：不同測試類別平行執行。None：循序，給共用 SQL Server 資料庫或私人案件副本的路線。
        [ValidateSet('None', 'Collections')] [string] $Parallelism = 'None'
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
            '--parallel', $Parallelism.ToLowerInvariant(),
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
        [string] $SkipPolicy = 'NoSkips',
        [switch] $WithholdFailureMessages,
        [ValidateSet('None', 'Collections')] [string] $Parallelism = 'None'
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
        -ProtectedMethodPatterns $ProtectedMethodPatterns `
        -Parallelism $Parallelism
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
    $boundedTree = Get-JetMemoryBoundedTreeSettings -Registry $Registry
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
        -StandardStreamCodePage ([Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage) `
        @boundedTree

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
            -SummaryPath $summaryPath `
            -WithholdFailureMessages:$WithholdFailureMessages
        $step['test'] = $parsed.Receipt

        if ($parsed.Summary.counters.total -lt $MinimumExpectedTests) {
            $step.status = 'failed'
            $step.classification.reason = 'test_count_below_minimum'
        }
        elseif ($parsed.Summary.counters.failed -gt 0) {
            # 測試程式因失敗回非零結束碼時，status 早已是 failed 而 reason 仍空；這裡一律補上原因與
            # 第一個失敗的細節，讓 firstRed 不再只有 child_failed。
            if ($step.status -ceq 'passed') {
                $step.status = 'failed'
            }
            if ([string]::IsNullOrWhiteSpace([string]$step.classification.reason)) {
                $step.classification.reason = 'trx_contains_failed_tests'
            }
            if ($null -ne $parsed.FirstFailure) {
                $failedCount = [int]$parsed.Summary.counters.failed
                $detail = "$failedCount failed. $($parsed.FirstFailure.fqn)"
                if ($parsed.FirstFailure.messageWithheld) {
                    $detail += ' (message withheld: private case)'
                }
                elseif (-not [string]::IsNullOrWhiteSpace([string]$parsed.FirstFailure.message)) {
                    $detail += ": $($parsed.FirstFailure.message)"
                }
                $step.classification['detail'] = $detail
            }
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
