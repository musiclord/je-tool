#requires -Version 7.4

[CmdletBinding()]
param([AllowNull()] [string] $RepositoryRoot = $null)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The owned process-tree contract requires Windows.' }

$repositoryRoot = if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
}
else { [IO.Path]::GetFullPath($RepositoryRoot) }
$sourcePath = Join-Path $repositoryRoot 'tools/harness/JetHarness.Process.cs'
$suiteRoot = Join-Path $repositoryRoot ('artifacts/harness/process-tree-contract/{0}' -f [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($suiteRoot) | Out-Null
$pwshPath = [Environment]::ProcessPath
$assertionCount = 0
$scenarioNames = [Collections.Generic.List[string]]::new()

Add-Type -Path $sourcePath -ErrorAction Stop
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
public static class ProcessTreeContractCancellation
{
    public static Task CancelWhenReady(string path, CancellationTokenSource cancellation)
    {
        return Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 300; attempt++)
            {
                if (File.Exists(path))
                {
                    cancellation.Cancel();
                    return;
                }
                await Task.Delay(50).ConfigureAwait(false);
            }
            cancellation.Cancel();
            throw new TimeoutException("The synthetic parent did not become ready for cancellation.");
        });
    }
}
'@ -ErrorAction Stop

function Assert-Contract {
    param([bool] $Condition, [string] $Message)
    $script:assertionCount++
    if (-not $Condition) { throw [InvalidOperationException]::new($Message) }
}

$childPath = Join-Path $suiteRoot 'child.ps1'
@'
param([string] $EvidencePath)
$ErrorActionPreference = 'Stop'
$process = [Diagnostics.Process]::GetCurrentProcess()
[Console]::Out.WriteLine('child-holds-stdout')
[Console]::Error.WriteLine('child-holds-stderr')
@{
    pid = $PID
    startTicks = $process.StartTime.ToUniversalTime().Ticks
    priority = [string]$process.PriorityClass
} | ConvertTo-Json -Compress | Set-Content -LiteralPath "$EvidencePath.tmp" -Encoding utf8
[IO.File]::Move("$EvidencePath.tmp", $EvidencePath)
Start-Sleep -Seconds 60
'@ | Set-Content -LiteralPath $childPath -Encoding utf8

$parentPath = Join-Path $suiteRoot 'parent.ps1'
@'
param(
    [string] $Mode,
    [string] $EvidenceRoot,
    [string] $PowerShellPath,
    [string] $ChildPath,
    [Parameter(ValueFromRemainingArguments = $true)] [string[]] $ProbeArguments
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$process = [Diagnostics.Process]::GetCurrentProcess()
@{
    pid = $PID
    startTicks = $process.StartTime.ToUniversalTime().Ticks
    priority = [string]$process.PriorityClass
    workingDirectory = [Environment]::CurrentDirectory
    arguments = @([Environment]::GetCommandLineArgs())
    inherited = [Environment]::GetEnvironmentVariable('JET_PROCESS_KEEP')
    removed = [Environment]::GetEnvironmentVariable('JET_PROCESS_REMOVE')
    overridden = [Environment]::GetEnvironmentVariable('JET_PROCESS_SET')
} | ConvertTo-Json -Depth 5 -Compress | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'parent.json') -Encoding utf8
[Console]::Out.WriteLine('root-output 中文')
[Console]::Out.WriteLine('synthetic-redaction-token')
[Console]::Out.WriteLine([Environment]::CurrentDirectory)
[Console]::Error.WriteLine('root-error 中文')
if ($Mode -eq 'flood') {
    for ($index = 0; $index -lt 100; $index++) {
        [Console]::Out.WriteLine('中' * 100)
        [Console]::Error.WriteLine('文' * 100)
    }
}
if ($Mode -ne 'simple') {
    $childEvidencePath = Join-Path $EvidenceRoot 'child.json'
    $startInfo = [Diagnostics.ProcessStartInfo]::new($PowerShellPath)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    # 指定 stdin 重導向，讓 .NET 同時把父程序的 stdout 和 stderr handles 傳給沒有視窗的子程序。
    $startInfo.RedirectStandardInput = $true
    foreach ($argument in @('-NoProfile', '-File', $ChildPath, $childEvidencePath)) {
        $startInfo.ArgumentList.Add($argument)
    }
    $child = [Diagnostics.Process]::Start($startInfo)
    $child.StandardInput.Close()
    try {
        $readyDeadline = [DateTime]::UtcNow.AddSeconds(10)
        while (-not [IO.File]::Exists($childEvidencePath)) {
            if ($child.HasExited -or [DateTime]::UtcNow -ge $readyDeadline) { throw 'The synthetic child did not become ready.' }
            Start-Sleep -Milliseconds 20
        }
        [IO.File]::WriteAllText((Join-Path $EvidenceRoot 'ready'), 'ready')
        if ($Mode -in @('timeout', 'cancel')) { Start-Sleep -Seconds 60 }
    }
    finally { $child.Dispose() }
}
exit 17
'@ | Set-Content -LiteralPath $parentPath -Encoding utf8

function Test-RecordedProcessExited {
    param($Evidence)
    $process = $null
    try {
        $process = [Diagnostics.Process]::GetProcessById([int]$Evidence.pid)
        return $process.HasExited -or $process.StartTime.ToUniversalTime().Ticks -ne [long]$Evidence.startTicks
    }
    catch [ArgumentException] { return $true }
    finally { if ($null -ne $process) { $process.Dispose() } }
}

$previousEnvironment = @{}
foreach ($name in @('JET_PROCESS_KEEP', 'JET_PROCESS_REMOVE', 'JET_PROCESS_SET')) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
$sibling = $null
try {
    [Environment]::SetEnvironmentVariable('JET_PROCESS_KEEP', 'inherited-synthetic-value')
    [Environment]::SetEnvironmentVariable('JET_PROCESS_REMOVE', 'removed-synthetic-value')
    [Environment]::SetEnvironmentVariable('JET_PROCESS_SET', 'old-synthetic-value')
    $siblingInfo = [Diagnostics.ProcessStartInfo]::new($pwshPath)
    $siblingInfo.UseShellExecute = $false
    $siblingInfo.CreateNoWindow = $true
    $siblingInfo.RedirectStandardOutput = $true
    $siblingInfo.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-File', $childPath, (Join-Path $suiteRoot 'sibling.json'))) {
        $siblingInfo.ArgumentList.Add($argument)
    }
    $sibling = [Diagnostics.Process]::Start($siblingInfo)
    $probeArguments = [string[]]@('', 'with spaces', 'quote"inside', 'trailing\', 'slash\"quote', '中文 & $() ;')
    foreach ($mode in @('simple', 'normal', 'flood', 'timeout', 'cancel')) {
        $scenarioRoot = Join-Path $suiteRoot "$mode with spaces"
        [IO.Directory]::CreateDirectory($scenarioRoot) | Out-Null
        $stdoutPath = Join-Path $scenarioRoot 'stdout.log'
        $stderrPath = Join-Path $scenarioRoot 'stderr.log'
        $arguments = [string[]](@('-NoProfile', '-File', $parentPath, $mode, $scenarioRoot, $pwshPath, $childPath) + $probeArguments)
        $overrides = [Collections.Generic.Dictionary[string, string]]::new()
        $overrides.Add('JET_PROCESS_SET', 'override 中文')
        $cancellation = [Threading.CancellationTokenSource]::new()
        $cancelTask = $null
        try {
            if ($mode -eq 'cancel') {
                $cancelTask = [ProcessTreeContractCancellation]::CancelWhenReady((Join-Path $scenarioRoot 'ready'), $cancellation)
            }
            $stopwatch = [Diagnostics.Stopwatch]::StartNew()
            $result = if ($mode -eq 'simple') {
                [Jet.Harness.BoundedProcessRunner]::Run(
                    $pwshPath, $arguments, $scenarioRoot, $null, $stdoutPath, $stderrPath,
                    4096, [TimeSpan]::FromSeconds(20), [string[]]@('JET_PROCESS_REMOVE'),
                    $overrides, [string[]]@('synthetic-redaction-token'), 65001)
            }
            else {
                [Jet.Harness.BoundedProcessRunner]::Run(
                    $pwshPath, $arguments, $scenarioRoot, $null, $stdoutPath, $stderrPath,
                    4096, [TimeSpan]::FromSeconds($(if ($mode -eq 'timeout') { 5 } else { 20 })),
                    [string[]]@('JET_PROCESS_REMOVE'), $overrides, [string[]]@('synthetic-redaction-token'),
                    65001, $true, $cancellation.Token)
            }
            if ($null -ne $cancelTask) { $cancelTask.GetAwaiter().GetResult() }
            $stopwatch.Stop()
        }
        finally { $cancellation.Dispose() }

        $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $scenarioRoot 'result.json') -Encoding utf8
        $parent = Get-Content -LiteralPath (Join-Path $scenarioRoot 'parent.json') -Raw -Encoding utf8 | ConvertFrom-Json
        Assert-Contract ($stopwatch.Elapsed.TotalSeconds -lt 15) "$mode did not finish before the synthetic child lifetime."
        Assert-Contract ($result.ProcessId -eq $parent.pid) "$mode changed the root process identity."
        Assert-Contract (Test-RecordedProcessExited $parent) "$mode left its root process running."
        Assert-Contract (-not $sibling.HasExited) "$mode stopped an unrelated process."
        Assert-Contract ($result.HasExitCode) "$mode lost the root exit code."
        Assert-Contract ($result.TimedOut -eq ($mode -eq 'timeout')) "$mode reported the wrong timeout state."
        Assert-Contract ($result.Cancelled -eq ($mode -eq 'cancel')) "$mode reported the wrong cancellation state."
        Assert-Contract ($parent.inherited -ceq 'inherited-synthetic-value') "$mode lost an inherited environment variable."
        Assert-Contract ($null -eq $parent.removed) "$mode did not remove the requested environment variable."
        Assert-Contract ($parent.overridden -ceq 'override 中文') "$mode did not apply the requested environment override."
        Assert-Contract ($parent.workingDirectory -ceq $scenarioRoot) "$mode changed the requested working directory."
        $actualArguments = @($parent.arguments | Select-Object -Last $probeArguments.Length)
        Assert-Contract ($actualArguments.Count -eq $probeArguments.Length) "$mode dropped an argument."
        for ($index = 0; $index -lt $probeArguments.Length; $index++) {
            Assert-Contract ($actualArguments[$index] -ceq $probeArguments[$index]) "$mode changed argument $index."
        }
        $stdout = Get-Content -LiteralPath $stdoutPath -Raw -Encoding utf8
        $stderr = Get-Content -LiteralPath $stderrPath -Raw -Encoding utf8
        Assert-Contract ($stdout.Contains('root-output 中文')) "$mode lost UTF-8 stdout."
        Assert-Contract ($stderr.Contains('root-error 中文')) "$mode lost UTF-8 stderr."
        Assert-Contract ($stdout.Contains('[sensitive]') -and -not $stdout.Contains('synthetic-redaction-token')) "$mode changed sensitive-value redaction."
        Assert-Contract ($stdout.Contains('[repository]') -and -not $stdout.Contains($scenarioRoot)) "$mode changed path redaction."
        Assert-Contract ($result.StandardOutputBytes -eq ([IO.FileInfo]::new($stdoutPath)).Length) "$mode reported the wrong stdout byte count."
        Assert-Contract ($result.StandardErrorBytes -eq ([IO.FileInfo]::new($stderrPath)).Length) "$mode reported the wrong stderr byte count."
        Assert-Contract ($result.StandardOutputBytes -le 4096 -and $result.StandardErrorBytes -le 4096) "$mode exceeded the standard-stream capture bounds."
        Assert-Contract ($result.StandardOutputTruncated -eq ($mode -eq 'flood') -and $result.StandardErrorTruncated -eq ($mode -eq 'flood')) "$mode reported incorrect standard-stream truncation."
        if ($mode -eq 'flood') {
            Assert-Contract ($stdout.Contains('[output truncated]') -and $stderr.Contains('[output truncated]')) 'Large inherited output must retain a truncation marker in both streams.'
        }
        if ($mode -eq 'simple') {
            Assert-Contract (-not $result.OwnedProcessTree -and $null -eq $result.CleanupSucceeded -and $null -eq $result.BelowNormalApplied) 'Default calls must not claim owned-tree cleanup or priority changes.'
            Assert-Contract (-not $result.KillAttempted) 'Default normal exit must not attempt a kill.'
        }
        else {
            $child = Get-Content -LiteralPath (Join-Path $scenarioRoot 'child.json') -Raw -Encoding utf8 | ConvertFrom-Json
            Assert-Contract (Test-RecordedProcessExited $child) "$mode left its descendant running."
            Assert-Contract ($result.OwnedProcessTree -and $result.CleanupSucceeded -and $result.BelowNormalApplied) "$mode did not confirm ownership, priority and cleanup."
            Assert-Contract ($result.KillAttempted -and $result.KillSucceeded -and $null -eq $result.KillError) "$mode did not complete owned cleanup."
            Assert-Contract ($parent.priority -ceq 'BelowNormal' -and $child.priority -ceq 'BelowNormal') "$mode did not apply BelowNormal to the real process tree."
            if ($mode -ne 'flood') {
                Assert-Contract ($stdout.Contains('child-holds-stdout') -and $stderr.Contains('child-holds-stderr')) "$mode did not exercise inherited standard streams."
            }
        }
        if ($mode -in @('simple', 'normal', 'flood')) {
            Assert-Contract ($result.ExitCode -eq 17) "$mode changed the parent's original exit code."
        }
        $scenarioNames.Add($mode)
    }

    $cancelledRoot = Join-Path $suiteRoot 'cancelled before start'
    [IO.Directory]::CreateDirectory($cancelledRoot) | Out-Null
    $cancelled = [Threading.CancellationTokenSource]::new()
    try {
        $cancelled.Cancel()
        $failure = $null
        try {
            [Jet.Harness.BoundedProcessRunner]::Run(
                $pwshPath, [string[]]@('-NoProfile', '-File', $parentPath, 'simple', $cancelledRoot, $pwshPath, $childPath),
                $cancelledRoot, $null, (Join-Path $cancelledRoot 'stdout.log'), (Join-Path $cancelledRoot 'stderr.log'),
                4096, [TimeSpan]::FromSeconds(20), [string[]]::new(0),
                [Collections.Generic.Dictionary[string, string]]::new(), [string[]]::new(0),
                65001, $true, $cancelled.Token) | Out-Null
        }
        catch { $failure = $_.Exception.GetBaseException() }
        Assert-Contract ($failure -is [OperationCanceledException]) 'Pre-cancelled owned work must report cancellation.'
        Assert-Contract (-not [IO.File]::Exists((Join-Path $cancelledRoot 'parent.json'))) 'Pre-cancelled owned work must not start the target.'
        Assert-Contract (-not $sibling.HasExited) 'Pre-cancelled owned work must leave the unrelated process running.'
        $scenarioNames.Add('cancel-before-start')
    }
    finally { $cancelled.Dispose() }

    [ordered]@{ status = 'passed'; assertions = $assertionCount; scenarios = $scenarioNames.ToArray() } |
        ConvertTo-Json -Compress -Depth 5
}
finally {
    if ($null -ne $sibling) {
        if (-not $sibling.HasExited) { $sibling.Kill(); $sibling.WaitForExit(10000) | Out-Null }
        $sibling.Dispose()
    }
    # 失敗時也只收掉本套合成情境記下且啟動時間吻合的程序，不掃描其他程序。
    foreach ($evidencePath in [IO.Directory]::GetFiles($suiteRoot, '*.json', [IO.SearchOption]::AllDirectories)) {
        if ([IO.Path]::GetFileName($evidencePath) -notin @('parent.json', 'child.json')) { continue }
        $evidence = Get-Content -LiteralPath $evidencePath -Raw -Encoding utf8 | ConvertFrom-Json
        $owned = $null
        try {
            $owned = [Diagnostics.Process]::GetProcessById([int]$evidence.pid)
            if (-not $owned.HasExited -and $owned.StartTime.ToUniversalTime().Ticks -eq [long]$evidence.startTicks) {
                $owned.Kill()
                $owned.WaitForExit(10000) | Out-Null
            }
        }
        catch [ArgumentException] { }
        finally { if ($null -ne $owned) { $owned.Dispose() } }
    }
    foreach ($entry in $previousEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value)
    }
}
