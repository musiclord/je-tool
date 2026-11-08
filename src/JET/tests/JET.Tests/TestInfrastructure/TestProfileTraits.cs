namespace JET.Tests.TestInfrastructure;

internal static class TestProfileTraits
{
    internal const string Key = "TestProfile";
    internal const string CapabilityKey = "Capability";

    // 原本另有 Provider 與 LocalDbProvider 兩組標記，給 live SQL Server 測試用；隨 SQL Server 移除（裁定 2026-11-06-08）。

    internal static readonly IReadOnlyCollection<KeyValuePair<string, string>> Scale =
        [new(Key, "Scale")];

    internal static readonly IReadOnlyCollection<KeyValuePair<string, string>> PrivateCase =
        [new(Key, "PrivateCase")];

    internal static readonly IReadOnlyCollection<KeyValuePair<string, string>> FileSystemLinks =
        [new(CapabilityKey, "FileSystemLinks")];

}
