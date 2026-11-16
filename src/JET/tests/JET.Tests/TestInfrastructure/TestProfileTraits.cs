namespace JET.Tests.TestInfrastructure;

internal static partial class TestProfileTraits
{
    internal const string Key = "TestProfile";

    internal static readonly IReadOnlyCollection<KeyValuePair<string, string>> Scale =
        [new(Key, "Scale")];
}
