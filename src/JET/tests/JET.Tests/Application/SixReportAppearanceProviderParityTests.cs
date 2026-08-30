using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// Value-blind appearance parity for all six formal report artifacts. The fixture is
/// deterministic and synthetic. Workbook metadata lineage is first matched to this
/// fixture's current project truth, then its provider-local identities are normalized
/// before semantic parity comparison; no customer workbook is read.
/// </summary>
public sealed class SixReportAppearanceProviderParityTests
{
    private const long PinnedSampleSeed = 8_082_025;

    private static readonly string[] ExpectedKinds =
    [
        "accountMapping",
        "criteriaSelectionReport",
        "infReport",
        "prescreenReport",
        "validationReport",
        "workingPaper"
    ];

    [Fact]
    public async Task SixReports_SqliteAndDuckDb_HaveIdenticalValueBlindAppearanceSnapshots()
    {
        using var sqliteHost = new HandlerTestHost();
        var sqlite = await ExportSixReportsAsync(sqliteHost, "sqlite");

        using var duckDbHost = new HandlerTestHost();
        var duckDb = await ExportSixReportsAsync(duckDbHost, "duckdb");

        AssertEquivalent(sqlite, duckDb);
    }

    [SqlServerFact]
    public async Task SixReports_SqliteAndSqlServer_HaveIdenticalValueBlindAppearanceSnapshots()
    {
        var connectionString = await TempSqlServerProject.ProbeConnectionStringAsync()
            ?? throw new InvalidOperationException(
                "SqlServerFact 已判定可用，但執行期無法取得 SQL Server 連線。");

        using var sqliteHost = new HandlerTestHost();
        var sqlite = await ExportSixReportsAsync(sqliteHost, "sqlite");

        HandlerTestHost? sqlServerHost = null;
        try
        {
            sqlServerHost = new HandlerTestHost(sqlServerConnectionString: connectionString);
            var sqlServer = await ExportSixReportsAsync(sqlServerHost, "sqlServer");

            AssertEquivalent(sqlite, sqlServer);
        }
        finally
        {
            var projectIds = sqlServerHost is null
                ? Array.Empty<string>()
                : ProjectIds(sqlServerHost.ProjectsRoot);
            try
            {
                sqlServerHost?.Dispose();
            }
            finally
            {
                foreach (var projectId in projectIds)
                {
                    await TempSqlServerProject.DropDatabaseAsync(connectionString, projectId);
                }
            }
        }
    }

    private static async Task<ProviderSnapshot> ExportSixReportsAsync(
        HandlerTestHost host,
        string databaseProvider)
    {
        var context = await DemoProjectPipeline.SetupAsync(
            host,
            runValidation: false,
            databaseProvider: databaseProvider);
        await PinSampleSeedAsync(host, context.ProjectId);
        var validation = await host.DispatchAsync("validate.run");
        var validationRunId = validation
            .GetProperty("resultRef")
            .GetProperty("runId")
            .GetString()!;
        _ = await host.DispatchAsync(
            "export.validationArtifacts",
            JsonSerializer.Serialize(new { runId = validationRunId }));

        var prescreen = await host.DispatchAsync("prescreen.run");
        var prescreenRunId = prescreen
            .GetProperty("resultRef")
            .GetProperty("runId")
            .GetString()!;
        _ = await host.DispatchAsync(
            "export.prescreenReport",
            JsonSerializer.Serialize(new { runId = prescreenRunId }));

        var committed = await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new
            {
                scenarios = new[]
                {
                    new
                    {
                        name = "樣式 provider 等價情境",
                        rationale = "以合成摘要條件產生六份正式報表",
                        groups = new[]
                        {
                            new
                            {
                                join = "AND",
                                rules = new[]
                                {
                                    new
                                    {
                                        join = "AND",
                                        type = "customKeywords",
                                        keywords = "調整"
                                    }
                                }
                            }
                        }
                    }
                }
            }));
        var revision = committed
            .GetProperty("resultRef")
            .GetProperty("revision")
            .GetString()!;

        _ = await host.DispatchAsync(
            "export.criteriaSelectionReport",
            JsonSerializer.Serialize(new
            {
                validationRunId,
                prescreenRunId,
                revision
            }));
        _ = await host.DispatchAsync(
            "export.workpaperStream",
            JsonSerializer.Serialize(new
            {
                validationRunId,
                prescreenRunId,
                scenarioRevision = revision,
                scenarioPositions = new[] { 1 }
            }));

        var loaded = await host.DispatchAsync(
            "project.load",
            JsonSerializer.Serialize(new { projectId = context.ProjectId }));
        var artifacts = loaded
            .GetProperty("reportArtifacts")
            .EnumerateArray()
            .ToArray();
        var expectedMetadata = ExpectedWorkbookMetadata.FromProjectLoad(loaded);
        var kinds = artifacts
            .Select(artifact => artifact.GetProperty("kind").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(ExpectedKinds, kinds);

        var projectDirectory = Path.Combine(host.ProjectsRoot, context.ProjectId);
        var snapshots = new Dictionary<string, WorkbookAppearanceSnapshot>(StringComparer.Ordinal);
        foreach (var artifact in artifacts)
        {
            var kind = artifact.GetProperty("kind").GetString()!;
            var fileName = artifact.GetProperty("fileName").GetString()!;
            if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Report artifact '{kind}' did not use a project-local file name.");
            }

            var path = Path.Combine(projectDirectory, fileName);
            if (!File.Exists(path))
            {
                throw new InvalidDataException($"Report artifact '{kind}' was not published.");
            }

            snapshots.Add(kind, WorkbookAppearanceSnapshot.Capture(path, expectedMetadata));
        }

        return new ProviderSnapshot(databaseProvider, snapshots);
    }

    private static void AssertEquivalent(
        ProviderSnapshot expected,
        ProviderSnapshot actual)
    {
        Assert.Equal(
            expected.Workbooks.Keys.Order(StringComparer.Ordinal),
            actual.Workbooks.Keys.Order(StringComparer.Ordinal));
        foreach (var kind in expected.Workbooks.Keys.Order(StringComparer.Ordinal))
        {
            var expectedWorkbook = expected.Workbooks[kind];
            var actualWorkbook = actual.Workbooks[kind];
            Assert.Equal(expectedWorkbook.CanonicalMetadata, actualWorkbook.CanonicalMetadata);
            var contentDifference = DescribeContentDifference(
                expectedWorkbook.Content,
                actualWorkbook.Content);
            Assert.True(
                contentDifference is null,
                $"{kind} content fingerprint differs at {contentDifference} before appearance comparison.");
            var appearanceDifference = expectedWorkbook.Appearance
                .DescribeFirstDifference(actualWorkbook.Appearance);
            Assert.True(
                appearanceDifference is null,
                $"{kind} appearance differs: {appearanceDifference}");

            var physicalDifference = expectedWorkbook.PhysicalLayout
                .DescribeFirstDifference(actualWorkbook.PhysicalLayout);
            Assert.True(
                physicalDifference is null,
                $"{kind} physical layout differs: {physicalDifference}");
        }
    }

    private static async Task PinSampleSeedAsync(HandlerTestHost host, string projectId)
    {
        var store = new JsonFileProjectStore(new JetProjectFolder(host.ProjectsRoot));
        var document = await store.FindAsync(projectId, CancellationToken.None)
            ?? throw new InvalidDataException("Provider snapshot project document was not published.");
        await store.SaveAsync(
            document with
            {
                SampleSeed = PinnedSampleSeed,
                SampleSeedVersion = JetAuditProgram.CurrentInfSamplingAlgorithmVersion
            },
            CancellationToken.None);
    }

    private static string? DescribeContentDifference(byte[] expected, byte[] actual)
    {
        if (expected.SequenceEqual(actual))
        {
            return null;
        }

        var expectedLines = System.Text.Encoding.UTF8.GetString(expected).Split('\n');
        var actualLines = System.Text.Encoding.UTF8.GetString(actual).Split('\n');
        var count = Math.Max(expectedLines.Length, actualLines.Length);
        for (var index = 0; index < count; index++)
        {
            var expectedLine = index < expectedLines.Length ? expectedLines[index] : null;
            var actualLine = index < actualLines.Length ? actualLines[index] : null;
            if (string.Equals(expectedLine, actualLine, StringComparison.Ordinal))
            {
                continue;
            }

            return $"line {index + 1} ({DescribeContentLine(expectedLine)} vs {DescribeContentLine(actualLine)})";
        }
        return "end-of-file";
    }

    private static string DescribeContentLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return "end-of-file";
        }

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var kind = root.GetProperty("kind").GetString();
        var sheet = root.TryGetProperty("sheet", out var sheetElement)
            ? sheetElement.GetString()
            : null;
        var row = root.TryGetProperty("row", out var rowElement)
            ? rowElement.GetUInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
        return string.Join(
            "/",
            new[] { kind, sheet, row }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string[] ProjectIds(string projectsRoot) =>
        Directory.Exists(projectsRoot)
            ? Directory.GetDirectories(projectsRoot)
                .Select(Path.GetFileName)
                .Where(projectId => !string.IsNullOrWhiteSpace(projectId))
                .Select(projectId => projectId!)
                .ToArray()
            : [];

    private sealed record ProviderSnapshot(
        string Provider,
        IReadOnlyDictionary<string, WorkbookAppearanceSnapshot> Workbooks);

    private sealed record MappingLineage(string SourceBatchId, string CommittedUtc)
    {
        internal static MappingLineage FromProjectLoad(JsonElement mapping, string kind)
        {
            var value = mapping.GetProperty(kind);
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"Provider snapshot project.load did not return current {kind} mapping metadata.");
            }

            return new MappingLineage(
                value.GetProperty("sourceBatchId").GetString()!,
                value.GetProperty("committedUtc").GetDateTimeOffset().ToString(
                    "O",
                    System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private sealed record ExpectedWorkbookMetadata(
        string PeriodStart,
        string PeriodEnd,
        int TaxonomyRevision,
        MappingLineage Gl,
        MappingLineage? Tb)
    {
        internal static ExpectedWorkbookMetadata FromProjectLoad(JsonElement loaded)
        {
            var project = loaded.GetProperty("project");
            var mapping = loaded.GetProperty("mapping");
            var tb = mapping.GetProperty("tb");
            return new ExpectedWorkbookMetadata(
                project.GetProperty("periodStart").GetString()!,
                project.GetProperty("periodEnd").GetString()!,
                loaded.GetProperty("taxonomy").GetProperty("revision").GetInt32(),
                MappingLineage.FromProjectLoad(mapping, "gl"),
                tb.ValueKind == JsonValueKind.Null
                    ? null
                    : MappingLineage.FromProjectLoad(mapping, "tb"));
        }
    }

    private sealed record WorkbookAppearanceSnapshot(
        byte[] Content,
        string CanonicalMetadata,
        SpreadsheetAppearanceFingerprint Appearance,
        SpreadsheetPhysicalLayoutFingerprint PhysicalLayout)
    {
        internal static WorkbookAppearanceSnapshot Capture(
            string path,
            ExpectedWorkbookMetadata expectedMetadata)
        {
            using var temporaryRoot = new TempProjectRoot();
            Directory.CreateDirectory(temporaryRoot.Path);
            var fingerprintPath = Path.Combine(
                temporaryRoot.Path,
                "provider-content.content.ndjson");
            SpreadsheetContentFingerprint.Capture(
                path,
                fingerprintPath,
                "provider-content",
                _ => SpreadsheetContentHeaderRule.None);
            return new WorkbookAppearanceSnapshot(
                WithoutMetadataPayloadRows(File.ReadAllBytes(fingerprintPath)),
                ReadCanonicalMetadata(path, expectedMetadata),
                SpreadsheetAppearanceFingerprint.Capture(path),
                SpreadsheetPhysicalLayoutFingerprint.Capture(path));
        }

        private static string ReadCanonicalMetadata(
            string path,
            ExpectedWorkbookMetadata expected)
        {
            using var document = SpreadsheetDocument.Open(path, false);
            var workbookPart = document.WorkbookPart
                ?? throw new InvalidDataException("Formal report workbook has no workbook part.");
            var sheet = workbookPart.Workbook.Sheets!
                .Elements<Sheet>()
                .Single(candidate => string.Equals(
                    candidate.Name?.Value,
                    ReportWorkbookMetadataFormat.WorksheetName,
                    StringComparison.Ordinal));
            if (sheet.State?.Value != SheetStateValues.VeryHidden)
            {
                throw new InvalidDataException("Formal report workbook metadata sheet is not VeryHidden.");
            }
            var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);
            var cells = worksheetPart.Worksheet.Descendants<Cell>()
                .ToDictionary(cell => cell.CellReference!.Value!, CellText, StringComparer.Ordinal);

            var chunkCount = int.Parse(cells["C1"], System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(
                    cells["A1"],
                    ReportWorkbookMetadataFormat.Marker,
                    StringComparison.Ordinal)
                || cells["B1"] != ReportWorkbookMetadataFormat.CurrentVersion.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
                || chunkCount < 1)
            {
                throw new InvalidDataException("Formal report workbook metadata header is malformed.");
            }

            var payload = string.Concat(Enumerable.Range(0, chunkCount).Select(index =>
            {
                var row = index + 2;
                if (cells[$"A{row}"] != (index + 1).ToString(
                        System.Globalization.CultureInfo.InvariantCulture))
                {
                    throw new InvalidDataException("Formal report workbook metadata chunk ordinal is malformed.");
                }
                return cells[$"B{row}"];
            }));

            var root = JsonNode.Parse(payload)?.AsObject()
                ?? throw new InvalidDataException("Formal report workbook metadata payload is not an object.");
            RequireNumber(root, "formatVersion", ReportWorkbookMetadataFormat.CurrentVersion);
            RequireNumber(root, "taxonomyRevision", expected.TaxonomyRevision);
            var population = RequireObject(root, "populationPolicy");
            RequireString(population, "periodStart", expected.PeriodStart);
            RequireString(population, "periodEnd", expected.PeriodEnd);
            var mapping = RequireObject(root, "mapping");
            RequireNumber(mapping, "formatVersion", MappingMetadataFormat.CurrentVersion);
            NormalizeLineage(RequireObject(mapping, "gl"), expected.Gl, "gl");
            if (expected.Tb is null)
            {
                if (mapping["tb"] is not null)
                {
                    throw new InvalidDataException("Formal report workbook metadata contains unexpected TB mapping.");
                }
            }
            else
            {
                NormalizeLineage(RequireObject(mapping, "tb"), expected.Tb, "tb");
            }

            return root.ToJsonString(new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = false
            });
        }

        private static string CellText(Cell cell) =>
            cell.InlineString?.InnerText ?? cell.CellValue?.InnerText ?? string.Empty;

        private static void NormalizeLineage(
            JsonObject mapping,
            MappingLineage expected,
            string kind)
        {
            RequireString(mapping, "sourceBatchId", expected.SourceBatchId);
            RequireString(mapping, "committedUtc", expected.CommittedUtc);
            mapping["sourceBatchId"] = $"$current-{kind}-batch";
            mapping["committedUtc"] = $"$current-{kind}-commit";
        }

        private static JsonObject RequireObject(JsonObject parent, string propertyName) =>
            parent[propertyName] as JsonObject
            ?? throw new InvalidDataException(
                $"Formal report workbook metadata '{propertyName}' is not an object.");

        private static void RequireString(
            JsonObject parent,
            string propertyName,
            string expected)
        {
            if (!string.Equals(
                    parent[propertyName]?.GetValue<string>(),
                    expected,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Formal report workbook metadata '{propertyName}' does not match current project truth.");
            }
        }

        private static void RequireNumber(
            JsonObject parent,
            string propertyName,
            int expected)
        {
            if (parent[propertyName]?.GetValue<int>() != expected)
            {
                throw new InvalidDataException(
                    $"Formal report workbook metadata '{propertyName}' does not match current project truth.");
            }
        }

        private static byte[] WithoutMetadataPayloadRows(byte[] content)
        {
            var source = Encoding.UTF8.GetString(content).Split('\n');
            var result = new StringBuilder(content.Length);
            foreach (var line in source)
            {
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.GetProperty("kind").GetString() == "row"
                    && root.TryGetProperty("sheet", out var sheet)
                    && string.Equals(
                        sheet.GetString(),
                        ReportWorkbookMetadataFormat.WorksheetName,
                        StringComparison.Ordinal)
                    && root.TryGetProperty("row", out var row)
                    && row.GetUInt32() >= 2U)
                {
                    continue;
                }

                result.Append(line).Append('\n');
            }
            return Encoding.UTF8.GetBytes(result.ToString());
        }
    }
}
