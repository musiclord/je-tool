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
                || version != MappingMetadataFormat.CurrentVersion)
            {
                throw Invalid("報告裡的欄位配對資訊版本不受支援，無法還原。請回第三步欄位配對直接重新選擇。");
            }

            var payload = ReadCell(workbookPart, worksheetPart, MappingMetadataFormat.PayloadCell);
            if (string.IsNullOrWhiteSpace(payload))
            {
                throw Invalid("報告裡的欄位配對資訊不完整，無法還原。請回第三步欄位配對直接重新選擇。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return Task.FromResult(MappingMetadataCodec.Decode(version, payload));
            }
            catch (MappingMetadataFormatException ex)
            {
                throw new JetActionException(
                    JetErrorCodes.MappingMetadataInvalid,
                    "報告裡的欄位配對資訊格式無效，無法還原。請回第三步欄位配對直接重新選擇。",
                    innerException: ex);
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
                "從報告還原欄位配對只支援 .xlsx 檔案，請選擇 JET 匯出的 .xlsx 報告。");
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
        "這份活頁簿沒有 JET 記錄的欄位配對資訊，無法還原；舊報告不能從畫面上的欄位推測配對。請改選較新的 JET 報告，或回第三步欄位配對直接重新選擇。");

    private static JetActionException Invalid(string message) => new(
        JetErrorCodes.MappingMetadataInvalid,
        message);
}
