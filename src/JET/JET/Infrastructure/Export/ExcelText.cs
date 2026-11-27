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
/// 文字寫成 inline string 還是寫進共用字串表，也只在這裡決定：工作表屬於寫法和 Excel 存檔時相同的正式輸出時，
/// <see cref="SetText"/> 與 <see cref="SetVerbatimText"/> 把文字寫進共用字串表，格子只放編號；其他活頁簿寫成 inline string。
/// JET 讀回用的隱藏紀錄與工作底稿用名稱帶 Inline 的方法，不論哪一種活頁簿都寫成 inline string。
/// 文字一律加 xml:space="preserve"：Excel 依這個屬性決定空白怎麼處理，加了才保證前後空白與連續空白照原樣保留。
/// Excel 一格最多 32,767 個字元。一般文字寫出後超過這個長度時，寫到上限並在結尾加上已截斷的註明，記在那張工作表的截斷格數；
/// 長度以跳脫後寫進檔案的字數計算，它不會比原文短，所以不論以原文或寫出的字數計算都不超過上限。
/// </summary>
internal static class ExcelText
{
    /// <summary>一個字元寫成 _xHHHH_ 時占的字數。</summary>
    private const int EscapedCharacterLength = 7;

    /// <summary>截斷時留給原文的字數：上限扣掉結尾的註明。註明沒有要跳脫的字元，寫出的字數等於它的長度。</summary>
    private static readonly int TruncatedTextBudget =
        ExcelWorksheetConstraints.MaxCellTextLength - ReportMessages.TruncatedCellNotice.Length;

    /// <summary>寫進 <paramref name="part"/> 這張工作表的文字；超過每格上限時截斷，並記在這張工作表的截斷格數。</summary>
    public static InlineString InlineString(string value, WorksheetPart part) =>
        TextElement(EncodeWithinLimit(value, part));

    /// <summary>和 <see cref="InlineString(string, WorksheetPart)"/> 相同，給以 XML 元素組列的寫出程式用。</summary>
    public static XElement InlineStringElement(XNamespace spreadsheet, string value, WorksheetPart part) =>
        TextElement(spreadsheet, EncodeWithinLimit(value, part));

    /// <summary>
    /// 照原樣寫出、不截斷的文字：JET 讀回用的配對資料與報告資料、篩選條件說明切好的分段，以及程式固定的文字。
    /// 這些文字由寫出的程式自己控制長度；配對資料與報告資料被截斷的話，JET 就讀不回來。
    /// </summary>
    public static InlineString VerbatimInlineString(string value) => TextElement(Encode(value));

    /// <summary>和 <see cref="VerbatimInlineString(string)"/> 相同，給以 XML 元素組列的寫出程式用。</summary>
    public static XElement VerbatimInlineStringElement(XNamespace spreadsheet, string value) =>
        TextElement(spreadsheet, Encode(value));

    /// <summary>
    /// 把 <paramref name="cell"/> 寫成文字格，文字寫進 <paramref name="part"/> 這張工作表；超過每格上限時截斷，並記在這張工作表的截斷格數。
    /// 這張工作表屬於寫法和 Excel 存檔時相同的正式輸出時，文字寫進共用字串表，格子只放編號；其他活頁簿寫成 inline string。
    /// </summary>
    public static void SetText(Cell cell, string value, WorksheetPart part) =>
        SetEncodedText(cell, EncodeWithinLimit(value, part), part);

    /// <summary>
    /// 和 <see cref="SetText"/> 相同，但照原樣寫出、不截斷：篩選條件說明切好的分段與程式固定的文字，長度由寫出的程式自己控制。
    /// </summary>
    public static void SetVerbatimText(Cell cell, string value, WorksheetPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        SetEncodedText(cell, Encode(value), part);
    }

    /// <summary>
    /// 把 <paramref name="cell"/> 寫成 inline string，不論活頁簿有沒有共用字串表；超過每格上限時截斷，記在這張工作表的截斷格數。
    /// 給工作底稿這類寫成 inline string 的輸出用。
    /// </summary>
    public static void SetInlineText(Cell cell, string value, WorksheetPart part)
    {
        ArgumentNullException.ThrowIfNull(cell);
        cell.DataType = CellValues.InlineString;
        cell.InlineString = InlineString(value, part);
    }

    /// <summary>
    /// 把 <paramref name="cell"/> 寫成 inline string、照原樣不截斷，不論活頁簿有沒有共用字串表。
    /// JET 讀回用的隱藏紀錄用這個寫法，讀回時只要讀那幾格，不必載入整份共用字串表。
    /// </summary>
    public static void SetVerbatimInlineText(Cell cell, string value)
    {
        ArgumentNullException.ThrowIfNull(cell);
        cell.DataType = CellValues.InlineString;
        cell.InlineString = VerbatimInlineString(value);
    }

    /// <summary>和 <see cref="SetInlineText"/> 相同，給以 XML 元素組列的寫出程式用；一律寫成 inline string。</summary>
    public static void SetInlineTextElement(XElement cell, XNamespace spreadsheet, string value, WorksheetPart part)
    {
        ArgumentNullException.ThrowIfNull(cell);
        cell.SetAttributeValue("t", "inlineStr");
        cell.Add(InlineStringElement(spreadsheet, value, part));
    }

    /// <summary>和 <see cref="SetVerbatimInlineText"/> 相同，給以 XML 元素組列的寫出程式用；一律寫成 inline string。</summary>
    public static void SetVerbatimInlineTextElement(XElement cell, XNamespace spreadsheet, string value)
    {
        ArgumentNullException.ThrowIfNull(cell);
        cell.SetAttributeValue("t", "inlineStr");
        cell.Add(VerbatimInlineStringElement(spreadsheet, value));
    }

    private static void SetEncodedText(Cell cell, string encoded, WorksheetPart part)
    {
        ArgumentNullException.ThrowIfNull(cell);
        if (FormalWorkbookArchive.SharedStringsOf(part.OpenXmlPackage) is { } sharedStrings)
        {
            cell.DataType = CellValues.SharedString;
            cell.InlineString = null;
            cell.CellValue = new CellValue(sharedStrings.Add(encoded).ToString(CultureInfo.InvariantCulture));
            return;
        }

        cell.DataType = CellValues.InlineString;
        cell.InlineString = TextElement(encoded);
    }

    /// <summary>
    /// 把已經經過 <see cref="Encode"/> 的文字寫成一筆共用字串項目：<c>&lt;si&gt;&lt;t xml:space="preserve"&gt;…&lt;/t&gt;&lt;/si&gt;</c>，
    /// 直接寫成 XML 文字，不經過 XML 寫出程式。編碼後只剩 XML 允許的字元，所以只把 &amp;、&lt; 與 &gt; 寫成實體參照，
    /// LF 與 Tab 照原樣寫。文字裡還有要跳脫的字元，表示呼叫端漏了編碼，丟出例外，不寫出讀不回來的 XML。
    /// </summary>
    internal static void WriteSharedStringItem(TextWriter writer, string encoded)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(encoded);
        writer.Write("<si><t xml:space=\"preserve\">");
        var start = 0;
        for (var index = 0; index < encoded.Length; index++)
        {
            if (IsPairStart(encoded, index))
            {
                index++;
                continue;
            }

            var character = encoded[index];
            var entity = character switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                _ => null
            };
            if (entity is null)
            {
                if (Escaped(character))
                {
                    throw new ArgumentException("共用字串的文字要先經過 Excel 文字編碼。", nameof(encoded));
                }

                continue;
            }

            writer.Write(encoded.AsSpan(start, index - start));
            writer.Write(entity);
            start = index + 1;
        }

        writer.Write(encoded.AsSpan(start));
        writer.Write("</t></si>");
    }

    /// <summary>這張工作表到目前為止有幾格文字因為超過上限而截斷。</summary>
    public static int TruncatedCellCount(WorksheetPart part) => part.Annotation<TruncatedCells>()?.Count ?? 0;

    private static InlineString TextElement(string encoded) =>
        new(new Text(encoded) { Space = SpaceProcessingModeValues.Preserve });

    private static XElement TextElement(XNamespace spreadsheet, string encoded) =>
        new(spreadsheet + "is",
            new XElement(spreadsheet + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), encoded));

    /// <summary>
    /// 寫出的字數不超過上限就照 <see cref="Encode"/> 的結果寫。超過時從頭累加每個字元寫出的字數，
    /// 加到放不下為止，再接上註明。成對的 surrogate 當成一個字元，放不下就整對不寫，不會切開。
    /// 截出的開頭單獨編碼時，結尾附近的底線可能不必再寫成 _x005F_，所以實際寫出的字數只會比累加的少。
    /// </summary>
    private static string EncodeWithinLimit(string value, WorksheetPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        var encoded = Encode(value);
        if (encoded.Length <= ExcelWorksheetConstraints.MaxCellTextLength)
        {
            return encoded;
        }

        var written = 0;
        var cut = 0;
        while (cut < value.Length)
        {
            var length = IsPairStart(value, cut) ? 2 : 1;
            var width = length == 1 && WrittenAsEscape(value, cut) ? EscapedCharacterLength : length;
            if (written + width > TruncatedTextBudget)
            {
                break;
            }

            written += width;
            cut += length;
        }

        CountTruncatedCell(part);
        return Encode(value[..cut]) + ReportMessages.TruncatedCellNotice;
    }

    private static void CountTruncatedCell(WorksheetPart part)
    {
        var cells = part.Annotation<TruncatedCells>();
        if (cells is null)
        {
            cells = new TruncatedCells();
            part.AddAnnotation(cells);
        }

        cells.Count++;
    }

    /// <summary>掛在工作表上的截斷格數；同一張工作表由同一個寫出程式依序寫入。</summary>
    private sealed class TruncatedCells
    {
        public int Count { get; set; }
    }

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

    /// <summary>不在成對 surrogate 裡的這個字元，<see cref="Encode"/> 是否會寫成 _xHHHH_。</summary>
    private static bool WrittenAsEscape(string value, int index) =>
        Escaped(value[index]) || (value[index] == '_' && ReadsAsEscape(value, index));

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
