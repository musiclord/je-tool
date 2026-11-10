using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 唯讀擷取檔案裡要帶入第三步的欄位配對。六份正式報表都有隱藏工作表 JET_Metadata，先讀它；
/// 沒有時讀「自動化工具-檔案欄位資訊」F1:H1（驗證報告與工作底稿另寫的同一份配對）。裁定 2026-11-05-03。
/// 兩種隱藏紀錄都沒有時，例如舊版 IDEA JET 的底稿，改讀同一張工作表看得見的 A 到 E 欄，交給 handler 推得草稿；
/// 隱藏紀錄存在但格式不對時，照舊整份不帶入（裁定 2026-11-09-28、2026-11-05-14）。
/// </summary>
public sealed class OpenXmlMappingMetadataReader : IMappingMetadataReader
{
    private const string TbSectionTitle = "TB檔案配對前後欄位對照表";
    private const string GlSectionTitle = "GL檔案配對前後欄位對照表";
    private const string BeforeNameHeader = "配對前欄位名稱";

    private const string VersionUnsupportedMessage = "檔案裡的欄位配對紀錄版本不受支援，無法帶入。請直接在下方選擇欄位。";
    private const string IncompleteMessage = "檔案裡的欄位配對紀錄不完整，無法帶入。請直接在下方選擇欄位。";
    private const string InvalidFormatMessage = "檔案裡的欄位配對紀錄格式無效，無法帶入。請直接在下方選擇欄位。";

    public Task<MappingDraftSource> ReadAsync(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePath(filePath);

        try
        {
            // Excel 開著報告也讀得到最後一次存檔的內容（裁定 2026-11-06-12）。
            using var stream = SourceFileAccess.OpenRead(filePath);
            using var document = SpreadsheetDocument.Open(stream, false);
            var workbookPart = document.WorkbookPart ?? throw FieldInfoSheetMissing();
            if (FindWorksheet(workbookPart, ReportWorkbookMetadataFormat.WorksheetName) is { } reportMetadata)
            {
                return Task.FromResult(Recorded(ReadReportWorkbookMetadata(workbookPart, reportMetadata, cancellationToken)));
            }

            var worksheetPart = FindWorksheet(workbookPart, MappingMetadataFormat.WorksheetName) ?? throw FieldInfoSheetMissing();
            var marker = ReadCell(workbookPart, worksheetPart, MappingMetadataFormat.MarkerCell);
            var versionText = ReadCell(workbookPart, worksheetPart, MappingMetadataFormat.VersionCell);
            var payload = ReadCell(workbookPart, worksheetPart, MappingMetadataFormat.PayloadCell);
            if (string.IsNullOrWhiteSpace(marker) && string.IsNullOrWhiteSpace(versionText) && string.IsNullOrWhiteSpace(payload))
            {
                // F1:H1 三格都空白才算沒有隱藏紀錄，改讀看得見的配對前後欄位對照表（裁定 2026-11-09-28）。
                return Task.FromResult(new MappingDraftSource(
                    null,
                    ReadFieldInfoSheet(workbookPart, worksheetPart, cancellationToken)));
            }

            if (!string.Equals(marker, MappingMetadataFormat.Marker, StringComparison.Ordinal))
            {
                // 三格有內容但標記不對，算隱藏紀錄存在但格式不對，整份不帶入（裁定 2026-11-05-14）。
                throw Invalid(InvalidFormatMessage);
            }

            if (!int.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
                || version != MappingMetadataFormat.CurrentVersion)
            {
                throw Invalid(VersionUnsupportedMessage);
            }

            if (string.IsNullOrWhiteSpace(payload))
            {
                throw Invalid(IncompleteMessage);
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return Task.FromResult(Recorded(MappingMetadataCodec.Decode(version, payload)));
            }
            catch (MappingMetadataFormatException ex)
            {
                throw new JetActionException(
                    JetErrorCodes.MappingMetadataInvalid,
                    InvalidFormatMessage,
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
        catch (Exception ex) when (SourceFileErrors.IsInUse(ex) || ex is UnauthorizedAccessException)
        {
            // 被其他程式獨占或沒有權限時，和其他匯入用同一句；Excel 一般開著的檔照樣讀得到（裁定 2026-11-06-12）。
            throw SourceFileErrors.CannotOpen(filePath, ex);
        }
        catch (Exception ex) when (ex is IOException
                                   or OpenXmlPackageException
                                   or FormatException
                                   or InvalidOperationException
                                   or OverflowException
                                   or ArgumentException
                                   or InvalidDataException
                                   or System.Xml.XmlException)
        {
            // 損壞、設了密碼或不是 .xlsx 的檔案都走這裡，訊息只寫發生什麼事與下一步，不推測原因（裁定 2026-10-30-20）。
            throw new JetActionException(
                JetErrorCodes.FileReadError,
                "無法讀取這份檔案。請改選能用 Excel 開啟的 .xlsx 工作底稿，或直接在下方選擇欄位。",
                innerException: ex);
        }
    }

    private static MappingDraftSource Recorded(MappingDraftMetadata metadata) => new(metadata, null);

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
            // 隱藏紀錄存在但標記不對，照舊整份不帶入（裁定 2026-11-05-14）。
            throw Invalid(InvalidFormatMessage);
        }

        if (!int.TryParse(ReadCell(workbookPart, worksheetPart, "B1"), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || version != ReportWorkbookMetadataFormat.CurrentVersion)
        {
            throw Invalid(VersionUnsupportedMessage);
        }

        if (!int.TryParse(ReadCell(workbookPart, worksheetPart, "C1"), NumberStyles.None, CultureInfo.InvariantCulture, out var chunks)
            || chunks < 1)
        {
            throw Invalid(IncompleteMessage);
        }

        var json = new System.Text.StringBuilder();
        for (var index = 1; index <= chunks; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = (index + 1).ToString(CultureInfo.InvariantCulture);
            if (!string.Equals(ReadCell(workbookPart, worksheetPart, "A" + row), index.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                || ReadCell(workbookPart, worksheetPart, "B" + row) is not { } chunk)
            {
                throw Invalid(IncompleteMessage);
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
                InvalidFormatMessage,
                innerException: ex);
        }
    }

    /// <summary>
    /// 讀「自動化工具-檔案欄位資訊」看得見的 A 到 E 欄。用 A 欄的兩個段落標題找出 TB 段與 GL 段，不靠固定列號：
    /// 舊版 IDEA JET 在兩段之間空一列（idea-tool.bas 第 10824 到 10963 行），新版空兩列。標題下一列是表頭時跳過，
    /// 資料列讀到 A 到 E 整列空白，或遇到另一段標題為止。只回傳表格原文，配對怎麼推得由 Domain 的 FieldInfoSheetMapping 決定。
    /// </summary>
    private static FieldInfoSheet ReadFieldInfoSheet(
        WorkbookPart workbookPart,
        WorksheetPart worksheetPart,
        CancellationToken cancellationToken)
    {
        var grid = ReadColumnsAToE(workbookPart, worksheetPart, cancellationToken);
        var tbTitle = FindTitleRow(grid, TbSectionTitle);
        var glTitle = FindTitleRow(grid, GlSectionTitle)
            ?? throw new JetActionException(
                JetErrorCodes.MappingMetadataMissing,
                "「自動化工具-檔案欄位資訊」裡找不到 GL 的欄位對照表，無法帶入欄位。請改選工作底稿，或直接在下方選擇欄位。");

        return new FieldInfoSheet(
            tbTitle is { } tbRow ? ReadSection(grid, tbRow) : [],
            ReadSection(grid, glTitle));
    }

    private static int? FindTitleRow(SortedDictionary<int, string?[]> grid, string title)
    {
        foreach (var (rowIndex, cells) in grid)
        {
            if (IsTitle(cells[0], title))
            {
                return rowIndex;
            }
        }

        return null;
    }

    private static IReadOnlyList<FieldInfoSheetRow> ReadSection(SortedDictionary<int, string?[]> grid, int titleRow)
    {
        var rows = new List<FieldInfoSheetRow>();
        var rowIndex = titleRow + 1;
        if (grid.TryGetValue(rowIndex, out var header) && IsTitle(header[0], BeforeNameHeader))
        {
            rowIndex++;
        }

        for (; grid.TryGetValue(rowIndex, out var cells); rowIndex++)
        {
            if (cells.All(string.IsNullOrWhiteSpace)
                || IsTitle(cells[0], TbSectionTitle)
                || IsTitle(cells[0], GlSectionTitle))
            {
                break;
            }

            rows.Add(new FieldInfoSheetRow(cells[0] ?? string.Empty, cells[1], cells[4]));
        }

        return rows;
    }

    private static bool IsTitle(string? value, string title) =>
        string.Equals(value?.Trim(), title, StringComparison.Ordinal);

    /// <summary>
    /// 一次讀出 A 到 E 欄的文字，依列號排序；沒有寫出的列與儲存格當成空白。儲存格沒有寫參照時，照出現順序推列號與欄號。
    /// </summary>
    private static SortedDictionary<int, string?[]> ReadColumnsAToE(
        WorkbookPart workbookPart,
        WorksheetPart worksheetPart,
        CancellationToken cancellationToken)
    {
        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable?
            .Elements<SharedStringItem>().ToArray();
        var grid = new SortedDictionary<int, string?[]>();
        var previousRow = 0;
        foreach (var row in worksheetPart.Worksheet?.Descendants<Row>() ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowIndex = row.RowIndex?.Value is { } explicitRow ? checked((int)explicitRow) : checked(previousRow + 1);
            previousRow = rowIndex;
            var previousColumn = 0;
            foreach (var cell in row.Elements<Cell>())
            {
                // 推算的列號與欄號也檢查溢位，損壞的檔案照讀不到的檔案處理。
                var column = ColumnNumber(cell.CellReference?.Value) ?? checked(previousColumn + 1);
                previousColumn = column;
                if (column > 5)
                {
                    continue;
                }

                if (!grid.TryGetValue(rowIndex, out var cells))
                {
                    cells = new string?[5];
                    grid[rowIndex] = cells;
                }

                cells[column - 1] = CellText(workbookPart, cell, sharedStrings);
            }
        }

        return grid;
    }

    private static int? ColumnNumber(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        var column = 0;
        foreach (var character in reference)
        {
            if (character is >= 'A' and <= 'Z')
            {
                column = checked(column * 26 + (character - 'A' + 1));
            }
            else if (character is >= 'a' and <= 'z')
            {
                column = checked(column * 26 + (character - 'a' + 1));
            }
            else
            {
                break;
            }
        }

        return column > 0 ? column : null;
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
                "由底稿匯入欄位只支援 .xlsx 檔案。請改選 .xlsx 的工作底稿，或直接在下方選擇欄位。");
        }
        if (!File.Exists(filePath))
        {
            throw new JetActionException(JetErrorCodes.FileNotFound, "找不到指定的檔案。請重新選擇檔案，或直接在下方選擇欄位。");
        }
    }

    private static string? ReadCell(
        WorkbookPart workbookPart,
        WorksheetPart worksheetPart,
        string reference)
    {
        var cell = worksheetPart.Worksheet?.Descendants<Cell>()
            .SingleOrDefault(item => string.Equals(item.CellReference?.Value, reference, StringComparison.Ordinal));
        return cell is null ? null : CellText(workbookPart, cell, sharedStrings: null);
    }

    private static string? CellText(
        WorkbookPart workbookPart,
        Cell cell,
        IReadOnlyList<SharedStringItem>? sharedStrings)
    {
        // JET 寫出與 Excel 另存的文字都用 _xHHHH_ 跳脫格式，讀回時還原，配對資訊裡的欄名才與匯入的欄名相同。
        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText is { } inline ? ExcelText.Decode(inline) : null;
        }

        var raw = cell.CellValue?.InnerText;
        if (cell.DataType?.Value == CellValues.SharedString
            && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            var item = sharedStrings is null
                ? workbookPart.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().ElementAtOrDefault(index)
                : index < sharedStrings.Count ? sharedStrings[index] : null;
            return item?.InnerText is { } shared ? ExcelText.Decode(shared) : null;
        }

        return raw;
    }

    private static JetActionException FieldInfoSheetMissing() => new(
        JetErrorCodes.MappingMetadataMissing,
        "這份檔案沒有「自動化工具-檔案欄位資訊」工作表，無法帶入欄位。請改選工作底稿，或直接在下方選擇欄位。");

    private static JetActionException Invalid(string message) => new(
        JetErrorCodes.MappingMetadataInvalid,
        message);
}
