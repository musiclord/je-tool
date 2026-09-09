Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'JetStrykerPatch.ps1')
$script:MutationUtf8 = [Text.UTF8Encoding]::new($false)

function Get-JetMutationScope {
    param([ValidateSet('GlProjectionGuard', 'MoneyScaling')] [string] $Scope = 'GlProjectionGuard')
    if ($Scope -ceq 'GlProjectionGuard') {
        return [ordered]@{ name = $Scope; testClass = 'JET.Tests.Domain.GlProjectionGuardTests'; mutate = 'Domain/Rules/GlProjectionGuard.cs' }
    }
    return [ordered]@{ name = $Scope; testClass = 'JET.Tests.Domain.MoneyScalingTests'; mutate = 'Domain/Primitives/MoneyScaling.cs' }
}

function Assert-JetMutationPath {
    param([string] $Root, [string] $Path)
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $pathFull = [IO.Path]::GetFullPath($Path)
    if (!$pathFull.StartsWith($rootFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Mutation path is outside its owned root.'
    }
    $current = $pathFull
    while ($current.Length -ge $rootFull.Length) {
        if ([IO.File]::Exists($current) -or [IO.Directory]::Exists($current)) {
            if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Mutation path contains a reparse point.' }
        }
        $current = [IO.Path]::GetDirectoryName($current)
        if ([string]::IsNullOrEmpty($current)) { break }
    }
    return $pathFull
}

function Write-JetMutationJson {
    param([string] $Path, $Value)
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 16), $script:MutationUtf8)
}

function New-JetMutationSyntheticStep {
    param([string] $Name, [string] $Status, [string] $Reason, [string] $Detail = '')
    $now = [DateTime]::UtcNow.ToString('O')
    return [ordered]@{
        name = $Name; status = $Status; command = @(); startedUtc = $now; completedUtc = $now; durationSeconds = 0
        classification = [ordered]@{ reason = $Reason; detail = $Detail }
        process = [ordered]@{ processId = $null; hasExitCode = $false; exitCode = $null; timedOut = $false; cancelled = $false; killAttempted = $false; killSucceeded = $false; killError = $null }
    }
}

function Get-JetMutationPatchIdentity {
    $names = @('stryker-pin.json', 'stryker-dependency-overlay.json', 'JetMutation.psm1', 'JetStrykerPatch.ps1', 'JetStrykerDependencies.ps1', 'JetMtpBoundary.cs', 'JetMutationSourceSelection.cs', 'qualification/JetMutation.Qualification.csproj', 'qualification/Program.cs', 'qualification/Stubs.cs', 'core-qualification/JetMutation.CoreQualification.csproj', 'core-qualification/Program.cs')
    $files = @($names | ForEach-Object { [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $_) -Algorithm SHA256).Hash.ToLowerInvariant() } })
    $payload = ($files | ConvertTo-Json -Compress)
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($payload))).ToLowerInvariant()
    return [ordered]@{ sha256 = $hash; files = $files }
}

function Get-JetMutationSourceArchive {
    param(
        [Parameter(Mandatory)] [string] $ArchivePath,
        [Parameter(Mandatory)] $Pin,
        [ValidateRange(1, 120000)] [int] $TimeoutMilliseconds = 120000,
        [Threading.CancellationToken] $CancellationToken = [Threading.CancellationToken]::None
    )
    $partialPath = Join-Path ([IO.Path]::GetDirectoryName($ArchivePath)) ('download-' + [Guid]::NewGuid().ToString('N') + '.partial')
    $deadline = [Threading.CancellationTokenSource]::CreateLinkedTokenSource($CancellationToken)
    $deadline.CancelAfter($TimeoutMilliseconds)
    $client = [Net.Http.HttpClient]::new()
    # ResponseHeadersRead ends HttpClient.Timeout at the headers. The linked
    # deadline below covers the complete transfer and never resets per read.
    $client.Timeout = [Threading.Timeout]::InfiniteTimeSpan
    $response = $null
    $input = $null
    $output = $null
    $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $deadline.Token.ThrowIfCancellationRequested()
        $response = $client.GetAsync([string]$Pin.archiveUrl, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $deadline.Token).GetAwaiter().GetResult()
        $response.EnsureSuccessStatusCode() | Out-Null
        if ($null -ne $response.Content.Headers.ContentLength -and $response.Content.Headers.ContentLength -gt $Pin.archiveMaximumBytes) { throw 'Stryker archive exceeds the download limit.' }
        $input = $response.Content.ReadAsStreamAsync($deadline.Token).GetAwaiter().GetResult()
        $output = [IO.File]::Create($partialPath)
        $buffer = [byte[]]::new(65536)
        $total = 0L
        while (($read = $input.ReadAsync($buffer, 0, $buffer.Length, $deadline.Token).GetAwaiter().GetResult()) -gt 0) {
            $deadline.Token.ThrowIfCancellationRequested()
            $total += $read
            if ($total -gt $Pin.archiveMaximumBytes) { throw 'Stryker archive exceeds the download limit.' }
            $hash.AppendData($buffer, 0, $read)
            $output.WriteAsync($buffer, 0, $read, $deadline.Token).GetAwaiter().GetResult() | Out-Null
        }
        $output.FlushAsync($deadline.Token).GetAwaiter().GetResult() | Out-Null
        $output.Dispose()
        $output = $null
        if ([Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant() -cne $Pin.archiveSha256) { throw 'Stryker archive SHA256 differs from the approved pin.' }
        $deadline.Token.ThrowIfCancellationRequested()
        [IO.File]::Move($partialPath, $ArchivePath)
    } catch {
        if ($deadline.IsCancellationRequested) {
            throw [OperationCanceledException]::new('Stryker source archive transfer reached its deadline or was cancelled.', $deadline.Token)
        }
        throw
    } finally {
        try {
            if ($null -ne $output) { $output.Dispose() }
            if ($null -ne $input) { $input.Dispose() }
            if ($null -ne $response) { $response.Dispose() }
        } finally {
            try {
                # This invocation owns only this unique partial file.
                if ([IO.File]::Exists($partialPath)) { [IO.File]::Delete($partialPath) }
            } finally { $hash.Dispose(); $client.Dispose(); $deadline.Dispose() }
        }
    }
}

function Initialize-JetMutationToolSource {
    param([string] $MutationRoot, $Pin, $PatchIdentity, [Threading.CancellationToken] $CancellationToken = [Threading.CancellationToken]::None)
    $CancellationToken.ThrowIfCancellationRequested()
    $cacheRoot = Assert-JetMutationPath $MutationRoot (Join-Path $MutationRoot ('t/' + $Pin.revision.Substring(0, 8) + '-' + $PatchIdentity.sha256.Substring(0, 8)))
    [IO.Directory]::CreateDirectory($cacheRoot) | Out-Null
    $archivePath = Join-Path $cacheRoot 'source.zip'
    $null = Assert-JetMutationPath $MutationRoot $archivePath
    if (![IO.File]::Exists($archivePath)) { Get-JetMutationSourceArchive -ArchivePath $archivePath -Pin $Pin -CancellationToken $CancellationToken }
    $CancellationToken.ThrowIfCancellationRequested()
    $archiveInfo = Get-Item -LiteralPath $archivePath
    if ($archiveInfo.Length -gt $Pin.archiveMaximumBytes -or (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Pin.archiveSha256) { throw 'Cached Stryker archive differs from the approved pin.' }
    $sourceRoot = Join-Path $cacheRoot 's'
    $readyPath = Join-Path $cacheRoot 'source-ready.json'
    if (![IO.File]::Exists($readyPath)) {
        if ([IO.Directory]::Exists($sourceRoot)) { throw 'Incomplete Stryker source cache is retained; use a fresh patch identity or inspect this owned cache.' }
        [IO.Directory]::CreateDirectory($sourceRoot) | Out-Null
        $zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
        $sourceFiles = [Collections.Generic.List[string]]::new()
        try {
            $expanded = 0L
            $prefix = 'stryker-net-' + $Pin.revision + '/'
            foreach ($entry in $zip.Entries) {
                $CancellationToken.ThrowIfCancellationRequested()
                $expanded += $entry.Length
                if ($expanded -gt $Pin.expandedMaximumBytes -or $entry.Length -gt 16MB) { throw 'Stryker source archive exceeds expansion limits.' }
                if (!$entry.FullName.StartsWith($prefix, [StringComparison]::Ordinal)) { throw 'Unexpected Stryker archive root.' }
                $relative = $entry.FullName.Substring($prefix.Length)
                if ([string]::IsNullOrEmpty($relative) -or $relative.EndsWith('/')) { continue }
                if ($relative -match '(^|/)(\.\.|\.git|bin|obj|artifacts)(/|$)' -or $relative.Contains(':') -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Unsafe Stryker source archive entry.' }
                $destination = Assert-JetMutationPath $sourceRoot (Join-Path $sourceRoot $relative)
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $false)
                $sourceFiles.Add($relative)
            }
        } finally { $zip.Dispose() }
        $CancellationToken.ThrowIfCancellationRequested()
        Set-JetStrykerBoundaryPatch -SourceRoot $sourceRoot -EvidencePath (Join-Path $cacheRoot 'patch.json')
        $sourceFiles.Add('src/Stryker.TestRunner.MicrosoftTestPlatform/JetMtpBoundary.cs')
        $sourceFiles.Add('src/Stryker.Core/Stryker.Core/MutationTest/JetMutationSourceSelection.cs')
        $sourceManifest = @($sourceFiles | ForEach-Object { $CancellationToken.ThrowIfCancellationRequested(); [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $sourceRoot $_) -Algorithm SHA256).Hash.ToLowerInvariant() } })
        $qualification = Join-Path $cacheRoot 'qualification'
        [IO.Directory]::CreateDirectory($qualification) | Out-Null
        foreach ($name in @('JetMutation.Qualification.csproj', 'Program.cs', 'Stubs.cs')) {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('qualification/' + $name)) -Destination (Join-Path $qualification $name)
        }
        $coreQualification = Join-Path $cacheRoot 'core-qualification'
        [IO.Directory]::CreateDirectory($coreQualification) | Out-Null
        foreach ($name in @('JetMutation.CoreQualification.csproj', 'Program.cs')) {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('core-qualification/' + $name)) -Destination (Join-Path $coreQualification $name)
        }
        Write-JetMutationJson $readyPath ([ordered]@{ revision = $Pin.revision; archiveSha256 = $Pin.archiveSha256; patch = $PatchIdentity; sourceFiles = $sourceManifest })
    }
    $ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
    if ($ready.revision -cne $Pin.revision -or $ready.patch.sha256 -cne $PatchIdentity.sha256) { throw 'Stryker source cache identity mismatch.' }
    foreach ($entry in $ready.sourceFiles) {
        $CancellationToken.ThrowIfCancellationRequested()
        $sourcePath = Assert-JetMutationPath $sourceRoot (Join-Path $sourceRoot $entry.path)
        if ((Get-Item -LiteralPath $sourcePath).Length -gt 16MB -or (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.sha256) { throw 'Pinned Stryker source cache changed.' }
    }
    $patch = Get-Content -LiteralPath (Join-Path $cacheRoot 'patch.json') -Raw | ConvertFrom-Json
    foreach ($entry in @($patch.files | Group-Object path | ForEach-Object { $_.Group[-1] })) {
        $CancellationToken.ThrowIfCancellationRequested()
        $path = Assert-JetMutationPath $sourceRoot (Join-Path $sourceRoot $entry.path)
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.afterSha256) { throw 'Patched Stryker source cache changed.' }
    }
    return [ordered]@{ cacheRoot = $cacheRoot; sourceRoot = $sourceRoot; qualificationProject = (Join-Path $cacheRoot 'qualification/JetMutation.Qualification.csproj'); coreQualificationProject = (Join-Path $cacheRoot 'core-qualification/JetMutation.CoreQualification.csproj'); cliProject = (Join-Path $sourceRoot $Pin.cliProject) }
}

function New-JetMutationSnapshot {
    param([string] $RepositoryRoot, [string] $Destination, [Threading.CancellationToken] $CancellationToken = [Threading.CancellationToken]::None)
    $CancellationToken.ThrowIfCancellationRequested()
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    $root = [IO.Path]::GetFullPath($RepositoryRoot)
    $files = [Collections.Generic.List[object]]::new()
    $paths = [Collections.Generic.List[string]]::new()
    # 使用已確認的來源清單，不遞迴掃入未列出的本機設定、案件或測試輸出。
    $inventoryPath = Assert-JetMutationPath $root (Join-Path $root 'docs/first-root-commit-candidate.txt')
    if ((Get-Item -LiteralPath $inventoryPath).Length -gt 1MB) { throw 'Mutation source inventory exceeds its bounded budget.' }
    $entries = @([IO.File]::ReadAllLines($inventoryPath) | Where-Object { $_ -ceq 'global.json' -or $_.StartsWith('src/JET/', [StringComparison]::Ordinal) })
    if ($entries -cnotcontains 'global.json' -or ($entries | Sort-Object -Unique).Count -ne $entries.Count) { throw 'Mutation source inventory is missing global.json or contains duplicates.' }
    foreach ($relative in $entries) {
        $CancellationToken.ThrowIfCancellationRequested()
        if ($relative -match '(^|/)(bin|obj|artifacts|TestResults|StrykerOutput|\.codex|\.git|data|projects|\.\.?)(/|$)' -or $relative.Contains('\') -or $relative.Contains(':')) {
            throw 'Mutation source inventory includes a forbidden path.'
        }
        $paths.Add((Assert-JetMutationPath $root (Join-Path $root $relative)))
    }
    $total = 0L
    foreach ($path in $paths) {
        $CancellationToken.ThrowIfCancellationRequested()
        $null = Assert-JetMutationPath $root $path
        $source = Get-Item -LiteralPath $path
        $total += $source.Length
        if ($source.Length -gt 512MB -or $total -gt 256MB -or $files.Count -ge 20000) { throw 'Mutation source snapshot exceeds the bounded input budget.' }
        $relative = [IO.Path]::GetRelativePath($root, $path).Replace('\', '/')
        $destinationPath = Assert-JetMutationPath $Destination (Join-Path $Destination $relative)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destinationPath)) | Out-Null
        $before = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        Copy-Item -LiteralPath $path -Destination $destinationPath
        $after = (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($before -cne $after) { throw 'A mutation source file changed while snapshotting.' }
        $files.Add([ordered]@{ path = $relative; bytes = $source.Length; sha256 = $after })
    }
    return [ordered]@{ schemaVersion = 1; includesWorkingTreeChanges = $true; privateDataInspected = $false; fileCount = $files.Count; bytes = $total; files = $files.ToArray() }
}

function Read-JetMutationResult {
    param([string] $RunRoot, $Scope)
    $boundaryPath = Join-Path $RunRoot 'selection.jsonl'
    $selectionInfo = Get-Item -LiteralPath $boundaryPath
    if ($selectionInfo.Length -gt 8MB) { throw 'Mutation selection evidence exceeds its limit.' }
    $events = @(Get-Content -LiteralPath $boundaryPath | ForEach-Object { $_ | ConvertFrom-Json })
    $discovery = @($events | Where-Object kind -CEQ 'discovery')
    $runs = @($events | Where-Object kind -CEQ 'run')
    $starts = @($events | Where-Object kind -CEQ 'server_start')
    if (@($events | Where-Object kind -CEQ 'rejected').Count -gt 0 -or $discovery.Count -eq 0 -or $runs.Count -eq 0 -or $starts.Count -eq 0) { throw 'Mutation selection evidence is missing or rejected.' }
    $known = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($uid in $discovery[0].uidHashes) { $null = $known.Add([string]$uid) }
    foreach ($event in $events) {
        if ($event.detail -cne $Scope.testClass) { throw 'Mutation selection class differs from the authorized scope.' }
        if ($event.kind -in @('discovery', 'run', 'updates')) {
            if ($event.kind -cne 'updates' -and @($event.uidHashes).Count -eq 0) { throw 'Mutation used an empty selection.' }
            foreach ($uid in $event.uidHashes) { if (!$known.Contains([string]$uid)) { throw 'Mutation selection contains an unknown UID.' } }
        }
    }
    $reportFiles = @(Get-ChildItem -LiteralPath (Join-Path $RunRoot 'report') -Recurse -File)
    $jsonReports = @($reportFiles | Where-Object Name -CEQ 'mutation-report.json')
    $htmlReports = @($reportFiles | Where-Object Name -CEQ 'mutation-report.html')
    if ($jsonReports.Count -ne 1 -or $htmlReports.Count -ne 1 -or $jsonReports[0].Length -gt 32MB -or $htmlReports[0].Length -eq 0) { throw 'Mutation JSON and HTML reports are incomplete.' }
    $report = Get-Content -LiteralPath $jsonReports[0].FullName -Raw | ConvertFrom-Json
    $mutants = [Collections.Generic.List[object]]::new()
    foreach ($property in $report.files.PSObject.Properties) {
        foreach ($mutant in @($property.Value.mutants)) {
            $reportedSource = $property.Name.Replace('\', '/')
            if ($reportedSource -cne $Scope.mutate -and !$reportedSource.EndsWith('/' + [string]$Scope.mutate, [StringComparison]::Ordinal)) { throw 'A mutant is outside the authorized source file.' }
            $mutants.Add([ordered]@{ id = $mutant.id; status = $mutant.status; mutatorName = $mutant.mutatorName; replacement = $mutant.replacement; location = $mutant.location })
        }
    }
    if ($mutants.Count -eq 0) { throw 'No mutations were measured.' }
    $counts = [ordered]@{}
    # Group-Object -Property does not expose OrderedDictionary keys as object
    # properties. Count by the actual status key so no result lands in "".
    foreach ($mutant in $mutants) {
        $status = [string]$mutant['status']
        if ($counts.Contains($status)) { $counts[$status] = [int]$counts[$status] + 1 }
        else { $counts[$status] = 1 }
    }
    return [ordered]@{
        status = 'passed'; scope = $Scope; selectedTests = $known.Count; serverStarts = $starts.Count; checkedRuns = $runs.Count
        selectionEvidence = 'selection.jsonl'; reports = @([IO.Path]::GetRelativePath($RunRoot, $jsonReports[0].FullName).Replace('\', '/'), [IO.Path]::GetRelativePath($RunRoot, $htmlReports[0].FullName).Replace('\', '/'))
        mutationCount = $mutants.Count; counts = $counts; mutants = $mutants.ToArray(); survivorReviewRequired = @($mutants.ToArray() | Where-Object { $_.status -cin @('Survived', 'NoCoverage') }).Count -gt 0
        interpretation = 'Counts apply only to this source file and selected public test class; Survived and NoCoverage mutants require manual review.'
        privateData = [ordered]@{ pathInspected = $false }
    }
}

function Invoke-JetMutationPipeline {
    param(
        [Parameter(Mandatory)] [string] $RepositoryRoot,
        [Parameter(Mandatory)] [string] $RunDirectory,
        [Parameter(Mandatory)] $Registry,
        [Parameter(Mandatory)] [scriptblock] $RunChildStep,
        [ValidateSet('GlProjectionGuard', 'MoneyScaling')] [string] $Scope = 'GlProjectionGuard',
        [ValidateRange(1, 1800)] [int] $TimeoutSeconds = 1800
    )
    $steps = [Collections.Generic.List[object]]::new()
    $selection = Get-JetMutationScope $Scope
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $pipelineCancellation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($TimeoutSeconds))
    $mutationRoot = Assert-JetMutationPath $RepositoryRoot (Join-Path $RepositoryRoot 'artifacts/harness/mutation')
    $runKey = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($RunDirectory))).ToLowerInvariant().Substring(0, 12)
    $runRoot = Join-Path $mutationRoot ('r/' + $runKey)
    $evidence = [ordered]@{ status = 'not_run'; scope = $selection; root = [IO.Path]::GetRelativePath($RepositoryRoot, $runRoot).Replace('\', '/'); privateData = [ordered]@{ pathInspected = $false } }
    $invoke = {
        param([string] $Name, [string[]] $Arguments, [string] $WorkingDirectory, [hashtable] $ExtraEnvironment = @{})
        $remaining = $TimeoutSeconds - [int][Math]::Ceiling($clock.Elapsed.TotalSeconds)
        if ($remaining -le 0 -or $pipelineCancellation.IsCancellationRequested) { return (New-JetMutationSyntheticStep $Name 'blocked' 'mutation_timeout') }
        $environment = @{
            MSBUILDDISABLENODEREUSE = '1'; DOTNET_CLI_TELEMETRY_OPTOUT = '1'; DOTNET_NOLOGO = '1'
            NUGET_PACKAGES = (Join-Path $mutationRoot 'c/packages'); NUGET_HTTP_CACHE_PATH = (Join-Path $mutationRoot 'c/http')
            NUGET_PLUGINS_CACHE_PATH = (Join-Path $mutationRoot 'c/plugins'); DOTNET_CLI_HOME = (Join-Path $mutationRoot 'c/dotnet')
            TEMP = (Join-Path $runRoot 'tmp'); TMP = (Join-Path $runRoot 'tmp')
        }
        foreach ($key in $ExtraEnvironment.Keys) { $environment[$key] = $ExtraEnvironment[$key] }
        $remove = @($Registry.testSettings.environmentVariablesToRemove) + @('STRYKER_DASHBOARD_API_KEY', 'STRYKER_API_KEY', 'JET_MUTATION_TEST_CLASS', 'JET_MUTATION_EVIDENCE_PATH')
        return (& $RunChildStep @{
            RepositoryRoot = $RepositoryRoot; RunDirectory = $RunDirectory; Name = $Name; FileName = 'dotnet'; Arguments = $Arguments
            DisplayCommand = @('dotnet') + @($Arguments | ForEach-Object { $_.Replace($RepositoryRoot, '[repository]') }); WorkingDirectory = $WorkingDirectory
            MaximumCapturedBytes = [int]$Registry.limits.maximumCapturedBytesPerStream; TimeoutSeconds = $remaining
            EnvironmentVariablesToRemove = $remove; EnvironmentVariablesToSet = $environment; TreatNuGetSourceFailureAsBlocked = $true
        })
    }
    try {
        if ([IO.Directory]::Exists($runRoot)) { throw 'Mutation evidence directory already exists.' }
        [IO.Directory]::CreateDirectory($runRoot) | Out-Null
        [IO.Directory]::CreateDirectory((Join-Path $runRoot 'tmp')) | Out-Null
        Write-JetMutationJson (Join-Path $runRoot 'owner.json') ([ordered]@{ runDirectory = [IO.Path]::GetRelativePath($RepositoryRoot, $RunDirectory).Replace('\', '/'); createdUtc = [DateTime]::UtcNow.ToString('O') })
        $pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'stryker-pin.json') -Raw | ConvertFrom-Json
        $patchIdentity = Get-JetMutationPatchIdentity
        $tool = Initialize-JetMutationToolSource -MutationRoot $mutationRoot -Pin $pin -PatchIdentity $patchIdentity -CancellationToken $pipelineCancellation.Token
        Write-JetMutationJson (Join-Path $runRoot 'tool-identity.json') ([ordered]@{ pin = $pin; patch = $patchIdentity })
        $steps.Add((New-JetMutationSyntheticStep 'mutation-source' 'passed' 'pinned_source_verified'))
        $qualificationBuild = & $invoke 'mutation-qualification-build' @('build', $tool.qualificationProject, '-c', 'Release', '--disable-build-servers', '-m:1', ('-p:StrykerSourceRoot=' + $tool.sourceRoot)) $tool.cacheRoot
        $steps.Add($qualificationBuild)
        if ($qualificationBuild.status -cne 'passed') { $evidence.status = $qualificationBuild.status; return [ordered]@{ steps = $steps.ToArray(); evidence = $evidence } }
        $qualificationDll = Join-Path $tool.cacheRoot 'qualification/bin/Release/net10.0/JetMutation.Qualification.dll'
        $qualification = & $invoke 'mutation-qualification' @($qualificationDll, (Join-Path $runRoot 'qualification-selection.jsonl'), $tool.sourceRoot) $tool.cacheRoot
        $steps.Add($qualification)
        if ($qualification.status -cne 'passed') { $evidence.status = $qualification.status; return [ordered]@{ steps = $steps.ToArray(); evidence = $evidence } }
        $toolBuild = & $invoke 'mutation-tool-build' @('build', $tool.cliProject, '-c', 'Release', '--disable-build-servers', '-m:1', '-p:RestoreLockedMode=true', '-p:NuGetAudit=true') $tool.sourceRoot
        $steps.Add($toolBuild)
        if ($toolBuild.status -cne 'passed') { $evidence.status = $toolBuild.status; return [ordered]@{ steps = $steps.ToArray(); evidence = $evidence } }
        $toolDll = Join-Path $tool.sourceRoot 'src/Stryker.CLI/Stryker.CLI/bin/Release/Stryker.CLI.dll'
        if (![IO.File]::Exists($toolDll)) { throw 'Pinned Stryker CLI output is missing.' }
        $coreQualificationBuild = & $invoke 'mutation-source-qualification-build' @('build', $tool.coreQualificationProject, '-c', 'Release', '--disable-build-servers', '-m:1', ('-p:StrykerCliDirectory=' + [IO.Path]::GetDirectoryName($toolDll))) $tool.cacheRoot
        $steps.Add($coreQualificationBuild)
        if ($coreQualificationBuild.status -cne 'passed') { $evidence.status = $coreQualificationBuild.status; return [ordered]@{ steps = $steps.ToArray(); evidence = $evidence } }
        $coreQualificationDll = Join-Path $tool.cacheRoot 'core-qualification/bin/Release/net10.0/JetMutation.CoreQualification.dll'
        $coreQualification = & $invoke 'mutation-source-qualification' @($coreQualificationDll, $tool.sourceRoot) $tool.cacheRoot
        $steps.Add($coreQualification)
        if ($coreQualification.status -cne 'passed') { $evidence.status = $coreQualification.status; return [ordered]@{ steps = $steps.ToArray(); evidence = $evidence } }
        $capability = & $invoke 'mutation-capability' @($toolDll, '--help', '--skip-version-check') $tool.sourceRoot
        $steps.Add($capability)
        if ($capability.status -cne 'passed') { $evidence.status = $capability.status; return [ordered]@{ steps = $steps.ToArray(); evidence = $evidence } }
        $help = Get-Content -LiteralPath (Join-Path $RunDirectory 'mutation-capability.stdout.log') -Raw
        if ($help -notmatch 'test-runner' -or $help -notmatch 'mtp' -or $help -notmatch 'concurrency') { throw 'Pinned Stryker CLI does not advertise required MTP capabilities.' }
        $snapshotRoot = Join-Path $runRoot 's'
        $snapshot = New-JetMutationSnapshot $RepositoryRoot $snapshotRoot -CancellationToken $pipelineCancellation.Token
        Write-JetMutationJson (Join-Path $runRoot 'snapshot.json') $snapshot
        $steps.Add((New-JetMutationSyntheticStep 'mutation-snapshot' 'passed' 'working_tree_snapshot_verified'))
        $reportRoot = Join-Path $runRoot 'report'
        [IO.Directory]::CreateDirectory($reportRoot) | Out-Null
        $configPath = Join-Path $runRoot 'stryker-config.json'
        Write-JetMutationJson $configPath @{ 'stryker-config' = @{} }
        $mutation = & $invoke 'mutation-run' @($toolDll, '--config-file', $configPath, '--test-runner', 'mtp', '--project', 'JET.csproj', '--test-project', 'JET.Tests.csproj', '--mutate', $selection.mutate, '--reporter', 'json', '--reporter', 'html', '--concurrency', '1', '--output', $reportRoot, '--break-at', '0', '--skip-version-check', '--configuration', 'Release') (Join-Path $snapshotRoot 'src/JET/tests/JET.Tests') @{ JET_MUTATION_TEST_CLASS = $selection.testClass; JET_MUTATION_EVIDENCE_PATH = (Join-Path $runRoot 'selection.jsonl') }
        $steps.Add($mutation)
        if ($mutation.status -cne 'passed') { $evidence.status = $mutation.status; return [ordered]@{ steps = $steps.ToArray(); evidence = $evidence } }
        $result = Read-JetMutationResult $runRoot $selection
        $result['toolRevision'] = $pin.revision
        $result['patchSha256'] = $patchIdentity.sha256
        $result['cliSha256'] = (Get-FileHash -LiteralPath $toolDll -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-JetMutationJson (Join-Path $runRoot 'result.json') $result
        $evidence.status = 'passed'
        $evidence['result'] = $evidence.root + '/result.json'
        $evidence['mutationCount'] = $result.mutationCount
        $evidence['counts'] = $result.counts
        $evidence['selectedTests'] = $result.selectedTests
        $evidence['survivorReviewRequired'] = $result.survivorReviewRequired
        $steps.Add((New-JetMutationSyntheticStep 'mutation-evidence' 'passed' 'selection_and_reports_verified'))
    } catch {
        $detail = $_.Exception.Message.Replace($RepositoryRoot, '[repository]').Replace([Environment]::GetFolderPath('UserProfile'), '[user-profile]')
        $reason = if ($pipelineCancellation.IsCancellationRequested -or $_.Exception.GetBaseException() -is [OperationCanceledException]) { 'mutation_timeout' } else { 'mutation_capability_or_evidence_unavailable' }
        $steps.Add((New-JetMutationSyntheticStep 'mutation-infrastructure' 'blocked' $reason $detail))
        $evidence.status = 'blocked'
    } finally {
        try {
            if ([IO.Directory]::Exists($runRoot)) {
                Write-JetMutationJson (Join-Path $runRoot 'pipeline.json') ([ordered]@{ scope = $selection; steps = $steps.ToArray(); evidence = $evidence; temporarySourceRetained = $true; privateData = [ordered]@{ pathInspected = $false } })
            }
        } finally { $pipelineCancellation.Dispose() }
    }
    return [ordered]@{ steps = $steps.ToArray(); evidence = $evidence }
}

Export-ModuleMember -Function Invoke-JetMutationPipeline, Get-JetMutationScope
