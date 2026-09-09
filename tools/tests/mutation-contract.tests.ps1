param([string] $RepositoryRoot = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
Import-Module (Join-Path $RepositoryRoot 'tools/harness/mutation/JetMutation.psm1') -Force
$mutationModule = Get-Module JetMutation
$mutationTestRoot = Join-Path $RepositoryRoot ('artifacts/harness/mutation/contract/' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($mutationTestRoot) | Out-Null
$assertions = 0
function Assert-Mutation($Condition, [string] $Message) {
    $script:assertions++
    if (!$Condition) { throw "Mutation contract failed: $Message" }
}
function Assert-MutationThrows([scriptblock] $Action, [string] $Message) {
    $threw = $false
    try { & $Action | Out-Null } catch { $threw = $true }
    Assert-Mutation $threw $Message
}
Assert-Mutation ((Get-JetMutationScope).testClass -ceq 'JET.Tests.Domain.GlProjectionGuardTests') 'default scope'
Assert-Mutation ((Get-JetMutationScope MoneyScaling).mutate -ceq 'Domain/Primitives/MoneyScaling.cs') 'second fixed source file'
Assert-MutationThrows { Get-JetMutationScope '*' } 'wildcard scope rejected'
Assert-MutationThrows { & $mutationModule { param($root) Assert-JetMutationPath $root (Join-Path $root '../outside') } $mutationTestRoot } 'path escape rejected'

# A single loopback connection per fixture; it never serves external content.
# The server runs on .NET tasks so a blocked PowerShell download cannot prevent
# the fixture from sending headers or observing its own shutdown token.
if ($null -eq ('Jet.Mutation.Contract.HttpFixture' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace Jet.Mutation.Contract {
    public sealed class HttpFixture : IDisposable {
        private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        private readonly Task server;
        private TcpClient client;
        private int headersSent;
        private int bytesSent;
        public string Url { get; private set; }
        public bool HeadersSent => Volatile.Read(ref headersSent) != 0;
        public int BytesSent => Volatile.Read(ref bytesSent);
        public bool Stopped => server.IsCompleted;
        public HttpFixture(string mode, byte[] body) {
            listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/source.zip";
            server = Task.Run(async () => {
                try {
                    using (client = await listener.AcceptTcpClientAsync(shutdown.Token))
                    using (var stream = client.GetStream()) {
                        var request = new StringBuilder();
                        var buffer = new byte[1024];
                        while (!request.ToString().Contains("\r\n\r\n")) {
                            var read = await stream.ReadAsync(buffer, 0, buffer.Length, shutdown.Token);
                            if (read == 0) throw new IOException("Client closed before request headers.");
                            request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                            if (request.Length > 8192) throw new IOException("Oversized fixture request.");
                        }
                        var length = mode == "streamed-oversize" ? "" : "Content-Length: " + body.Length + "\r\n";
                        var headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n" + length + "Connection: close\r\n\r\n");
                        await stream.WriteAsync(headers, 0, headers.Length, shutdown.Token);
                        await stream.FlushAsync(shutdown.Token);
                        Interlocked.Exchange(ref headersSent, 1);
                        if (mode == "stalled-body") {
                            await stream.WriteAsync(body, 0, 1, shutdown.Token);
                            Interlocked.Increment(ref bytesSent);
                            await stream.FlushAsync(shutdown.Token);
                            await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
                        } else if (mode == "drip-body") {
                            foreach (var value in body) {
                                await stream.WriteAsync(new[] { value }, 0, 1, shutdown.Token);
                                Interlocked.Increment(ref bytesSent);
                                await stream.FlushAsync(shutdown.Token);
                                await Task.Delay(50, shutdown.Token);
                            }
                        } else {
                            await stream.WriteAsync(body, 0, body.Length, shutdown.Token);
                            Interlocked.Add(ref bytesSent, body.Length);
                        }
                    }
                } catch (OperationCanceledException) { }
                  catch (IOException) { }
                  catch (SocketException) { }
                  catch (ObjectDisposedException) { }
            });
        }
        public void Dispose() {
            shutdown.Cancel();
            listener.Stop();
            client?.Dispose();
            try {
                if (!server.Wait(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("Loopback fixture did not stop.");
            } finally { shutdown.Dispose(); }
        }
    }
}
'@
}
$downloadBody = [Text.Encoding]::ASCII.GetBytes('synthetic archive body')
$downloadHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($downloadBody)).ToLowerInvariant()
foreach ($downloadCase in @('complete', 'stalled-body', 'drip-body', 'announced-oversize', 'streamed-oversize', 'wrong-hash')) {
    $downloadRoot = Join-Path $mutationTestRoot ('download-' + $downloadCase)
    [IO.Directory]::CreateDirectory($downloadRoot) | Out-Null
    $sentinel = Join-Path $downloadRoot 'unrelated.partial'
    [IO.File]::WriteAllText($sentinel, 'owned by another invocation')
    $caseBody = if ($downloadCase -ceq 'drip-body') { [byte[]]::new(128) } else { $downloadBody }
    $serverMode = if ($downloadCase -ceq 'wrong-hash') { 'complete' } else { $downloadCase }
    $fixture = [Jet.Mutation.Contract.HttpFixture]::new($serverMode, $caseBody)
    try {
        $downloadPin = @{ archiveUrl = $fixture.Url; archiveMaximumBytes = $(if ($downloadCase -like '*oversize') { 4 } else { 256 }); archiveSha256 = $(if ($downloadCase -ceq 'wrong-hash') { '0' * 64 } else { $downloadHash }) }
        $downloadPath = Join-Path $downloadRoot 'source.zip'
        $failure = $null
        $elapsed = [Diagnostics.Stopwatch]::StartNew()
        try {
            & $mutationModule {
                param($path, $pin, $milliseconds)
                Get-JetMutationSourceArchive -ArchivePath $path -Pin $pin -TimeoutMilliseconds $milliseconds
            } $downloadPath $downloadPin $(if ($downloadCase -in @('stalled-body', 'drip-body')) { 750 } else { 5000 })
        } catch { $failure = $_.Exception.GetBaseException() }
        $elapsed.Stop()
        Assert-Mutation ($fixture.HeadersSent) ($downloadCase + ' actually receives response headers')
        if ($downloadCase -ceq 'complete') {
            Assert-Mutation ($null -eq $failure -and [IO.File]::Exists($downloadPath)) 'complete loopback body is published'
            Assert-Mutation ((Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $downloadHash) 'downloaded body has the pinned hash'
        } else {
            Assert-Mutation ($null -ne $failure -and ![IO.File]::Exists($downloadPath)) ($downloadCase + ' cannot publish an incomplete archive')
            if ($downloadCase -in @('stalled-body', 'drip-body')) {
                Assert-Mutation ($failure -is [OperationCanceledException]) ($downloadCase + ' fails by cancellation')
                Assert-Mutation ($elapsed.Elapsed.TotalSeconds -lt 4) ($downloadCase + ' uses one whole-transfer deadline')
                if ($downloadCase -ceq 'drip-body') { Assert-Mutation ($fixture.BytesSent -gt 1 -and $fixture.BytesSent -lt 128) 'multiple successful reads do not reset the deadline' }
            } elseif ($downloadCase -like '*oversize') {
                Assert-Mutation ($failure.Message.Contains('exceeds the download limit')) ($downloadCase + ' fails at the byte limit')
            } else { Assert-Mutation ($failure.Message.Contains('SHA256 differs')) 'wrong hash fails checksum validation' }
        }
        Assert-Mutation (@(Get-ChildItem -LiteralPath $downloadRoot -File -Filter 'download-*.partial').Count -eq 0) ($downloadCase + ' removes its partial file')
        Assert-Mutation ([IO.File]::ReadAllText($sentinel) -ceq 'owned by another invocation') ($downloadCase + ' preserves unrelated partial files')
    } finally { $fixture.Dispose() }
    Assert-Mutation ($fixture.Stopped) ($downloadCase + ' stops the loopback server')
}

# The outer one-second budget must also cancel initialization body reads, even
# though the download helper itself permits five seconds in this scenario.
$initializationRoot = Join-Path $mutationTestRoot 'initialization-budget'
[IO.Directory]::CreateDirectory($initializationRoot) | Out-Null
$initializationFixture = [Jet.Mutation.Contract.HttpFixture]::new('stalled-body', $downloadBody)
try {
    & $mutationModule {
        param($root, $url, $hash)
        $script:InitializationDownloadPath = Join-Path $root 'source.zip'
        $script:InitializationPin = @{ archiveUrl = $url; archiveMaximumBytes = 256; archiveSha256 = $hash }
        function script:Initialize-JetMutationToolSource {
            param($MutationRoot, $Pin, $PatchIdentity, [Threading.CancellationToken] $CancellationToken)
            Get-JetMutationSourceArchive -ArchivePath $script:InitializationDownloadPath -Pin $script:InitializationPin -TimeoutMilliseconds 5000 -CancellationToken $CancellationToken
            throw 'The initialization download unexpectedly completed.'
        }
    } $initializationRoot $initializationFixture.Url $downloadHash
    $initializationRun = Join-Path $initializationRoot 'run'
    [IO.Directory]::CreateDirectory($initializationRun) | Out-Null
    $initializationElapsed = [Diagnostics.Stopwatch]::StartNew()
    $initializationResult = Invoke-JetMutationPipeline -RepositoryRoot $initializationRoot -RunDirectory $initializationRun -Registry @{} -TimeoutSeconds 1 -RunChildStep { throw 'No child may start after initialization times out.' }
    $initializationElapsed.Stop()
    Assert-Mutation ($initializationFixture.HeadersSent) 'pipeline initialization reaches the body read'
    Assert-Mutation ($initializationResult.evidence.status -ceq 'blocked' -and $initializationResult.steps[-1].classification.reason -ceq 'mutation_timeout') 'pipeline initialization consumes the overall timeout budget'
    Assert-Mutation ($initializationElapsed.Elapsed.TotalSeconds -lt 4) 'pipeline deadline overrides the longer per-download allowance'
    Assert-Mutation (@(Get-ChildItem -LiteralPath $initializationRoot -File -Filter 'download-*.partial').Count -eq 0) 'pipeline cancellation removes its partial file'
} finally { $initializationFixture.Dispose() }
Assert-Mutation ($initializationFixture.Stopped) 'initialization cancellation stops its loopback server'

$syntheticRoot = Join-Path $mutationTestRoot 'input'
foreach ($relative in @('src/JET/JET/Domain', 'src/JET/bin', 'src/JET/obj', 'src/JET/artifacts', 'src/JET/.codex/sessions', 'src/JET/data', 'data/test-case')) {
    [IO.Directory]::CreateDirectory((Join-Path $syntheticRoot $relative)) | Out-Null
    [IO.File]::WriteAllText((Join-Path $syntheticRoot ($relative + '/synthetic.txt')), 'synthetic marker')
}
[IO.File]::WriteAllText((Join-Path $syntheticRoot 'global.json'), '{}')
[IO.File]::WriteAllText((Join-Path $syntheticRoot 'src/JET/JET/Domain/Uncommitted.cs'), '// synthetic uncommitted source')
[IO.File]::WriteAllText((Join-Path $syntheticRoot 'src/JET/JET/Domain/Unlisted.txt'), 'unlisted synthetic marker')
[IO.Directory]::CreateDirectory((Join-Path $syntheticRoot 'docs')) | Out-Null
[IO.File]::WriteAllLines((Join-Path $syntheticRoot 'docs/first-root-commit-candidate.txt'), @('global.json', 'src/JET/JET/Domain/synthetic.txt', 'src/JET/JET/Domain/Uncommitted.cs'))
$snapshotRoot = Join-Path $mutationTestRoot 'snapshot'
$snapshot = & $mutationModule { param($root, $destination) New-JetMutationSnapshot $root $destination } $syntheticRoot $snapshotRoot
Assert-Mutation ($snapshot.fileCount -eq 3) 'snapshot includes only global.json and synthetic source files'
Assert-Mutation (![IO.File]::Exists((Join-Path $snapshotRoot 'src/JET/JET/Domain/Unlisted.txt'))) 'snapshot excludes unlisted local files'
Assert-Mutation ($snapshot.files.path -ccontains 'src/JET/JET/Domain/Uncommitted.cs') 'uncommitted source included'
Assert-Mutation (!$snapshot.privateDataInspected) 'private input remains excluded'
foreach ($relative in @('src/JET/bin', 'src/JET/obj', 'src/JET/artifacts', 'src/JET/.codex', 'src/JET/data', 'data/test-case')) {
    Assert-Mutation (![IO.Directory]::Exists((Join-Path $snapshotRoot $relative))) ('excluded directory ' + $relative)
}
$reportRoot = Join-Path $mutationTestRoot 'evidence'
[IO.Directory]::CreateDirectory((Join-Path $reportRoot 'report')) | Out-Null
$scope = Get-JetMutationScope
$uid = 'a' * 64
$events = @(
    @{ kind = 'server_start'; detail = $scope.testClass; uidHashes = @(); processId = 1 },
    @{ kind = 'discovery'; detail = $scope.testClass; uidHashes = @($uid); processId = 1 },
    @{ kind = 'run'; detail = $scope.testClass; uidHashes = @($uid); processId = 1 },
    @{ kind = 'updates'; detail = $scope.testClass; uidHashes = @($uid); processId = 1 }
)
$selectionPath = Join-Path $reportRoot 'selection.jsonl'
$writeEvents = { param($items) [IO.File]::WriteAllLines($selectionPath, @($items | ForEach-Object { $_ | ConvertTo-Json -Compress })) }
& $writeEvents $events
$report = @{ files = @{ 'Domain/Rules/GlProjectionGuard.cs' = @{ mutants = @(@{ id = '1'; status = 'Survived'; mutatorName = 'Equality'; replacement = 'true'; location = @{ start = @{ line = 1; column = 1 }; end = @{ line = 1; column = 2 } } }) } } }
[IO.File]::WriteAllText((Join-Path $reportRoot 'report/mutation-report.json'), ($report | ConvertTo-Json -Depth 10))
[IO.File]::WriteAllText((Join-Path $reportRoot 'report/mutation-report.html'), '<html>synthetic report</html>')
$result = & $mutationModule { param($root, $scope) Read-JetMutationResult $root $scope } $reportRoot $scope
Assert-Mutation ($result.selectedTests -eq 1 -and $result.mutationCount -eq 1) 'actual counts retained'
Assert-Mutation ($result.counts.Survived -eq 1) 'Survived is counted under its status name'
Assert-Mutation ($result.survivorReviewRequired) 'survivors require human review'
$report.files['Domain/Rules/GlProjectionGuard.cs'].mutants[0].status = 'NoCoverage'
[IO.File]::WriteAllText((Join-Path $reportRoot 'report/mutation-report.json'), ($report | ConvertTo-Json -Depth 10))
$uncoveredResult = & $mutationModule { param($root, $scope) Read-JetMutationResult $root $scope } $reportRoot $scope
Assert-Mutation ($uncoveredResult.survivorReviewRequired) 'NoCoverage-only reports require human review'
Assert-Mutation ($uncoveredResult.counts.NoCoverage -eq 1 -and $uncoveredResult.mutants[0].status -ceq 'NoCoverage') 'NoCoverage counts and source status are retained'
$report.files['Domain/Rules/GlProjectionGuard.cs'].mutants[0].status = 'CompileError'
[IO.File]::WriteAllText((Join-Path $reportRoot 'report/mutation-report.json'), ($report | ConvertTo-Json -Depth 10))
$compileErrorResult = & $mutationModule { param($root, $scope) Read-JetMutationResult $root $scope } $reportRoot $scope
Assert-Mutation ($compileErrorResult.status -ceq 'passed' -and !$compileErrorResult.survivorReviewRequired -and $compileErrorResult.counts.CompileError -eq 1) 'CompileError remains a mutation classification without becoming a product failure'
$report.files['Domain/Rules/GlProjectionGuard.cs'].mutants[0].status = 'Survived'
$singleMutant = $report.files['Domain/Rules/GlProjectionGuard.cs'].mutants[0]
$mixedOrdinal = 0
$report.files['Domain/Rules/GlProjectionGuard.cs'].mutants = @('Killed', 'Survived', 'NoCoverage', 'CompileError', 'Killed') | ForEach-Object {
    $mixedOrdinal += 1
    $entry = @{} + $singleMutant
    $entry['id'] = [string]$mixedOrdinal
    $entry['status'] = $_
    $entry
}
[IO.File]::WriteAllText((Join-Path $reportRoot 'report/mutation-report.json'), ($report | ConvertTo-Json -Depth 10))
$mixedResult = & $mutationModule { param($root, $scope) Read-JetMutationResult $root $scope } $reportRoot $scope
Assert-Mutation ($mixedResult.counts.Killed -eq 2 -and $mixedResult.counts.Survived -eq 1 -and $mixedResult.counts.NoCoverage -eq 1 -and $mixedResult.counts.CompileError -eq 1) 'mixed statuses retain their separate and repeated counts'
Assert-Mutation (($mixedResult.counts.Values | Measure-Object -Sum).Sum -eq $mixedResult.mutationCount -and $mixedResult.mutationCount -eq 5 -and !$mixedResult.counts.Contains('')) 'status counts sum to the mutation count without an empty key'
Assert-Mutation ($mixedResult.survivorReviewRequired -and $mixedResult.status -ceq 'passed') 'mixed undetected mutants require review without changing measurement status'
$report.files['Domain/Rules/GlProjectionGuard.cs'].mutants = @($singleMutant)
[IO.File]::WriteAllText((Join-Path $reportRoot 'report/mutation-report.json'), ($report | ConvertTo-Json -Depth 10))
$events[2].uidHashes = @('b' * 64)
& $writeEvents $events
Assert-MutationThrows { & $mutationModule { param($root, $scope) Read-JetMutationResult $root $scope } $reportRoot $scope } 'unknown UID evidence rejected'
$events[2].uidHashes = @()
& $writeEvents $events
Assert-MutationThrows { & $mutationModule { param($root, $scope) Read-JetMutationResult $root $scope } $reportRoot $scope } 'empty run evidence rejected'
$events[2].uidHashes = @($uid)
$events[2].detail = '*'
& $writeEvents $events
Assert-MutationThrows { & $mutationModule { param($root, $scope) Read-JetMutationResult $root $scope } $reportRoot $scope } 'wrong class evidence rejected'

# Exercise early-return status bookkeeping with synthetic child results. No
# download, restore, build, tool execution, or product snapshot occurs here.
$mockToolRoot = Join-Path $mutationTestRoot 'mock-tool'
$mockCli = Join-Path $mockToolRoot 'src/Stryker.CLI/Stryker.CLI/bin/Release/Stryker.CLI.dll'
[IO.Directory]::CreateDirectory((Split-Path $mockCli -Parent)) | Out-Null
[IO.File]::WriteAllText($mockCli, 'synthetic non-executable marker')
& $mutationModule {
    param($mockRoot)
    $script:MutationContractMockRoot = $mockRoot
    function script:Initialize-JetMutationToolSource {
        param($MutationRoot, $Pin, $PatchIdentity, $CancellationToken)
        return @{ cacheRoot = $script:MutationContractMockRoot; sourceRoot = $script:MutationContractMockRoot; qualificationProject = 'synthetic.csproj'; coreQualificationProject = 'synthetic-core.csproj'; cliProject = 'synthetic-tool.csproj' }
    }
    function script:New-JetMutationSnapshot {
        param($RepositoryRoot, $Destination, $CancellationToken)
        [IO.Directory]::CreateDirectory($Destination) | Out-Null
        return @{ files = @(); fileCount = 0; synthetic = $true }
    }
} $mockToolRoot
$mockRegistry = @{ limits = @{ maximumCapturedBytesPerStream = 262144 }; testSettings = @{ environmentVariablesToRemove = @() } }
foreach ($failureTarget in @('mutation-qualification-build', 'mutation-qualification', 'mutation-tool-build', 'mutation-source-qualification-build', 'mutation-source-qualification', 'mutation-capability', 'mutation-run')) {
    $mockRun = Join-Path $syntheticRoot ('artifacts/harness/runs/' + $failureTarget)
    [IO.Directory]::CreateDirectory($mockRun) | Out-Null
    $mockChild = {
        param([hashtable] $parameters)
        if ($parameters.Name -ceq 'mutation-capability') { [IO.File]::WriteAllText((Join-Path $parameters.RunDirectory 'mutation-capability.stdout.log'), '--test-runner mtp --concurrency') }
        return @{ name = $parameters.Name; status = $(if ($parameters.Name -ceq $failureTarget) { 'failed' } else { 'passed' }); classification = @{ reason = 'synthetic_child'; detail = '' }; process = @{ exitCode = $(if ($parameters.Name -ceq $failureTarget) { 17 } else { 0 }) } }
    }.GetNewClosure()
    $failedPipeline = Invoke-JetMutationPipeline -RepositoryRoot $syntheticRoot -RunDirectory $mockRun -Registry $mockRegistry -RunChildStep $mockChild
    Assert-Mutation ($failedPipeline.evidence.status -ceq 'failed') ($failureTarget + ' propagates failed status')
    Assert-Mutation ($failedPipeline.steps[-1].name -ceq $failureTarget -and $failedPipeline.steps[-1].process.exitCode -eq 17) ($failureTarget + ' preserves original failing step')
}
[pscustomobject]@{ status = 'passed'; assertions = $assertions; privateDataInspected = $false; productTestsExecuted = $false } | ConvertTo-Json -Compress
