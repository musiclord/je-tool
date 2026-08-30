using System.Text;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class SpreadsheetAppearanceSnapshotCompressionTests
{
    private const string CompressionModeEnvironmentVariable =
        "JET_STAGE10_APPEARANCE_COMPRESSION_MODE";

    [Fact]
    public void LegacyAndCompressedDocuments_GiveTheSamePassAndFailInBothDirections()
    {
        var expectedEntries = Ordered(
            Entry("AccountMapping!A1", "font.bold", "true"),
            Entry("AccountMapping!A2", "font.bold", "true"),
            Entry("AccountMapping!cellStream", "cellCount", "2"),
            Entry("AccountMapping!cellStream", "sha256", new string('A', 64)));
        var changedEntries = Ordered(
            Entry("AccountMapping!A1", "font.bold", "true"),
            Entry("AccountMapping!A2", "font.bold", "false"),
            Entry("AccountMapping!cellStream", "cellCount", "2"),
            Entry("AccountMapping!cellStream", "sha256", new string('B', 64)));

        var expectedLegacy = Legacy(expectedEntries);
        var changedLegacy = Legacy(changedEntries);
        var expectedCompressed = SpreadsheetAppearanceSnapshotSchemaGuard.CompressJson(
            expectedLegacy,
            LegacyReportKind.AccountMapping);
        var changedCompressed = SpreadsheetAppearanceSnapshotSchemaGuard.CompressJson(
            changedLegacy,
            LegacyReportKind.AccountMapping);

        var oldExpected = SpreadsheetAppearanceFingerprint.LoadJson(
            expectedLegacy,
            LegacyReportKind.AccountMapping);
        var newExpected = SpreadsheetAppearanceFingerprint.LoadJson(
            expectedCompressed,
            LegacyReportKind.AccountMapping);
        var oldChanged = SpreadsheetAppearanceFingerprint.LoadJson(
            changedLegacy,
            LegacyReportKind.AccountMapping);
        var newChanged = SpreadsheetAppearanceFingerprint.LoadJson(
            changedCompressed,
            LegacyReportKind.AccountMapping);

        Assert.True(IsMatch(oldExpected, newExpected));
        Assert.True(IsMatch(newExpected, oldExpected));
        Assert.True(IsMatch(oldChanged, newChanged));
        Assert.True(IsMatch(newChanged, oldChanged));
        Assert.False(IsMatch(oldExpected, oldChanged));
        Assert.False(IsMatch(oldExpected, newChanged));
        Assert.False(IsMatch(newExpected, oldChanged));
        Assert.False(IsMatch(newExpected, newChanged));
        Assert.False(IsMatch(oldChanged, oldExpected));
        Assert.False(IsMatch(oldChanged, newExpected));
        Assert.False(IsMatch(newChanged, oldExpected));
        Assert.False(IsMatch(newChanged, newExpected));

        var oldDifferences = oldExpected.DescribeDifferences(oldChanged);
        Assert.Equal(oldDifferences, oldExpected.DescribeDifferences(newChanged));
        Assert.Equal(oldDifferences, newExpected.DescribeDifferences(oldChanged));
        Assert.Equal(oldDifferences, newExpected.DescribeDifferences(newChanged));
    }

    [Fact]
    public void CellStreamCountAndSha256_AreIdenticalAcrossLegacyAndCompressedDocuments()
    {
        var entries = Ordered(
            Entry("AccountMapping!A1", "font.name", "Calibri"),
            Entry("AccountMapping!cellStream", "cellCount", "250"),
            Entry("AccountMapping!cellStream", "sha256", new string('C', 64)),
            Entry("List!cellStream", "cellCount", "0"),
            Entry("List!cellStream", "sha256", new string('D', 64)));
        var legacyBytes = Legacy(entries);
        var compressedBytes = SpreadsheetAppearanceSnapshotSchemaGuard.CompressJson(
            legacyBytes,
            LegacyReportKind.AccountMapping);

        var oldFingerprint = SpreadsheetAppearanceFingerprint.LoadJson(
            legacyBytes,
            LegacyReportKind.AccountMapping);
        var newFingerprint = SpreadsheetAppearanceFingerprint.LoadJson(
            compressedBytes,
            LegacyReportKind.AccountMapping);
        var oldProofs = oldFingerprint.Entries
            .Where(entry => entry.Location.EndsWith("!cellStream", StringComparison.Ordinal))
            .ToArray();
        var newProofs = newFingerprint.Entries
            .Where(entry => entry.Location.EndsWith("!cellStream", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(oldProofs, newProofs);
        Assert.True(IsMatch(oldFingerprint, newFingerprint));
        Assert.True(IsMatch(newFingerprint, oldFingerprint));
    }

    [Fact]
    public void RangeEncoding_IsLosslessAndDoesNotBridgeHoles()
    {
        var entries = Ordered(
            Entry("AccountMapping!A1", "font.bold", "true"),
            Entry("AccountMapping!A2", "font.bold", "true"),
            Entry("AccountMapping!A4", "font.bold", "true"),
            Entry("AccountMapping!B1", "font.bold", "true"),
            Entry("AccountMapping!B2", "font.bold", "true"));
        var compressed = SpreadsheetAppearanceSnapshotSchemaGuard.CompressJson(
            Legacy(entries),
            LegacyReportKind.AccountMapping);
        var json = Encoding.UTF8.GetString(compressed);
        var roundTripped = SpreadsheetAppearanceSnapshotSchemaGuard.ReadEntries(
            compressed,
            LegacyReportKind.AccountMapping);

        Assert.Equal(entries, roundTripped);
        Assert.Contains("AccountMapping!A1:B2", json, StringComparison.Ordinal);
        Assert.Contains("AccountMapping!A4", json, StringComparison.Ordinal);
        Assert.DoesNotContain("AccountMapping!A1:B4", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryFixtures_AreCanonicalCompressedDocumentsWithLegacyEquivalentEntries()
    {
        SpreadsheetAppearanceSnapshotSchemaGuard.ValidateRepositoryInventory(
            TestRepositoryPaths.RepositoryRoot);
        var fixtureDirectory = Path.Combine(
            TestRepositoryPaths.RepositoryRoot,
            SpreadsheetAppearanceSnapshotSchemaGuard.FixtureRelativeDirectory.Replace(
                '/',
                Path.DirectorySeparatorChar));

        foreach (var identity in SpreadsheetAppearanceSnapshotSchemaGuard.ExpectedSnapshots)
        {
            var path = Path.Combine(fixtureDirectory, identity.FileName);
            var compressedBytes = File.ReadAllBytes(path);
            Assert.Equal(
                SpreadsheetAppearanceSnapshotSchemaGuard.CompressedSchemaVersion,
                SpreadsheetAppearanceSnapshotSchemaGuard.ReadSchemaVersion(compressedBytes));

            var entries = SpreadsheetAppearanceSnapshotSchemaGuard.ReadEntries(
                compressedBytes,
                identity.Report);
            var legacyBytes = Legacy(entries);
            var recompressed = SpreadsheetAppearanceSnapshotSchemaGuard.CompressJson(
                legacyBytes,
                identity.Report);
            Assert.Equal(compressedBytes, recompressed);

            var oldFingerprint = SpreadsheetAppearanceFingerprint.LoadJson(
                legacyBytes,
                identity.Report);
            var newFingerprint = SpreadsheetAppearanceFingerprint.LoadJson(
                compressedBytes,
                identity.Report);
            Assert.Equal(oldFingerprint.Entries, newFingerprint.Entries);
            Assert.True(IsMatch(oldFingerprint, newFingerprint));
            Assert.True(IsMatch(newFingerprint, oldFingerprint));
        }
    }

    [Fact]
    public void CompressRepositoryFixtures_UsesExplicitClosedMode()
    {
        var mode = Environment.GetEnvironmentVariable(CompressionModeEnvironmentVariable);
        Assert.True(
            string.IsNullOrWhiteSpace(mode)
            || string.Equals(mode, "compress", StringComparison.Ordinal),
            $"{CompressionModeEnvironmentVariable} must be unset or compress.");
        if (string.IsNullOrWhiteSpace(mode))
        {
            return;
        }

        var fixtureDirectory = Path.Combine(
            TestRepositoryPaths.RepositoryRoot,
            SpreadsheetAppearanceSnapshotSchemaGuard.FixtureRelativeDirectory.Replace(
                '/',
                Path.DirectorySeparatorChar));
        var converted = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var identity in SpreadsheetAppearanceSnapshotSchemaGuard.ExpectedSnapshots)
        {
            var path = Path.Combine(fixtureDirectory, identity.FileName);
            var before = NormalizeCheckoutLineEndings(File.ReadAllBytes(path));
            var beforeEntries = SpreadsheetAppearanceSnapshotSchemaGuard.ReadEntries(
                before,
                identity.Report);
            var after = SpreadsheetAppearanceSnapshotSchemaGuard.CompressJson(
                before,
                identity.Report);
            var afterEntries = SpreadsheetAppearanceSnapshotSchemaGuard.ReadEntries(
                after,
                identity.Report);
            Assert.Equal(beforeEntries, afterEntries);
            converted.Add(identity.FileName, after);
        }

        foreach (var (fileName, bytes) in converted)
        {
            File.WriteAllBytes(Path.Combine(fixtureDirectory, fileName), bytes);
        }
        SpreadsheetAppearanceSnapshotSchemaGuard.ValidateRepositoryInventory(
            TestRepositoryPaths.RepositoryRoot);
    }

    private static byte[] NormalizeCheckoutLineEndings(byte[] bytes)
    {
        if (bytes.Length >= 2
            && bytes[^2] == (byte)'\r'
            && bytes[^1] == (byte)'\n')
        {
            var canonical = new byte[bytes.Length - 1];
            bytes.AsSpan(0, bytes.Length - 2).CopyTo(canonical);
            canonical[^1] = (byte)'\n';
            return canonical;
        }
        return bytes;
    }

    private static bool IsMatch(
        SpreadsheetAppearanceFingerprint expected,
        SpreadsheetAppearanceFingerprint actual) =>
        expected.DescribeFirstDifference(actual) is null;

    private static byte[] Legacy(IReadOnlyList<AppearanceEntry> entries) =>
        Encoding.UTF8.GetBytes(SpreadsheetAppearanceFingerprint.SerializeEntries(entries));

    private static AppearanceEntry[] Ordered(params AppearanceEntry[] entries) =>
        entries
            .OrderBy(entry => entry.Location, StringComparer.Ordinal)
            .ThenBy(entry => entry.Property, StringComparer.Ordinal)
            .ToArray();

    private static AppearanceEntry Entry(string location, string property, string value) =>
        new(location, property, value);
}
