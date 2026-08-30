using System.Globalization;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class ReportWorkbookMetadataCodecTests
{
    [Fact]
    public void Encode_GlOnlySnapshotKeepsCanonicalPolicyAndExplicitNullTb()
    {
        var gl = GlMapping();
        var fields = gl.GlOptions!.RdeFields;
        var metadata = new ReportWorkbookMetadata(
            "2026-01-01",
            "2026-12-31",
            TaxonomyRevision: 7,
            gl,
            TbMapping: null);

        using var document = JsonDocument.Parse(ReportWorkbookMetadataCodec.Encode(metadata));
        var root = document.RootElement;

        Assert.Equal(
            ["formatVersion", "approvalDateMode", "populationPolicy", "taxonomyRevision", "mapping"],
            root.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.Equal(ApprovalDateModeNames.Mapped, root.GetProperty("approvalDateMode").GetString());
        Assert.Equal("2026-01-01", root.GetProperty("populationPolicy").GetProperty("periodStart").GetString());
        Assert.True(root.GetProperty("populationPolicy").GetProperty("postingStatus").GetProperty("includeBlank").GetBoolean());
        Assert.Equal(7, root.GetProperty("taxonomyRevision").GetInt32());
        Assert.Equal(MappingMetadataFormat.CurrentVersion, root.GetProperty("mapping").GetProperty("formatVersion").GetInt32());
        Assert.Equal("gl-batch", root.GetProperty("mapping").GetProperty("gl").GetProperty("sourceBatchId").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("mapping").GetProperty("tb").ValueKind);
        Assert.Equal(
            fields[0].FieldId,
            root.GetProperty("mapping").GetProperty("gl").GetProperty("rdeFields")[0]
                .GetProperty("fieldId").GetString());
    }

    [Fact]
    public void Encode_WithTbSnapshotWritesOneCompleteTbProperty()
    {
        var tb = new CommittedMapping(
            DatasetKind.Tb,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TbMappingKeys.AccNum] = "Account",
                [TbMappingKeys.AccName] = "AccountName",
                [TbMappingKeys.Amount] = "Movement"
            },
            TbChangeModeNames.Direct,
            "tb-batch",
            new DateTimeOffset(2026, 8, 13, 1, 0, 0, TimeSpan.Zero));
        var metadata = new ReportWorkbookMetadata(
            "2026-01-01",
            "2026-12-31",
            TaxonomyRevision: 7,
            GlMapping(),
            tb);

        using var document = JsonDocument.Parse(ReportWorkbookMetadataCodec.Encode(metadata));
        var mapping = document.RootElement.GetProperty("mapping");
        Assert.Equal(
            ["formatVersion", "gl", "tb"],
            mapping.EnumerateObject().Select(static property => property.Name).ToArray());

        var encodedTb = mapping.GetProperty("tb");
        Assert.Equal(
            ["kind", "sourceBatchId", "committedUtc", "mapping", "changeMode"],
            encodedTb.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.Equal("tb", encodedTb.GetProperty("kind").GetString());
        Assert.Equal("tb-batch", encodedTb.GetProperty("sourceBatchId").GetString());
        Assert.Equal(
            "Movement",
            encodedTb.GetProperty("mapping").GetProperty(TbMappingKeys.Amount).GetString());
        Assert.Equal(TbChangeModeNames.Direct, encodedTb.GetProperty("changeMode").GetString());
    }

    [Fact]
    public void Append_MultiChunkMappingV2MetadataKeepsCanonicalPayloadAndHiddenStructure()
    {
        const int rdeFieldCount = 96;
        var metadata = MultiChunkMetadata(rdeFieldCount);
        var canonicalJson = ReportWorkbookMetadataCodec.Encode(metadata);
        Assert.True(canonicalJson.Length > ReportWorkbookMetadataFormat.MaximumChunkLength);

        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var fixturePart = workbookPart.AddNewPart<WorksheetPart>();
            fixturePart.Worksheet = new Worksheet(new SheetData());
            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(fixturePart),
                SheetId = 1,
                Name = "Fixture"
            });
            workbookPart.Workbook.Save();

            ReportWorkbookMetadataWorksheet.Append(document, metadata, CancellationToken.None);
        }

        stream.Position = 0;
        using var read = SpreadsheetDocument.Open(stream, false);
        Assert.Empty(new OpenXmlValidator().Validate(read));

        var workbookPartRead = read.WorkbookPart!;
        var metadataSheet = workbookPartRead.Workbook.Sheets!.Elements<Sheet>().Single(sheet =>
            string.Equals(
                sheet.Name?.Value,
                ReportWorkbookMetadataFormat.WorksheetName,
                StringComparison.Ordinal));
        Assert.Equal(SheetStateValues.VeryHidden, metadataSheet.State?.Value);

        var worksheet = Assert.IsType<WorksheetPart>(
            workbookPartRead.GetPartById(metadataSheet.Id!.Value!)).Worksheet;
        var columns = worksheet.GetFirstChild<Columns>()!.Elements<Column>().ToArray();
        Assert.Collection(
            columns,
            column =>
            {
                Assert.Equal(1U, column.Min?.Value);
                Assert.Equal(1U, column.Max?.Value);
                Assert.False(column.Hidden?.Value ?? false);
            },
            column =>
            {
                Assert.Equal(2U, column.Min?.Value);
                Assert.Equal(2U, column.Max?.Value);
                Assert.True(column.Hidden?.Value ?? false);
            },
            column =>
            {
                Assert.Equal(3U, column.Min?.Value);
                Assert.Equal(3U, column.Max?.Value);
                Assert.True(column.Hidden?.Value ?? false);
            });

        var rows = worksheet.GetFirstChild<SheetData>()!.Elements<Row>().ToArray();
        var header = rows[0];
        Assert.Equal(1U, header.RowIndex?.Value);
        Assert.False(header.Hidden?.Value ?? false);
        Assert.Equal(ReportWorkbookMetadataFormat.Marker, CellText(header, "A1"));
        Assert.Equal(
            ReportWorkbookMetadataFormat.CurrentVersion.ToString(CultureInfo.InvariantCulture),
            CellText(header, "B1"));

        var chunkCount = int.Parse(CellText(header, "C1"), CultureInfo.InvariantCulture);
        var expectedChunkCount = (canonicalJson.Length
                                  + ReportWorkbookMetadataFormat.MaximumChunkLength
                                  - 1)
                                 / ReportWorkbookMetadataFormat.MaximumChunkLength;
        Assert.True(chunkCount > 1);
        Assert.Equal(expectedChunkCount, chunkCount);
        Assert.Equal(chunkCount + 1, rows.Length);
        var chunks = new string[chunkCount];
        for (var index = 0; index < chunkCount; index++)
        {
            var row = rows[index + 1];
            var rowIndex = checked((uint)index + 2U);
            Assert.Equal(rowIndex, row.RowIndex?.Value);
            Assert.True(row.Hidden?.Value ?? false);
            Assert.Equal(
                (index + 1).ToString(CultureInfo.InvariantCulture),
                CellText(row, $"A{rowIndex}"));
            chunks[index] = CellText(row, $"B{rowIndex}");
            Assert.InRange(
                chunks[index].Length,
                1,
                ReportWorkbookMetadataFormat.MaximumChunkLength);
            if (index < chunkCount - 1)
            {
                Assert.Equal(ReportWorkbookMetadataFormat.MaximumChunkLength, chunks[index].Length);
            }
        }

        var reassembled = string.Concat(chunks);
        Assert.Equal(canonicalJson, reassembled);
        using var json = JsonDocument.Parse(reassembled);
        var encodedGl = json.RootElement.GetProperty("mapping").GetProperty("gl");
        var encodedMapping = encodedGl.GetProperty("mapping")
            .EnumerateObject()
            .ToDictionary(
                static property => property.Name,
                static property => property.Value.GetString()!,
                StringComparer.Ordinal);
        var decodedOptions = GlMappingOptionsJsonCodec.ReadProperties(
            encodedGl,
            encodedMapping,
            "mapping.gl.");
        Assert.Equal(rdeFieldCount, decodedOptions.RdeFields.Count);
        Assert.Equal(
            metadata.GlMapping.GlOptions!.RdeFields.ToArray(),
            decodedOptions.RdeFields.ToArray());
    }

    private static string CellText(Row row, string reference)
    {
        var cell = Assert.Single(
            row.Elements<Cell>(),
            candidate => string.Equals(
                candidate.CellReference?.Value,
                reference,
                StringComparison.Ordinal));
        return cell.InlineString?.InnerText ?? cell.CellValue?.Text ?? string.Empty;
    }

    private static ReportWorkbookMetadata MultiChunkMetadata(int rdeFieldCount)
    {
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GlMappingKeys.DocDate] = "ApprovedAt",
            [GlMappingKeys.PostingStatus] = "PostingState"
        };
        var fields = Enumerable.Range(1, rdeFieldCount)
            .Select(index => new GlRdeFieldMetadata(
                $"rde.{index.ToString("x32", CultureInfo.InvariantCulture)}",
                $"SyntheticRdeColumn{index:D3}",
                $"Synthetic RDE {index:D3} {new string('x', 340)}",
                (index % 3) switch
                {
                    0 => RdeFieldValueTypeNames.Text,
                    1 => RdeFieldValueTypeNames.Date,
                    _ => RdeFieldValueTypeNames.Money
                }))
            .ToArray();
        var sourceColumns = new[] { "ApprovedAt", "PostingState" }
            .Concat(fields.Select(static field => field.SourceColumn))
            .ToArray();
        var options = GlMappingOptionsRules.NormalizeAndValidate(
            mapping,
            sourceColumns,
            new GlMappingOptions(
                ApprovalDateModeNames.Mapped,
                new GlPostingStatusPolicy(["posted"], IncludeBlank: true),
                new GlManualAutoPolicy(["M"], ["A"]),
                fields));
        var gl = new CommittedMapping(
            DatasetKind.Gl,
            mapping,
            "debitCredit",
            "gl-batch-multi-chunk",
            new DateTimeOffset(2026, 8, 13, 2, 0, 0, TimeSpan.Zero),
            MappingMetadataFormat.CurrentVersion,
            options);

        return new ReportWorkbookMetadata(
            "2026-01-01",
            "2026-12-31",
            TaxonomyRevision: 7,
            gl,
            TbMapping: null);
    }

    private static CommittedMapping GlMapping()
    {
        var fields = new[]
        {
            new GlRdeFieldMetadata(
                "rde.0123456789abcdef0123456789abcdef",
                "CustomDate",
                "自訂日期",
                RdeFieldValueTypeNames.Date)
        };
        return new CommittedMapping(
            DatasetKind.Gl,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GlMappingKeys.DocDate] = "ApprovedAt",
                [GlMappingKeys.PostingStatus] = "PostingState"
            },
            "debitCredit",
            "gl-batch",
            new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero),
            MappingMetadataFormat.CurrentVersion,
            new GlMappingOptions(
                ApprovalDateModeNames.Mapped,
                new GlPostingStatusPolicy(["posted"], IncludeBlank: true),
                new GlManualAutoPolicy(["M"], ["A"]),
                fields));
    }
}
