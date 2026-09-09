// JET-only adapter for the pinned Stryker revision. Replaced when upstream offers
// equivalent global MTP filters and a checked UID selection at every RPC call.
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stryker.TestRunner.MicrosoftTestPlatform.Models;

namespace Stryker.TestRunner.MicrosoftTestPlatform;

internal static class JetMtpBoundary
{
    private static readonly ConcurrentDictionary<string, HashSet<string>> Discovered = new(StringComparer.Ordinal);
    private static readonly object EvidenceLock = new();
    internal static string SelectedClass => Environment.GetEnvironmentVariable("JET_MUTATION_TEST_CLASS") switch
    {
        "JET.Tests.Domain.GlProjectionGuardTests" => "JET.Tests.Domain.GlProjectionGuardTests",
        "JET.Tests.Domain.MoneyScalingTests" => "JET.Tests.Domain.MoneyScalingTests",
        _ => throw Reject("invalid_filter")
    };

    internal static string[] ServerArguments(string assembly, int port)
    {
        var selectedClass = SelectedClass;
        Evidence("server_start", [], selectedClass);
        return [assembly, "--server", "--client-port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--filter-class", selectedClass, "--filter-not-trait", "TestProfile=PrivateCase", "TestProfile=Provider", "TestProfile=Scale",
            "--parallel", "none", "--seed", "20260828", "--no-ansi"];
    }

    internal static void RegisterDiscovery(string assembly, IReadOnlyList<TestNode> tests)
    {
        var selectedClass = SelectedClass;
        if (tests.Count == 0) throw Reject("empty_discovery");
        var uids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var test in tests)
        {
            if (string.IsNullOrWhiteSpace(test.Uid) || test.LocationType != selectedClass || !uids.Add(test.Uid))
                throw Reject("discovery_outside_scope");
        }
        if (Discovered.TryGetValue(assembly, out var previous) && !previous.SetEquals(uids))
            throw Reject("discovery_changed_after_restart");
        Discovered[assembly] = uids;
        Evidence("discovery", tests.Select(t => t.Uid), selectedClass);
    }

    internal static HashSet<string> ValidateRun(string assembly, TestNode[]? tests)
    {
        var selectedClass = SelectedClass;
        if (tests is null || tests.Length == 0) throw Reject("empty_selection");
        if (!Discovered.TryGetValue(assembly, out var known)) throw Reject("missing_discovery");
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var test in tests)
        {
            if (test.LocationType != selectedClass || string.IsNullOrWhiteSpace(test.Uid) || !known.Contains(test.Uid) || !selected.Add(test.Uid))
                throw Reject("unknown_uid");
        }
        Evidence("run", selected, selectedClass);
        return selected;
    }

    internal static void ValidateUpdates(HashSet<string> selected, TestNodeUpdate[] updates)
    {
        foreach (var update in updates)
        {
            if (!selected.Contains(update.Node.Uid)) throw Reject("unexpected_execution_uid");
            if (update.Node.LocationType is not null && update.Node.LocationType != SelectedClass)
                throw Reject("unexpected_execution_class");
        }
        Evidence("updates", updates.Select(t => t.Node.Uid), SelectedClass);
    }

    private static InvalidOperationException Reject(string reason)
    {
        Evidence("rejected", [], reason);
        return new InvalidOperationException("JET mutation boundary: " + reason);
    }

    private static void Evidence(string kind, IEnumerable<string> uids, string detail)
    {
        var path = Environment.GetEnvironmentVariable("JET_MUTATION_EVIDENCE_PATH");
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("JET mutation evidence path is required.");
        var hashes = uids.Select(uid => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uid))).ToLowerInvariant()).Distinct().Order().ToArray();
        var line = JsonSerializer.Serialize(new { kind, detail, uidHashes = hashes, processId = Environment.ProcessId }) + "\n";
        lock (EvidenceLock)
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            if (stream.Length + Encoding.UTF8.GetByteCount(line) > 8 * 1024 * 1024)
                throw new InvalidOperationException("JET mutation evidence exceeds its 8 MB limit.");
            stream.Write(Encoding.UTF8.GetBytes(line));
            stream.Flush();
        }
    }
}
