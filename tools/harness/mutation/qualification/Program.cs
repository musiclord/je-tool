using System.Text.Json;
using Microsoft.Extensions.Logging;
using Stryker.TestRunner.MicrosoftTestPlatform;
using Stryker.TestRunner.MicrosoftTestPlatform.Models;

var evidencePath = args[0];
Environment.SetEnvironmentVariable("JET_MUTATION_EVIDENCE_PATH", evidencePath);
Environment.SetEnvironmentVariable("JET_MUTATION_TEST_CLASS", "JET.Tests.Domain.GlProjectionGuardTests");
var cases = new List<string>();
var assertions = 0;
void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
async Task Reject(Func<Task> action, string name)
{
    try { await action(); } catch (InvalidOperationException ex) when (ex.Message.StartsWith("JET mutation boundary:")) { cases.Add(name); return; }
    throw new Exception("Expected boundary rejection: " + name);
}
TestNode Node(string id, string? type = "JET.Tests.Domain.GlProjectionGuardTests") => new(id, id, "test", "discovered", LocationType: type);
const string assembly = "synthetic/JET.Tests.dll";
var first = Node("synthetic-one");
var second = Node("synthetic-two");
var transport = new RecordingTransport();
using var server = new AssemblyTestServer(assembly, [], new SilentLogger(), "qualification", connectionFactory: transport);
Check(await server.StartAsync(), "server starts");
var cli = transport.Starts.Single();
Check(cli.SequenceEqual(new[] { assembly, "--server", "--client-port", "19001", "--filter-class", "JET.Tests.Domain.GlProjectionGuardTests", "--filter-not-trait", "TestProfile=PrivateCase", "TestProfile=Provider", "TestProfile=Scale", "--parallel", "none", "--seed", "20260828", "--no-ansi" }), "exact MTP CLI filters");
cases.Add("exact_cli_filters");
await Reject(() => { Environment.SetEnvironmentVariable("JET_MUTATION_TEST_CLASS", "*"); JetMtpBoundary.ServerArguments(assembly, 19001); return Task.CompletedTask; }, "invalid_filter");
Environment.SetEnvironmentVariable("JET_MUTATION_TEST_CLASS", "JET.Tests.Domain.GlProjectionGuardTests");
await Reject(() => { JetMtpBoundary.RegisterDiscovery(assembly, []); return Task.CompletedTask; }, "empty_discovery");
await Reject(() => { JetMtpBoundary.RegisterDiscovery(assembly, [Node("outside", "Synthetic.Other")]); return Task.CompletedTask; }, "wrong_discovery_class");
await Reject(() => { JetMtpBoundary.RegisterDiscovery(assembly, [Node("unknown", null)]); return Task.CompletedTask; }, "missing_discovery_class");
await Reject(() => { JetMtpBoundary.RegisterDiscovery(assembly, [first, first]); return Task.CompletedTask; }, "duplicate_discovery_uid");
await Reject(async () => { await server.RunTestsAsync([first]); }, "missing_discovery");
JetMtpBoundary.RegisterDiscovery(assembly, [first, second]);
await Reject(async () => { await server.RunTestsAsync(null); }, "null_selection");
await Reject(async () => { await server.RunTestsAsync([]); }, "empty_selection");
await Reject(async () => { await server.RunTestsAsync([Node("unknown")]); }, "unknown_uid");
await Reject(async () => { await server.RunTestsAsync([first, first]); }, "duplicate_selection_uid");
Check(transport.Runs.Count == 0, "no rejected selection reaches RPC");
foreach (var stage in new[] { "initial", "coverage", "mutant" })
{
    await server.RunTestsAsync(stage == "initial" ? [first, second] : [first]);
    Check(transport.Runs.Last().SequenceEqual(stage == "initial" ? new[] { first.Uid, second.Uid } : new[] { first.Uid }), stage + " checked UIDs");
    cases.Add(stage + "_selection");
}
transport.NextUpdate = Node("outside");
await Reject(async () => { await server.RunTestsAsync([first]); }, "unexpected_execution_uid");
transport.BlockRpc = true;
var timed = await server.RunTestsAsync([first], TimeSpan.FromMilliseconds(25));
Check(timed.TimedOut, "RPC timeout reaches caller");
cases.Add("rpc_timeout");
await server.RestartAsync(force: true);
Check(transport.KillCount == 1 && transport.Starts.Count == 2, "timeout restart kills owned host and reapplies CLI");
Check(transport.Starts[0].SequenceEqual(transport.Starts[1]), "restart preserves filters");
transport.BlockRpc = false;
await server.RunTestsAsync([second]);
Check(transport.Runs.Last().SequenceEqual(new[] { second.Uid }), "restart retains checked subset");
cases.Add("restart_selection");
transport.CancelRpc = true;
var callsBeforeCancel = transport.Runs.Count;
try { await server.RunTestsAsync([first]); throw new Exception("Expected RPC cancellation."); }
catch (OperationCanceledException) { cases.Add("cancelled_rpc"); }
Check(transport.Runs.Count == callsBeforeCancel + 1 && transport.Runs.Last().SequenceEqual(new[] { first.Uid }), "cancellation does not broaden or retry the RPC");
await server.RestartAsync(force: true);
transport.CancelRpc = false;
await server.RunTestsAsync([second]);
Check(transport.Starts.Count == 3 && transport.Starts.All(arguments => arguments.SequenceEqual(cli)), "cancelled RPC restart preserves CLI filters");
Check(transport.Runs.Last().SequenceEqual(new[] { second.Uid }), "cancelled RPC restart preserves the selected UID");
cases.Add("cancelled_rpc_restart");
await Reject(() => { JetMtpBoundary.RegisterDiscovery(assembly, [Node("changed")]); return Task.CompletedTask; }, "restart_discovery_changed");
await server.StopAsync(force: true);
var cancellation = new CancellationTokenSource();
cancellation.Cancel();
transport.CancelConnection = true;
Check(!await server.StartAsync(cancellation.Token), "cancelled start cannot become initialized");
Check(!server.IsInitialized, "cancel leaves server uninitialized");
cases.Add("cancelled_start");
var runnerSource = File.ReadAllText(Path.Combine(args[1], "src/Stryker.TestRunner.MicrosoftTestPlatform/MicrosoftTestingPlatformRunner.cs"));
var factorySource = File.ReadAllText(Path.Combine(args[1], "src/Stryker.TestRunner.MicrosoftTestPlatform/DefaultTestServerConnectionFactory.cs"));
Check(runnerSource.Contains("JetMtpBoundary.RegisterDiscovery(assembly, tests);"), "actual runner discovery is checked");
Check(factorySource.Contains(".WithArguments(JetMtpBoundary.ServerArguments(assembly, port))"), "actual production transport uses checked CLI");
Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", assertions, cases, actualAssemblyTestServer = true, privateDataInspected = false }));

sealed class RecordingTransport : ITestServerConnectionFactory, ITestServerListener, ITestServerProcess, ITestingPlatformClient, IProcessHandle
{
    public List<string[]> Starts { get; } = [];
    public List<string[]> Runs { get; } = [];
    public int KillCount { get; private set; }
    public bool BlockRpc { get; set; }
    public bool CancelRpc { get; set; }
    public bool CancelConnection { get; set; }
    public TestNode? NextUpdate { get; set; }
    public (ITestServerListener Listener, int Port) CreateListener() => (this, 19001);
    public ITestServerProcess StartProcess(string assembly, int port, Dictionary<string, string?> environmentVariables) { Starts.Add(JetMtpBoundary.ServerArguments(assembly, port)); return this; }
    public ITestingPlatformClient CreateClient(Stream stream, IProcessHandle processHandle, ILogger logger, string? rpcLogFilePath) => this;
    public Task<(Stream Stream, IDisposable Connection)> AcceptConnectionAsync(CancellationToken token) => CancelConnection ? Task.FromCanceled<(Stream, IDisposable)>(token) : Task.FromResult<(Stream, IDisposable)>((new MemoryStream(), new MemoryStream()));
    public void Stop() { }
    Task ITestServerProcess.WaitForExitAsync() => new TaskCompletionSource().Task;
    public bool HasExited => false;
    public IProcessHandle ProcessHandle => this;
    public Task InitializeAsync() => Task.CompletedTask;
    public Task ExitAsync(bool gracefully = true) => Task.CompletedTask;
    public Task<int> WaitServerProcessExitAsync() => Task.FromResult(0);
    public Task<ResponseListener> DiscoverTestsAsync(Guid requestId, Func<TestNodeUpdate[], Task> action) => Task.FromResult(new ResponseListener());
    public async Task<ResponseListener> RunTestsAsync(Guid requestId, Func<TestNodeUpdate[], Task> action, TestNode[]? testNodes)
    {
        if (testNodes is null || testNodes.Length == 0) throw new Exception("Unbounded RPC reached transport.");
        Runs.Add(testNodes.Select(t => t.Uid).ToArray());
        if (CancelRpc) throw new OperationCanceledException("synthetic RPC cancellation");
        if (BlockRpc) return await new TaskCompletionSource<ResponseListener>().Task;
        var updates = NextUpdate is null ? testNodes.Select(t => new TestNodeUpdate(t with { ExecutionState = "passed" }, "")).ToArray() : [new TestNodeUpdate(NextUpdate, "")];
        NextUpdate = null;
        await action(updates);
        return new ResponseListener();
    }
    public int Id => 19002;
    public string ProcessName => "synthetic";
    public int ExitCode => 0;
    public TextWriter StandardInput => TextWriter.Null;
    public TextReader StandardOutput => TextReader.Null;
    public void Kill() => KillCount++;
    public Task<int> StopAsync() => Task.FromResult(0);
    public Task<int> WaitForExitAsync() => new TaskCompletionSource<int>().Task;
    public Task WriteInputAsync(string input) => Task.CompletedTask;
    public void Dispose() { }
}
