using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>唯讀擷取 JET 報告固定工作表 F1:H1 的版本化 mapping metadata。</summary>
public sealed class OpenXmlMappingMetadataReader : IMappingMetadataReader
{
    public Task<MappingDraftMetadata> ReadAsync(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePath(filePath);

        try
        {
            using var document = SpreadsheetDocument.Open(filePath, false);
            var workbookPart = document.WorkbookPart;
            var sheet = workbookPart?.Workbook.Sheets?.Elements<Sheet>()
                .SingleOrDefault(item => string.Equals(
                    item.Name?.Value,
                    MappingMetadataFormat.WorksheetName,
                    StringComparison.Ordinal));
            if (workbookPart is null || sheet?.Id?.Value is null
                || workbookPart.GetPartById(sheet.Id.Value) is not WorksheetPart worksheetPart)
            {
                throw Missing();
            }

            var marker = ReadCell(workbookPart, worksheetPart, MappingMetadataFormat.MarkerCell);
            if (!string.Equals(marker, MappingMetadataFormat.Marker, StringComparison.Ordinal))
            {
                throw Missing();
            }

            var versionText = ReadCell(workbookPart, worksheetPart, MappingMetadataFormat.VersionCell);
            if (!int.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
                || (version != MappingMetadataFormat.LegacyVersion
                    && version != MappingMetadataFormat.CurrentVersion))
            {
                throw Invalid("欄位配對 metadata 版本不受支援。");
            }

            var payload = ReadCell(workbookPart, worksheetPart, MappingMetadataFormat.PayloadCell);
            if (string.IsNullOrWhiteSpace(payload))
            {
                throw Invalid("欄位配對 metadata payload 缺失。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return Task.FromResult(MappingMetadataCodec.Decode(version, payload));
            }
            catch (MappingMetadataFormatException ex)
            {
                throw Invalid(ex.Message);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JetActionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or OpenXmlPackageException
                                   or FormatException
                                   or InvalidOperationException)
        {
            throw new JetActionException(
                JetErrorCodes.FileReadError,
                "無法讀取欄位配對報告；檔案可能損壞、被鎖定或不是有效的 xlsx。");
        }
    }

    private static void ValidatePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath))
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                "filePath 必須是本機絕對路徑。");
        }
        if (!string.Equals(Path.GetExtension(filePath), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new JetActionException(
                JetErrorCodes.UnsupportedFileType,
                "欄位配對 metadata 只支援 .xlsx。");
        }
        if (!File.Exists(filePath))
        {
            throw new JetActionException(JetErrorCodes.FileNotFound, "找不到指定的欄位配對報告。");
        }
    }

    private static string? ReadCell(
        WorkbookPart workbookPart,
        WorksheetPart worksheetPart,
        string reference)
    {
        var cell = worksheetPart.Worksheet.Descendants<Cell>()
            .SingleOrDefault(item => string.Equals(item.CellReference?.Value, reference, StringComparison.Ordinal));
        if (cell is null)
        {
            return null;
        }

        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText;
        }

        var raw = cell.CellValue?.InnerText;
        if (cell.DataType?.Value == CellValues.SharedString
            && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            return workbookPart.SharedStringTablePart?.SharedStringTable
                .Elements<SharedStringItem>().ElementAtOrDefault(index)?.InnerText;
        }

        return raw;
    }

    private static JetActionException Missing() => new(
        JetErrorCodes.MappingMetadataMissing,
        "這份活頁簿沒有 JET 版本化欄位配對 metadata；舊報告不能從可見欄位猜測還原。");

    private static JetActionException Invalid(string message) => new(
        JetErrorCodes.MappingMetadataInvalid,
        message);
}
