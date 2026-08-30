using System.Text;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class SpreadsheetAppearanceSnapshotSchemaGuardTests
{
    [Fact]
    public void CanonicalClosedAppearanceDocument_IsAccepted()
    {
        var json = Snapshot(
            Entry("AccountMapping!A1", "font.bold", "true"),
            Entry("AccountMapping!pageSetup", "orientation", "landscape"));

        SpreadsheetAppearanceSnapshotSchemaGuard.ValidateJson(
            json,
            LegacyReportKind.AccountMapping);
    }

    [Fact]
    public void FixedLegacyOnlyConditionalSheets_AreAcceptedByTheClosedCatalog()
    {
        SpreadsheetAppearanceSnapshotSchemaGuard.ValidateJson(
            Snapshot(Entry("A2!A1", "font.bold", "true")),
            LegacyReportKind.PrescreenReport);
        SpreadsheetAppearanceSnapshotSchemaGuard.ValidateJson(
            Snapshot(Entry("A4 (2)!A1", "font.bold", "true")),
            LegacyReportKind.PrescreenReport);
        SpreadsheetAppearanceSnapshotSchemaGuard.ValidateJson(
            Snapshot(Entry(
                "step1-3 完整性測試之差異說明!A1",
                "font.bold",
                "true")),
            LegacyReportKind.WorkingPaper);
    }

    [Fact]
    public void LegacyUnspecifiedPrinterDpiZero_IsAcceptedAsAClosedNumericValue()
    {
        SpreadsheetAppearanceSnapshotSchemaGuard.ValidateJson(
            Snapshot(Entry("ValidationReport!pageSetup", "horizontalDpi", "0")),
            LegacyReportKind.ValidationReport);
        SpreadsheetAppearanceSnapshotSchemaGuard.ValidateJson(
            Snapshot(Entry("ValidationReport!pageSetup", "verticalDpi", "0")),
            LegacyReportKind.ValidationReport);
    }

    [Fact]
    public void CellStreamProof_UsesOnlyClosedCountAndSha256Values()
    {
        SpreadsheetAppearanceSnapshotSchemaGuard.ValidateJson(
            Snapshot(
                Entry("AccountMapping!cellStream", "cellCount", "123"),
                Entry(
                    "AccountMapping!cellStream",
                    "sha256",
                    new string('A', 64))),
            LegacyReportKind.AccountMapping);

        AssertRejected(
            Snapshot(Entry("AccountMapping!cellStream", "cellCount", "0123")),
            SpreadsheetAppearanceSnapshotViolation.UnknownValue,
            sentinel: null);
        AssertRejected(
            Snapshot(Entry("AccountMapping!cellStream", "sha256", new string('G', 64))),
            SpreadsheetAppearanceSnapshotViolation.UnknownValue,
            sentinel: null);
    }

    [Fact]
    public void UnknownJsonKey_IsRejectedWithoutEchoingTheKeyOrValue()
    {
        const string sentinel = "SYNTHETIC-UNKNOWN-KEY-SENTINEL";
        var valid = Encoding.UTF8.GetString(Snapshot());
        var invalid = Encoding.UTF8.GetBytes(valid.Replace(
            "  \"entries\": [",
            $"  \"{sentinel}\": \"{sentinel}\",\n  \"entries\": [",
            StringComparison.Ordinal));

        AssertRejected(
            invalid,
            SpreadsheetAppearanceSnapshotViolation.UnknownKey,
            sentinel);
    }

    [Fact]
    public void DuplicateJsonKey_IsRejectedWithoutEchoingTheValue()
    {
        const string sentinel = "SYNTHETIC-DUPLICATE-KEY-SENTINEL";
        var invalid = Encoding.UTF8.GetBytes($$"""
            {
              "schemaVersion": 1,
              "{{sentinel}}": 1,
              "{{sentinel}}": 1,
              "entries": []
            }
            """ + "\n");

        AssertRejected(
            invalid,
            SpreadsheetAppearanceSnapshotViolation.DuplicateKey,
            sentinel);
    }

    [Fact]
    public void UnknownAppearanceProperty_IsRejectedWithoutEchoingIt()
    {
        const string sentinel = "SYNTHETIC-UNKNOWN-PROPERTY-SENTINEL";
        var invalid = Snapshot(Entry("AccountMapping!A1", sentinel, "true"));

        AssertRejected(
            invalid,
            SpreadsheetAppearanceSnapshotViolation.UnknownProperty,
            sentinel);
    }

    [Fact]
    public void UnknownFontName_IsRejectedWithoutEchoingIt()
    {
        const string sentinel = "SYNTHETIC-UNKNOWN-FONT-SENTINEL";
        var invalid = Snapshot(Entry("AccountMapping!A1", "font.name", sentinel));

        AssertRejected(
            invalid,
            SpreadsheetAppearanceSnapshotViolation.UnknownValue,
            sentinel);
    }

    [Fact]
    public void UnknownNumberFormat_IsRejectedWithoutEchoingIt()
    {
        const string sentinel = "SYNTHETIC-UNKNOWN-NUMBER-FORMAT-SENTINEL";
        var invalid = Snapshot(Entry("AccountMapping!A1", "numberFormat.code", sentinel));

        AssertRejected(
            invalid,
            SpreadsheetAppearanceSnapshotViolation.UnknownValue,
            sentinel);
    }

    [Fact]
    public void UnknownWorksheet_IsRejectedWithoutEchoingIt()
    {
        const string sentinel = "SYNTHETIC-UNKNOWN-SHEET-SENTINEL";
        var invalid = Snapshot(Entry($"{sentinel}!A1", "font.bold", "true"));

        AssertRejected(
            invalid,
            SpreadsheetAppearanceSnapshotViolation.UnknownSheet,
            sentinel);
    }

    [Fact]
    public void UnknownStyleValue_IsRejectedWithoutEchoingIt()
    {
        const string sentinel = "SYNTHETIC-UNKNOWN-VALUE-SENTINEL";
        var invalid = Snapshot(Entry("AccountMapping!A1", "font.bold", sentinel));

        AssertRejected(
            invalid,
            SpreadsheetAppearanceSnapshotViolation.UnknownValue,
            sentinel);
    }

    [Fact]
    public void CompressedSchema_RejectsUnknownRootAndNestedKeysWithoutEchoingThem()
    {
        const string rootSentinel = "SYNTHETIC-V2-ROOT-KEY-SENTINEL";
        const string nestedSentinel = "SYNTHETIC-V2-NESTED-KEY-SENTINEL";
        var valid = Encoding.UTF8.GetString(CompressedSnapshot(
            Entry("AccountMapping!A1", "font.bold", "true")));
        var unknownRoot = Encoding.UTF8.GetBytes(valid.Replace(
            "  \"entries\": [",
            $"  \"{rootSentinel}\": [],\n  \"entries\": [",
            StringComparison.Ordinal));
        var unknownNested = Encoding.UTF8.GetBytes(valid.Replace(
            "\"property\": \"font.bold\"",
            $"\"{nestedSentinel}\": \"font.bold\"",
            StringComparison.Ordinal));

        AssertRejected(
            unknownRoot,
            SpreadsheetAppearanceSnapshotViolation.UnknownKey,
            rootSentinel);
        AssertRejected(
            unknownNested,
            SpreadsheetAppearanceSnapshotViolation.UnknownKey,
            nestedSentinel);
    }

    [Fact]
    public void CompressedSchema_RejectsUnknownPropertyValueAndWorksheetWithoutEchoingThem()
    {
        const string propertySentinel = "SYNTHETIC-V2-PROPERTY-SENTINEL";
        const string valueSentinel = "SYNTHETIC-V2-VALUE-SENTINEL";
        const string sheetSentinel = "SYNTHETIC-V2-SHEET-SENTINEL";
        var valid = Encoding.UTF8.GetString(CompressedSnapshot(
            Entry("AccountMapping!A1", "font.bold", "true")));

        AssertRejected(
            Encoding.UTF8.GetBytes(valid.Replace(
                "\"property\": \"font.bold\"",
                $"\"property\": \"{propertySentinel}\"",
                StringComparison.Ordinal)),
            SpreadsheetAppearanceSnapshotViolation.UnknownProperty,
            propertySentinel);
        AssertRejected(
            Encoding.UTF8.GetBytes(valid.Replace(
                "\"value\": \"true\"",
                $"\"value\": \"{valueSentinel}\"",
                StringComparison.Ordinal)),
            SpreadsheetAppearanceSnapshotViolation.UnknownValue,
            valueSentinel);
        AssertRejected(
            Encoding.UTF8.GetBytes(valid.Replace(
                "AccountMapping!A1",
                $"{sheetSentinel}!A1",
                StringComparison.Ordinal)),
            SpreadsheetAppearanceSnapshotViolation.UnknownSheet,
            sheetSentinel);
    }

    [Fact]
    public void CompressedSchema_RejectsOverlappingLocationRanges()
    {
        var valid = Encoding.UTF8.GetString(CompressedSnapshot(
            Entry("AccountMapping!A1", "font.bold", "true"),
            Entry("AccountMapping!A2", "font.bold", "true")));
        var overlapping = Encoding.UTF8.GetBytes(valid.Replace(
            "\"AccountMapping!A1:A2\"",
            "\"AccountMapping!A1:A2\",\n        \"AccountMapping!A2\"",
            StringComparison.Ordinal));

        AssertRejected(
            overlapping,
            SpreadsheetAppearanceSnapshotViolation.DuplicateEntry,
            sentinel: null);
    }

    [Fact]
    public void CompressedSchema_RejectsExpansionBeforeAllocatingTooManyAppearanceEntries()
    {
        var valid = Encoding.UTF8.GetString(CompressedSnapshot(
            Entry("AccountMapping!A1", "font.bold", "true"),
            Entry("AccountMapping!A1", "font.italic", "true")));
        var expansionBomb = Encoding.UTF8.GetBytes(valid.Replace(
            "AccountMapping!A1",
            "AccountMapping!A1:XFD31",
            StringComparison.Ordinal));

        AssertRejected(
            expansionBomb,
            SpreadsheetAppearanceSnapshotViolation.InvalidShape,
            sentinel: null);
    }

    [Fact]
    public void CompressedSchema_RejectsNonCanonicalEquivalentRangeDecomposition()
    {
        var valid = Encoding.UTF8.GetString(CompressedSnapshot(
            Entry("AccountMapping!A1", "font.bold", "true"),
            Entry("AccountMapping!A2", "font.bold", "true"),
            Entry("AccountMapping!B1", "font.bold", "true"),
            Entry("AccountMapping!B2", "font.bold", "true")));
        var decomposed = Encoding.UTF8.GetBytes(valid.Replace(
            "\"AccountMapping!A1:B2\"",
            "\"AccountMapping!A1:B1\",\n        \"AccountMapping!A2:B2\"",
            StringComparison.Ordinal));

        AssertRejected(
            decomposed,
            SpreadsheetAppearanceSnapshotViolation.NonCanonicalJson,
            sentinel: null);
    }

    [Fact]
    public void NonCanonicalEntryOrderAndSerialization_AreRejected()
    {
        var outOfOrder = SnapshotWithoutOrdering(
            Entry("AccountMapping!B1", "font.bold", "true"),
            Entry("AccountMapping!A1", "font.bold", "true"));
        var noFinalLineFeed = Snapshot(Entry("AccountMapping!A1", "font.bold", "true"))[..^1];

        AssertRejected(
            outOfOrder,
            SpreadsheetAppearanceSnapshotViolation.NonCanonicalEntryOrder,
            sentinel: null);
        AssertRejected(
            noFinalLineFeed,
            SpreadsheetAppearanceSnapshotViolation.NonCanonicalJson,
            sentinel: null);
    }

    [Fact]
    public void RepositoryInventory_AlwaysRequiresTheExactTwelveStage9Snapshots()
    {
        using var directory = new TemporarySnapshotRepository();

        AssertInvalidInventory(() =>
            SpreadsheetAppearanceSnapshotSchemaGuard.ValidateRepositoryInventory(directory.Path));

        var fixtureDirectory = System.IO.Path.Combine(
            directory.Path,
            SpreadsheetAppearanceSnapshotSchemaGuard.FixtureRelativeDirectory.Replace(
                '/',
                System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(fixtureDirectory);
        AssertInvalidInventory(() =>
            SpreadsheetAppearanceSnapshotSchemaGuard.ValidateRepositoryInventory(directory.Path));

        var emptySnapshot = CompressedSnapshot();
        var first = SpreadsheetAppearanceSnapshotSchemaGuard.ExpectedSnapshots[0];
        File.WriteAllBytes(System.IO.Path.Combine(fixtureDirectory, first.FileName), emptySnapshot);
        AssertInvalidInventory(() =>
            SpreadsheetAppearanceSnapshotSchemaGuard.ValidateRepositoryInventory(directory.Path));

        foreach (var identity in SpreadsheetAppearanceSnapshotSchemaGuard.ExpectedSnapshots)
        {
            File.WriteAllBytes(
                System.IO.Path.Combine(fixtureDirectory, identity.FileName),
                emptySnapshot);
        }
        SpreadsheetAppearanceSnapshotSchemaGuard.ValidateRepositoryInventory(directory.Path);

        File.WriteAllBytes(
            System.IO.Path.Combine(fixtureDirectory, "unexpected.appearance.json"),
            emptySnapshot);
        AssertInvalidInventory(() =>
            SpreadsheetAppearanceSnapshotSchemaGuard.ValidateRepositoryInventory(directory.Path));
    }

    private static void AssertInvalidInventory(Action action)
    {
        var exception = Assert.Throws<SpreadsheetAppearanceSnapshotSchemaException>(action);
        Assert.Equal(
            SpreadsheetAppearanceSnapshotViolation.InvalidInventory,
            exception.Violation);
    }

    private static void AssertRejected(
        byte[] json,
        SpreadsheetAppearanceSnapshotViolation expected,
        string? sentinel)
    {
        var exception = Assert.Throws<SpreadsheetAppearanceSnapshotSchemaException>(() =>
            SpreadsheetAppearanceSnapshotSchemaGuard.ValidateJson(
                json,
                LegacyReportKind.AccountMapping));

        Assert.Equal(expected, exception.Violation);
        if (sentinel is not null)
        {
            Assert.DoesNotContain(sentinel, exception.Message, StringComparison.Ordinal);
        }
    }

    private static byte[] Snapshot(params AppearanceEntry[] entries) =>
        Encoding.UTF8.GetBytes(SpreadsheetAppearanceFingerprint.SerializeEntries(
            entries
                .OrderBy(entry => entry.Location, StringComparer.Ordinal)
                .ThenBy(entry => entry.Property, StringComparer.Ordinal)
                .ToArray()));

    private static byte[] SnapshotWithoutOrdering(params AppearanceEntry[] entries) =>
        Encoding.UTF8.GetBytes(SpreadsheetAppearanceFingerprint.SerializeEntries(entries));

    private static byte[] CompressedSnapshot(params AppearanceEntry[] entries) =>
        SpreadsheetAppearanceSnapshotSchemaGuard.CompressJson(
            Snapshot(entries),
            LegacyReportKind.AccountMapping);

    private static AppearanceEntry Entry(string location, string property, string value) =>
        new(location, property, value);

    private sealed class TemporarySnapshotRepository : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"jet-appearance-schema-{Guid.NewGuid():N}");

        internal TemporarySnapshotRepository() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            var fullPath = System.IO.Path.GetFullPath(Path);
            var expectedPrefix = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "jet-appearance-schema-");
            if (!fullPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Synthetic snapshot cleanup target was not recognized.");
            }
            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
    }
}
