using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;

namespace JET.Infrastructure;

internal static class ReportWorkbookMetadataCodec
{
    public static string Encode(ReportWorkbookMetadata metadata)
    {
        ReportWorkbookMetadataInvariant.Validate(metadata);
        var options = metadata.GlMapping.GlOptions
            ?? GlMappingOptions.NormalizeLegacy(metadata.GlMapping.Mapping);
        GlMappingOptionsJsonCodec.Validate(options, metadata.GlMapping.Mapping);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
               {
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   Indented = false
               }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", ReportWorkbookMetadataFormat.CurrentVersion);
            writer.WriteString("approvalDateMode", options.ApprovalDateMode);
            writer.WritePropertyName("populationPolicy");
            writer.WriteStartObject();
            writer.WriteString("periodStart", metadata.PeriodStart);
            writer.WriteString("periodEnd", metadata.PeriodEnd);
            writer.WritePropertyName("postingStatus");
            writer.WriteStartObject();
            var postingMapped = metadata.GlMapping.Mapping.TryGetValue(
                    GlMappingKeys.PostingStatus,
                    out var postingColumn)
                && !string.IsNullOrWhiteSpace(postingColumn);
            writer.WriteBoolean("isMapped", postingMapped);
            WriteStrings(
                writer,
                "acceptedValues",
                options.PostingStatusPolicy?.AcceptedValues ?? Array.Empty<string>());
            writer.WriteBoolean("includeBlank", options.PostingStatusPolicy?.IncludeBlank ?? false);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteNumber("taxonomyRevision", metadata.TaxonomyRevision);
            writer.WritePropertyName("mapping");
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", MappingMetadataFormat.CurrentVersion);
            WriteGl(writer, metadata.GlMapping, options);
            if (metadata.TbMapping is null)
            {
                writer.WritePropertyName("tb");
                writer.WriteNullValue();
            }
            else
            {
                WriteDatasetStart(writer, metadata.TbMapping, "tb");
                WriteMapping(writer, metadata.TbMapping.Mapping, TbMappingKeys.All);
                writer.WriteString("changeMode", metadata.TbMapping.ModeName);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteGl(
        Utf8JsonWriter writer,
        CommittedMapping mapping,
        GlMappingOptions options)
    {
        WriteDatasetStart(writer, mapping, "gl");
        WriteMapping(writer, mapping.Mapping, GlMappingKeys.All);
        writer.WriteString("amountMode", mapping.ModeName);
        GlMappingOptionsJsonCodec.WriteProperties(writer, options);
        writer.WriteEndObject();
    }

    private static void WriteDatasetStart(
        Utf8JsonWriter writer,
        CommittedMapping mapping,
        string name)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteString("kind", mapping.Kind switch
        {
            DatasetKind.Gl => "gl",
            DatasetKind.Tb => "tb",
            _ => throw new InvalidOperationException("Report workbook mapping kind 不受支援。")
        });
        writer.WriteString("sourceBatchId", mapping.SourceBatchId);
        writer.WriteString("committedUtc", mapping.CommittedUtc.ToString("O", CultureInfo.InvariantCulture));
    }

    private static void WriteMapping(
        Utf8JsonWriter writer,
        IReadOnlyDictionary<string, string> mapping,
        IReadOnlyList<string> orderedKeys)
    {
        writer.WritePropertyName("mapping");
        writer.WriteStartObject();
        foreach (var key in orderedKeys)
        {
            if (mapping.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                writer.WriteString(key, value);
            }
        }
        writer.WriteEndObject();
    }

    private static void WriteStrings(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyList<string> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }
        writer.WriteEndArray();
    }
}

internal static class ReportWorkbookMetadataWorksheet
{
    public static void Append(
        SpreadsheetDocument document,
        ReportWorkbookMetadata metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        var workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("Formal report workbook has no workbook part.");
        var sheets = workbookPart.Workbook.Sheets
            ?? throw new InvalidDataException("Formal report workbook has no sheets collection.");
        if (sheets.Elements<Sheet>().Any(sheet => string.Equals(
                sheet.Name?.Value,
                ReportWorkbookMetadataFormat.WorksheetName,
                StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Formal report workbook already contains canonical metadata sheet.");
        }

        var chunks = Chunk(ReportWorkbookMetadataCodec.Encode(metadata));
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        var worksheet = new Worksheet(
            new Columns(
                new Column { Min = 1, Max = 1, Width = 10, CustomWidth = true },
                new Column { Min = 2, Max = 2, Width = 2, Hidden = true, CustomWidth = true },
                new Column { Min = 3, Max = 3, Width = 2, Hidden = true, CustomWidth = true }),
            sheetData);
        worksheetPart.Worksheet = worksheet;
        sheetData.Append(new Row(
            Inline("A1", ReportWorkbookMetadataFormat.Marker),
            Number("B1", ReportWorkbookMetadataFormat.CurrentVersion),
            Number("C1", chunks.Count)) { RowIndex = 1 });
        for (var index = 0; index < chunks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowIndex = checked((uint)index + 2U);
            sheetData.Append(new Row(
                Number($"A{rowIndex}", index + 1),
                Inline($"B{rowIndex}", chunks[index])) { RowIndex = rowIndex, Hidden = true });
        }
        worksheet.Save();

        var nextSheetId = sheets.Elements<Sheet>()
            .Select(sheet => sheet.SheetId?.Value ?? 0U)
            .DefaultIfEmpty(0U)
            .Max() + 1U;
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = nextSheetId,
            Name = ReportWorkbookMetadataFormat.WorksheetName,
            State = SheetStateValues.VeryHidden
        });
        workbookPart.Workbook.Save();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static IReadOnlyList<string> Chunk(string value)
    {
        var result = new List<string>();
        var offset = 0;
        while (offset < value.Length)
        {
            var length = Math.Min(ReportWorkbookMetadataFormat.MaximumChunkLength, value.Length - offset);
            if (offset + length < value.Length && char.IsHighSurrogate(value[offset + length - 1]))
            {
                length--;
            }
            result.Add(value.Substring(offset, length));
            offset += length;
        }
        if (result.Count == 0)
        {
            result.Add(string.Empty);
        }
        return result;
    }

    private static Cell Inline(string reference, string value) => new()
    {
        CellReference = reference,
        DataType = CellValues.InlineString,
        InlineString = new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve })
    };

    private static Cell Number(string reference, int value) => new()
    {
        CellReference = reference,
        DataType = CellValues.Number,
        CellValue = new CellValue(value.ToString(CultureInfo.InvariantCulture))
    };
}
