using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 唯讀擷取 JET 報告記下的確認配對。六份正式報表都有隱藏工作表 JET_Metadata，先讀它；
/// 沒有時才讀「自動化工具-檔案欄位資訊」F1:H1（驗證報告與工作底稿另寫的同一份配對）。裁定 2026-11-05-03。
/// </summary>
public sealed class OpenXmlMappingMetadataReader : IMappingMetadataReader
{
    public Task<MappingDraftMetadata> ReadAsync(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePath(filePath);

        try
        {
            // Excel 開著報告也讀得到最後一次存檔的內容（裁定 2026-11-06-12）。
            using var stream = SourceFileAccess.OpenRead(filePath);
            using var document = SpreadsheetDocument.Open(stream, false);
            var workbookPart = document.WorkbookPart ?? throw Missing();
            if (FindWorksheet(workbookPart, ReportWorkbookMetadataFormat.WorksheetName) is { } reportMetadata)
            {
                return Task.FromResult(ReadReportWorkbookMetadata(workbookPart, reportMetadata, cancellationToken));
            }

            var worksheetPart = FindWorksheet(workbookPart, MappingMetadataFormat.WorksheetName) ?? throw Missing();
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
                "無法讀取欄位配對報告。請確認檔案沒有被其他程式開啟，而且是 JET 匯出的 xlsx 檔。",
                innerException: ex);
        }
    }

    private static WorksheetPart? FindWorksheet(WorkbookPart workbookPart, string name)
    {
        var sheet = workbookPart.Workbook?.Sheets?.Elements<Sheet>()
            .SingleOrDefault(item => string.Equals(item.Name?.Value, name, StringComparison.Ordinal));
        return sheet?.Id?.Value is { } id && workbookPart.GetPartById(id) is WorksheetPart part ? part : null;
    }

    /// <summary>
    /// JET_Metadata 的格式見 ReportWorkbookMetadataWorksheet.Append：A1 標記、B1 版本、C1 分段數，
    /// 第 2 列起 A 欄是段號、B 欄是 JSON 的一段，依序接起來。
    /// </summary>
    private static MappingDraftMetadata ReadReportWorkbookMetadata(
        WorkbookPart workbookPart,
        WorksheetPart worksheetPart,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(ReadCell(workbookPart, worksheetPart, "A1"), ReportWorkbookMetadataFormat.Marker, StringComparison.Ordinal))
        {
            throw Missing();
        }

        if (!int.TryParse(ReadCell(workbookPart, worksheetPart, "B1"), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || version != ReportWorkbookMetadataFormat.CurrentVersion)
        {
            throw Invalid("報告裡的欄位配對資訊版本不受支援，無法還原。請回第三步欄位配對直接重新選擇。");
        }

        if (!int.TryParse(ReadCell(workbookPart, worksheetPart, "C1"), NumberStyles.None, CultureInfo.InvariantCulture, out var chunks)
            || chunks < 1)
        {
            throw Invalid("報告裡的欄位配對資訊不完整，無法還原。請回第三步欄位配對直接重新選擇。");
        }

        var json = new System.Text.StringBuilder();
        for (var index = 1; index <= chunks; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = (index + 1).ToString(CultureInfo.InvariantCulture);
            if (!string.Equals(ReadCell(workbookPart, worksheetPart, "A" + row), index.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                || ReadCell(workbookPart, worksheetPart, "B" + row) is not { } chunk)
            {
                throw Invalid("報告裡的欄位配對資訊不完整，無法還原。請回第三步欄位配對直接重新選擇。");
            }

            json.Append(chunk);
        }

        try
        {
            return MappingMetadataCodec.DecodeReportWorkbook(json.ToString());
        }
        catch (MappingMetadataFormatException ex)
        {
            throw new JetActionException(
                JetErrorCodes.MappingMetadataInvalid,
                "報告裡的欄位配對資訊格式無效，無法還原。請回第三步欄位配對直接重新選擇。",
                innerException: ex);
        }
    }

    private static void ValidatePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath))
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "file_path_not_absolute");
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
        var cell = worksheetPart.Worksheet?.Descendants<Cell>()
            .SingleOrDefault(item => string.Equals(item.CellReference?.Value, reference, StringComparison.Ordinal));
        if (cell is null)
        {
            return null;
        }

        // JET 寫出與 Excel 另存的文字都用 _xHHHH_ 跳脫格式，讀回時還原，配對資訊裡的欄名才與匯入的欄名相同。
        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText is { } inline ? ExcelText.Decode(inline) : null;
        }

        var raw = cell.CellValue?.InnerText;
        if (cell.DataType?.Value == CellValues.SharedString
            && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            return workbookPart.SharedStringTablePart?.SharedStringTable?
                .Elements<SharedStringItem>().ElementAtOrDefault(index)?.InnerText is { } shared
                ? ExcelText.Decode(shared)
                : null;
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
