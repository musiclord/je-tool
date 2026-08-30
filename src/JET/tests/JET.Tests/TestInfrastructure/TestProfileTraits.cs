namespace JET.Tests.TestInfrastructure;

internal static class TestProfileTraits
{
    internal const string Key = "TestProfile";
    internal const string CapabilityKey = "Capability";

    internal static readonly IReadOnlyCollection<KeyValuePair<string, string>> Provider =
        [new(Key, "Provider"), new(CapabilityKey, "SqlServer")];

    internal static readonly IReadOnlyCollection<KeyValuePair<string, string>> LocalDbProvider =
        [new(Key, "Provider"), new(CapabilityKey, "LocalDbExpress")];

    internal static readonly IReadOnlyCollection<KeyValuePair<string, string>> Scale =
        [new(Key, "Scale")];

    internal static readonly IReadOnlyCollection<KeyValuePair<string, string>> PrivateCase =
        [new(Key, "PrivateCase")];

    internal static readonly IReadOnlyCollection<KeyValuePair<string, string>> FileSystemLinks =
        [new(CapabilityKey, "FileSystemLinks")];

}
