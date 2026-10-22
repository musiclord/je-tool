using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 儲存格文字寫進活頁簿前的編碼，以及 JET 讀回自己寫的文字時的還原，依 Excel 文字的 _xHHHH_ 跳脫格式：
/// XML 不允許的字元寫成 _xHHHH_；CR 也這樣寫，因為讀取 XML 時 CR 會被換成 LF；原文裡會被讀成跳脫格式的底線寫成 _x005F_。
/// Tab、LF 與成對的 surrogate 照原樣寫入。報告與範本的文字儲存格只透過這裡建立。
/// </summary>
internal static class ExcelText
{
    public static InlineString InlineString(string value, bool preserveSpace = true) =>
        new(new Text(Encode(value)) { Space = preserveSpace ? SpaceProcessingModeValues.Preserve : null });

    public static XElement InlineStringElement(XNamespace spreadsheet, string value) =>
        new(spreadsheet + "is",
            new XElement(spreadsheet + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), Encode(value)));

    public static string Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        StringBuilder? builder = null;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (IsPairStart(value, index))
            {
                builder?.Append(character).Append(value[index + 1]);
                index++;
                continue;
            }

            string? replacement = Escaped(character) ? Escape(character)
                : character == '_' && ReadsAsEscape(value, index) ? "_x005F_"
                : null;
            if (replacement is null)
            {
                builder?.Append(character);
                continue;
            }

            builder ??= new StringBuilder(value.Length + 16).Append(value, 0, index);
            builder.Append(replacement);
        }

        return builder?.ToString() ?? value;
    }

    /// <summary>從左到右還原 _xHHHH_；_x005F_ 還原成底線後，後面的文字照原樣保留。</summary>
    public static string Decode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var start = value.IndexOf("_x", StringComparison.Ordinal);
        if (start < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length).Append(value, 0, start);
        for (var index = start; index < value.Length; index++)
        {
            if (value[index] == '_'
                && index + 6 < value.Length
                && value[index + 1] == 'x'
                && value[index + 6] == '_'
                && ushort.TryParse(value.AsSpan(index + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
            {
                builder.Append((char)code);
                index += 6;
                continue;
            }

            builder.Append(value[index]);
        }

        return builder.ToString();
    }

    /// <summary>以 DOM 存回工作表；失敗時同 <see cref="MarkWriteFailure(Exception, WorksheetPart?, IEnumerable{Cell})"/> 記下位置。</summary>
    public static void SaveWorksheet(WorksheetPart part)
    {
        var worksheet = part.Worksheet
            ?? throw new InvalidOperationException("Worksheet part has no loaded worksheet to save.");
        try
        {
            worksheet.Save();
        }
        catch (Exception error) when (MarkWriteFailure(error, part, worksheet.Descendants<Cell>()))
        {
            throw;
        }
    }

    /// <summary>
    /// 工作表寫入失敗時記下 <c>worksheet_write</c> 階段、工作表名稱，以及第一個含 XML 不允許字元的儲存格與該字元的碼位。
    /// 工作表名稱都是程式寫死的範本名稱，不是案件資料；只記位置與碼位，不記文字。
    /// 供 catch 的 when 條件使用，一律回傳 false，例外照原樣往外丟。
    /// </summary>
    public static bool MarkWriteFailure(Exception error, WorksheetPart? part, IEnumerable<Cell> cells) =>
        Mark(error, part, cells.Select(cell => (cell.CellReference?.Value, cell.InnerText)));

    public static bool MarkWriteFailure(Exception error, WorksheetPart? part, IEnumerable<XElement> cells) =>
        Mark(error, part, cells.Select(cell => ((string?)cell.Attribute("r"), cell.Value)));

    private static bool Mark(Exception error, WorksheetPart? part, IEnumerable<(string? Reference, string Text)> cells)
    {
        var fields = new List<(string Key, object? Value)>(3);
        if (SheetName(part) is { } sheetName)
        {
            fields.Add(("sheet_name", sheetName));
        }

        foreach (var (reference, text) in cells)
        {
            if (FirstInvalidCodePoint(text) is { } codePoint)
            {
                if (reference is not null)
                {
                    fields.Add(("cell_reference", reference));
                }

                fields.Add(("invalid_codepoint", codePoint));
                break;
            }
        }

        return SupportFailureDiagnostics.Mark(error, "worksheet_write", [.. fields]);
    }

    /// <summary>這個 part 在 workbook.xml 登記的工作表名稱；找不到時不記。</summary>
    private static string? SheetName(WorksheetPart? part)
    {
        if (part is null)
        {
            return null;
        }

        try
        {
            var workbookPart = part.GetParentParts().OfType<WorkbookPart>().FirstOrDefault();
            if (workbookPart?.Workbook?.Sheets is not { } sheets)
            {
                return null;
            }

            var id = workbookPart.GetIdOfPart(part);
            return sheets.Elements<Sheet>()
                .FirstOrDefault(sheet => string.Equals(sheet.Id?.Value, id, StringComparison.Ordinal))?.Name?.Value;
        }
        catch (Exception lookupError) when (lookupError is ArgumentException or InvalidOperationException
                                           or OpenXmlPackageException or XmlException or IOException)
        {
            // 找不到工作表名稱只少記一個欄位，不影響原本的例外。
            return null;
        }
    }

    private static int? FirstInvalidCodePoint(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (IsPairStart(text, index))
            {
                index++;
                continue;
            }

            if (text[index] != '\r' && Escaped(text[index]))
            {
                return text[index];
            }
        }

        return null;
    }

    private static bool IsPairStart(string value, int index) =>
        char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]);

    /// <summary>不在成對 surrogate 裡的字元是否要寫成 _xHHHH_：XML 不允許的字元、CR 與單獨的 surrogate。</summary>
    private static bool Escaped(char character) =>
        character == '\r'
        || (character < ' ' && character is not ('\t' or '\n'))
        || character is '￾' or '￿'
        || char.IsSurrogate(character);

    private static string Escape(char character) =>
        "_x" + ((int)character).ToString("X4", CultureInfo.InvariantCulture) + "_";

    /// <summary>
    /// 這個底線之後是 x 與四個十六進位數字，而且再下一個寫出的字元也是底線，Excel 就會把這段讀成跳脫格式。
    /// 下一個字元本身要跳脫時，寫出的第一個字元同樣是底線。大寫 X 一併跳脫，寫成 _x005F_ 在任何情況下都還原成底線。
    /// </summary>
    private static bool ReadsAsEscape(string value, int index)
    {
        if (index + 6 >= value.Length || value[index + 1] is not ('x' or 'X'))
        {
            return false;
        }

        for (var offset = 2; offset < 6; offset++)
        {
            if (!char.IsAsciiHexDigit(value[index + offset]))
            {
                return false;
            }
        }

        var next = index + 6;
        return value[next] == '_' || (!IsPairStart(value, next) && Escaped(value[next]));
    }
}
