using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JET.Tests.Infrastructure;

internal static partial class LegacyAppearanceRegistry
{
    public const string FixtureRelativePath =
        "tests/JET.Tests/Infrastructure/Fixtures/legacy-appearance-registry.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static LegacyAppearanceRegistryDocument Load()
    {
        var fixturePath = Path.Combine(
            TestRepositoryPaths.SolutionRoot,
            FixtureRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var json = File.ReadAllText(fixturePath, new UTF8Encoding(false, true));
        return JsonSerializer.Deserialize<LegacyAppearanceRegistryDocument>(json, JsonOptions)
            ?? throw new InvalidDataException($"Legacy appearance registry is empty: {fixturePath}");
    }
}

internal sealed class LegacyAppearanceRegistryDocument
{
    public int SchemaVersion { get; init; }

    public string ExtractionMode { get; init; } = string.Empty;

    public List<LegacyAppearanceRegistryEntry> Entries { get; init; } = [];

    public List<LegacyScriptUnspecifiedSheet> ScriptUnspecifiedSheets { get; init; } = [];
}

internal sealed class LegacyAppearanceRegistryEntry
{
    public string Id { get; init; } = string.Empty;

    public string Procedure { get; init; } = string.Empty;

    public string Report { get; init; } = string.Empty;

    public string Worksheet { get; init; } = string.Empty;

    public string RangeExpression { get; init; } = string.Empty;

    public string DynamicRowBasis { get; init; } = string.Empty;

    public string Property { get; init; } = string.Empty;

    public string NormalizedValue { get; init; } = string.Empty;

    public string Condition { get; init; } = string.Empty;

    public int BasLine { get; init; }

    public int IsmLine { get; init; }

    public string SourceText { get; init; } = string.Empty;

    public string? JetOutput { get; init; }

    public string? NoCorrespondenceReason { get; init; }
}

internal sealed class LegacyScriptUnspecifiedSheet
{
    public string Report { get; init; } = string.Empty;

    public string Worksheet { get; init; } = string.Empty;

    public string SourceProcedure { get; init; } = string.Empty;

    public int BasLine { get; init; }

    public int IsmLine { get; init; }

    public string JetOutput { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;
}
