Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'JetStrykerDependencies.ps1')

function Set-JetStrykerBoundaryPatch {
    param([Parameter(Mandatory)] [string] $SourceRoot, [Parameter(Mandatory)] [string] $EvidencePath)
    $runnerRoot = Join-Path $SourceRoot 'src/Stryker.TestRunner.MicrosoftTestPlatform'
    $changes = @(
        @{
            path = 'DefaultTestServerConnectionFactory.cs'
            before = '.WithArguments([assembly, "--server", "--client-port", port.ToString()])'
            after = '.WithArguments(JetMtpBoundary.ServerArguments(assembly, port))'
        },
        @{
            path = 'MicrosoftTestingPlatformRunner.cs'
            before = 'var tests = await server.DiscoverTestsAsync().ConfigureAwait(false);'
            after = "var tests = await server.DiscoverTestsAsync().ConfigureAwait(false);`n            JetMtpBoundary.RegisterDiscovery(assembly, tests);"
        },
        @{
            path = 'AssemblyTestServer.cs'
            before = 'var runId = Guid.NewGuid();'
            after = "var selectedUids = JetMtpBoundary.ValidateRun(_assembly, testsToRun);`n        var runId = Guid.NewGuid();"
        },
        @{
            path = 'AssemblyTestServer.cs'
            before = "Func<TestNodeUpdate[], Task> onUpdate = updates =>`n        {"
            after = "Func<TestNodeUpdate[], Task> onUpdate = updates =>`n        {`n            JetMtpBoundary.ValidateUpdates(selectedUids, updates);"
        }
    )
    $records = [Collections.Generic.List[object]]::new()
    foreach ($change in $changes) {
        $path = Join-Path $runnerRoot $change.path
        $text = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
        $beforeHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ([regex]::Matches($text, [regex]::Escape($change.before)).Count -ne 1) {
            throw "Pinned Stryker patch anchor mismatch: $($change.path)."
        }
        [IO.File]::WriteAllText($path, $text.Replace($change.before, $change.after), [Text.UTF8Encoding]::new($false))
        $records.Add([ordered]@{ path = 'src/Stryker.TestRunner.MicrosoftTestPlatform/' + $change.path; beforeSha256 = $beforeHash; afterSha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() })
    }
    $guardSource = Join-Path $PSScriptRoot 'JetMtpBoundary.cs'
    Copy-Item -LiteralPath $guardSource -Destination (Join-Path $runnerRoot 'JetMtpBoundary.cs')
    $records.Add([ordered]@{ path = 'src/Stryker.TestRunner.MicrosoftTestPlatform/JetMtpBoundary.cs'; beforeSha256 = $null; afterSha256 = (Get-FileHash -LiteralPath $guardSource -Algorithm SHA256).Hash.ToLowerInvariant() })
    $coreRelativePath = 'src/Stryker.Core/Stryker.Core/MutationTest/CsharpMutationProcess.cs'
    $corePath = Join-Path $SourceRoot $coreRelativePath
    $coreText = [IO.File]::ReadAllText($corePath).Replace("`r`n", "`n")
    $coreAnchor = 'foreach (var file in semanticModels.Keys)'
    if ([regex]::Matches($coreText, [regex]::Escape($coreAnchor)).Count -ne 1) { throw 'Pinned Stryker early source selection anchor mismatch.' }
    $coreBeforeHash = (Get-FileHash -LiteralPath $corePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($corePath, $coreText.Replace($coreAnchor, 'foreach (var file in JetMutationSourceSelection.Select(semanticModels.Keys.ToArray(), _options))'), [Text.UTF8Encoding]::new($false))
    $records.Add([ordered]@{ path = $coreRelativePath; beforeSha256 = $coreBeforeHash; afterSha256 = (Get-FileHash -LiteralPath $corePath -Algorithm SHA256).Hash.ToLowerInvariant() })
    $selectionSource = Join-Path $PSScriptRoot 'JetMutationSourceSelection.cs'
    $selectionRelativePath = 'src/Stryker.Core/Stryker.Core/MutationTest/JetMutationSourceSelection.cs'
    Copy-Item -LiteralPath $selectionSource -Destination (Join-Path $SourceRoot $selectionRelativePath)
    $records.Add([ordered]@{ path = $selectionRelativePath; beforeSha256 = $null; afterSha256 = (Get-FileHash -LiteralPath $selectionSource -Algorithm SHA256).Hash.ToLowerInvariant() })
    foreach ($record in @(Set-JetStrykerPackageReferences -SourceRoot $SourceRoot)) { $records.Add($record) }
    $pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'stryker-pin.json') -Raw | ConvertFrom-Json
    foreach ($record in @(Set-JetStrykerDependencyLocks -SourceRoot $SourceRoot -ExpectedOverlaySha256 $pin.dependencyOverlaySha256)) { $records.Add($record) }
    [IO.File]::WriteAllText($EvidencePath, ([ordered]@{ schemaVersion = 1; patchVersion = 3; files = $records.ToArray() } | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}
