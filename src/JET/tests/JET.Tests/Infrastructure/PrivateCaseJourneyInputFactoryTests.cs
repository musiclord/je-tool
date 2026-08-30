using System.Text.Json;
using ClosedXML.Excel;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class PrivateCaseJourneyInputFactoryTests
{
    [Fact]
    public void Create_CompleteManifest_CopiesPrivateInputsAndCreatesCalendarWorkbooks()
    {
        using var fixture = new SyntheticFixture();
        using var workspace = PrivateCaseInputWorkspace.Create(fixture.OwnedRoot);

        var prepared = PrivateCaseJourneyInputFactory.Create(
            fixture.Manifest,
            fixture.SourceRoot,
            workspace);

        Assert.Equal("local-case-01", prepared.Input.CaseAlias);
        Assert.Equal("SYNTHETIC-PRIVATE-CASE", prepared.Input.ProjectCode);
        Assert.Equal("Synthetic private case entity", prepared.Input.EntityName);
        Assert.Equal("synthetic-private-operator", prepared.Input.OperatorId);
        Assert.Equal("2025-01-01", prepared.Input.PeriodStart);
        Assert.Equal("2025-12-31", prepared.Input.PeriodEnd);
        Assert.Equal("2025-12-31", prepared.Input.LastPeriodStart);
        Assert.Equal("dual", prepared.Input.GlAmountMode);
        Assert.Equal("direct", prepared.Input.TbChangeMode);
        Assert.Equal(3, prepared.PrivateSourceCount);
        Assert.Equal(2, prepared.GeneratedReferenceCount);
        Assert.Equal("gl-001.xlsx", Assert.Single(prepared.Input.GlSources).FileName);
        Assert.Equal("tb-001.xlsx", Assert.Single(prepared.Input.TbSources).FileName);
        Assert.Equal("account-mapping-001.xlsx", prepared.Input.AccountMappingFile.FileName);
        Assert.Null(prepared.Input.AuthorizedPreparerFile);
        Assert.Equal(JsonValueKind.Array, prepared.Input.FilterScenarios.ValueKind);
        Assert.Equal(1, prepared.Input.FilterScenarios.GetArrayLength());

        AssertCalendarWorkbook(
            prepared.Input.HolidayFile,
            "Date_of_Holiday",
            expectedDataRows: 2);
        AssertCalendarWorkbook(
            prepared.Input.MakeupDayFile,
            "Date_of_MakeUpday",
            expectedDataRows: 1);
        Assert.Equal(fixture.GlBytes, File.ReadAllBytes(prepared.Input.GlSources[0].FilePath));
        Assert.Equal(fixture.TbBytes, File.ReadAllBytes(prepared.Input.TbSources[0].FilePath));
        Assert.Equal(fixture.GlBytes, File.ReadAllBytes(fixture.GlPath));
        Assert.Equal(fixture.TbBytes, File.ReadAllBytes(fixture.TbPath));

        var serialized = JsonSerializer.Serialize(prepared);
        using var serializedDocument = JsonDocument.Parse(serialized);
        Assert.Equal(2, serializedDocument.RootElement.EnumerateObject().Count());
        Assert.DoesNotContain(fixture.SourceRoot, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(prepared.Input.EntityName, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(prepared.Input.FilterScenarios.GetRawText(), serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_AuthorizedPreparerFileMode_CopiesTheExplicitFile()
    {
        using var fixture = new SyntheticFixture(includeAuthorizedPreparer: true);
        using var workspace = PrivateCaseInputWorkspace.Create(fixture.OwnedRoot);

        var prepared = PrivateCaseJourneyInputFactory.Create(
            fixture.Manifest,
            fixture.SourceRoot,
            workspace);

        Assert.Equal(4, prepared.PrivateSourceCount);
        Assert.Equal(
            "authorized-preparer-001.xlsx",
            Assert.IsType<PrivateCaseJourneyReferenceFile>(prepared.Input.AuthorizedPreparerFile).FileName);
    }

    [Fact]
    public void Create_MissingPrivateSource_FailsWithoutEchoingThePath()
    {
        using var fixture = new SyntheticFixture();
        File.Delete(fixture.GlPath);
        using var workspace = PrivateCaseInputWorkspace.Create(fixture.OwnedRoot);

        var error = Assert.Throws<PrivateCaseJourneyInputException>(() =>
            PrivateCaseJourneyInputFactory.Create(
                fixture.Manifest,
                fixture.SourceRoot,
                workspace));

        Assert.Equal(PrivateCaseJourneyInputFailure.PrivateFileUnavailable, error.Failure);
        Assert.DoesNotContain(fixture.SourceRoot, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gl.xlsx", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertCalendarWorkbook(
        PrivateCaseJourneyReferenceFile reference,
        string expectedHeader,
        int expectedDataRows)
    {
        Assert.StartsWith("private case journey reference", reference.ToString(), StringComparison.Ordinal);
        using var workbook = new XLWorkbook(reference.FilePath);
        var sheet = workbook.Worksheet(1);
        Assert.Equal(expectedHeader, sheet.Cell(2, 1).GetString());
        Assert.Equal(expectedDataRows + 2, sheet.LastRowUsed()!.RowNumber());
    }

    private sealed class SyntheticFixture : IDisposable
    {
        internal SyntheticFixture(bool includeAuthorizedPreparer = false)
        {
            Root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"jet-private-journey-input-{Guid.NewGuid():N}");
            SourceRoot = System.IO.Path.Combine(Root, "source");
            OwnedRoot = System.IO.Path.Combine(Root, "owned");
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(OwnedRoot);

            GlBytes = [1, 2, 3, 4];
            TbBytes = [5, 6, 7];
            GlPath = Write("gl.xlsx", GlBytes);
            TbPath = Write("tb.xlsx", TbBytes);
            _ = Write("account-mapping.xlsx", [8, 9]);
            if (includeAuthorizedPreparer)
            {
                _ = Write("authorized-preparer.xlsx", [10]);
            }

            var referenceData = new PrivateCaseReferenceData(
                "account-mapping.xlsx",
                includeAuthorizedPreparer
                    ? PrivateCaseAuthorizedPreparerMode.File
                    : PrivateCaseAuthorizedPreparerMode.NotProvided,
                includeAuthorizedPreparer ? "authorized-preparer.xlsx" : null,
                [new DateOnly(2025, 1, 1), new DateOnly(2025, 2, 28)],
                [new DateOnly(2025, 2, 8)]);
            var scenarios = new[]
            {
                JsonSerializer.SerializeToElement(new
                {
                    name = "synthetic scenario",
                    rationale = "synthetic journey input",
                    groups = Array.Empty<object>(),
                }),
            };
            Manifest = new PrivateCaseManifest(
                schemaVersion: 1,
                caseAlias: "local-case-01",
                new PrivateCaseProjectSettings(
                    "SYNTHETIC-PRIVATE-CASE",
                    "Synthetic private case entity",
                    "synthetic-private-operator",
                    new DateOnly(2025, 1, 1),
                    new DateOnly(2025, 12, 31),
                    new DateOnly(2025, 12, 31),
                    sampleSeed: 1_600_001),
                new PrivateCaseGlSettings(
                    [new PrivateCaseSource("gl.xlsx", "GL", firstRowIsFieldNames: true)],
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [GlMappingKeys.DocNum] = "doc",
                    },
                    GlAmountMode.DualAmount),
                new PrivateCaseTbSettings(
                    [new PrivateCaseSource("tb.xlsx", "TB", firstRowIsFieldNames: true)],
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [TbMappingKeys.AccNum] = "account",
                    },
                    TbChangeMode.DirectChange),
                referenceData,
                scenarios,
                new PrivateCaseLegacyEvidence(
                    "gl-log.txt",
                    "tb-log.txt",
                    new Dictionary<LegacyReportKind, string>(),
                    [new PrivateCaseScenarioCount(1, 0, 0)],
                    []));
        }

        internal string Root { get; }

        internal string SourceRoot { get; }

        internal string OwnedRoot { get; }

        internal string GlPath { get; }

        internal string TbPath { get; }

        internal byte[] GlBytes { get; }

        internal byte[] TbBytes { get; }

        internal PrivateCaseManifest Manifest { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private string Write(string relativePath, byte[] bytes)
        {
            var path = System.IO.Path.Combine(SourceRoot, relativePath);
            File.WriteAllBytes(path, bytes);
            return path;
        }
    }
}
