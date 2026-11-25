using System.Globalization;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Application;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Excel 日期型儲存格的序列值要讀成 Excel 顯示的日期。Excel 的 1900 日期系統把 1900 年當成閏年：
/// 序列值 1 顯示 1900-01-01，59 顯示 1900-02-28，60 顯示日曆上沒有的 1900-02-29，61 起才和一般的日期換算一致；
/// 序列值 0 顯示 1900 年 1 月 0 日。.xlsx 與 .xls 讀出來的要一樣；.xls 由 ExcelDataReader 換算，
/// 套件換不對的幾個值在測試裡另外寫出預期，並寫明原因。
/// </summary>
public sealed class ExcelSerialDateReadingTests
{
    // 序列值與 Excel 顯示的日期。日曆上沒有的日期照 Excel 顯示的年月日寫成文字，確認配對時列為無法辨識。
    // 負數與超過 9999-12-31 的序列值，Excel 只顯示 #####，讀取器照舊交出序列值的文字。
    private static readonly (double Serial, string Shown)[] Dates1900 =
    [
        (0, "1900-01-00"),
        (1, "1900-01-01"),
        (2, "1900-01-02"),
        (59, "1900-02-28"),
        (59.75, "1900-02-28"),
        (60, "1900-02-29"),
        (61, "1900-03-01"),
        (45292, "2024-01-01"),
        // 45657 連加十次 0.1 得到的值，離隔天不到半毫秒；Excel 與 DateTime.FromOADate 都先四捨五入到毫秒，算成隔天。
        (45657.999999999985, "2025-01-01"),
        (2958465, "9999-12-31"),
        (-5, "-5.0"),
        (2958466, "2958466.0"),
    ];

    // 1904 日期系統的序列值 0 是 1904-01-01，沒有 1900 年閏年的問題。負數往前數到 1900-01-01 為止；
    // 更早的日期與晚於 9999-12-31 的值換不出日期，.xlsx 的讀取器交出加 1,462 後的序列值文字。
    private static readonly (double Serial, string Shown)[] Dates1904 =
    [
        (-1461, "1.0"),
        (-1460.75, "1.25"),
        (-1460, "1900-01-01"),
        (-1, "1903-12-31"),
        (0, "1904-01-01"),
        (1, "1904-01-02"),
        (43830, "2024-01-01"),
        (44195.999999999985, "2025-01-01"),
        (2957003, "9999-12-31"),
        (2957003.5, "9999-12-31"),
        (2957004, "2958466.0"),
    ];

    [Theory]
    [InlineData("xlsx", false)]
    [InlineData("xlsx", true)]
    [InlineData("xls", false)]
    [InlineData("xls", true)]
    public async Task DateCell_IsReadAsTheDateExcelShows(string format, bool date1904)
    {
        var cases = date1904 ? Dates1904 : Dates1900;
        if (format == "xls" && !date1904)
        {
            // .xls 由 ExcelDataReader 換成日期，它把序列值 60 換成和 59 同一天的 1900-02-28，讀取器分不出來；這是套件的限制。
            cases = cases.Select(item => item.Serial == 60 ? (item.Serial, "1900-02-28") : item).ToArray();
        }

        if (format == "xls" && date1904)
        {
            // ExcelDataReader 換算 1904 日期系統時先加 1,462，序列值 -1461 以上、-1460 未滿換出 1899-12-31；
            // 套件不公開日期系統，讀取器照 1900 日期系統的序列值 0 存成 1900-01-00。換不出日期時，套件交回原本的序列值。
            // 這兩點和 .xlsx 不同，是套件的限制。
            cases = cases.Select(item => item.Serial switch
            {
                >= -1461 and < -1460 => (item.Serial, "1900-01-00"),
                2957004 => (item.Serial, "2957004.0"),
                _ => item,
            }).ToArray();
        }

        var path = WriteSource(format, date1904, cases.Select(item => item.Serial).ToArray());
        try
        {
            ITabularFileReader reader = format == "xlsx" ? new OpenXmlSaxTableReader() : new BinaryExcelTableReader();
            var read = new List<string>();
            await foreach (var row in reader.ReadRowsAsync(new TabularSourceRequest(path), CancellationToken.None))
            {
                read.Add(row.Values["NativeDate"]);
            }

            Assert.Equal(
                cases.Select(item => item.Serial.ToString(CultureInfo.InvariantCulture) + " → " + item.Shown),
                cases.Zip(read, (item, value) => item.Serial.ToString(CultureInfo.InvariantCulture) + " → " + value));
        }
        finally { File.Delete(path); }
    }

    // 日曆上沒有的 1900-02-29 與 1900-01-00 列在確認配對後的日期提醒，標出日，原因是日曆上沒有這一天；
    // 序列值 1 的 1900-01-01 早於 1900-03-01，底稿寫成文字，另列一則底稿寫成文字，標出整個值；
    // 序列值 45292 的 2024-01-01 是一般的日期，不列。
    [Theory]
    [InlineData("xlsx")]
    [InlineData("xls")]
    public async Task DateCellExcelShowsAsNonexistentDay_IsListedAsNoSuchDate(string format)
    {
        var path = WriteSource(format, date1904: false, [60, 0, 1, 45292]);
        try
        {
            using var host = new HandlerTestHost();
            await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
            { projectName = "Excel serial dates", periodStart = "2024-01-01", periodEnd = "2025-12-31", databaseProvider = "sqlite" }));
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            var mapping = MappingCaseTemplateFixture.Mapping();
            mapping["approvalDate"] = "NativeDate";

            var committed = JsonSerializer.SerializeToElement(await host.DispatchAsync("mapping.commit.gl",
                JsonSerializer.Serialize(new { mapping, amountMode = "signed" })));

            // .xls 的序列值 60 由 ExcelDataReader 換成 1900-02-28，是讀得成的日期，不列為日曆上沒有這一天，
            // 和 1900-01-01 一起列在底稿寫成文字那一則；這是套件的限制。
            string[] expected = format == "xls"
                ? ["1900-01-00|noSuchDate|8|2|3"]
                : ["1900-02-29|noSuchDate|8|2|2", "1900-01-00|noSuchDate|8|2|3"];
            string[] expectedText = format == "xls"
                ? ["1900-02-28|0|10|2", "1900-01-01|0|10|4"]
                : ["1900-01-01|0|10|4"];
            Assert.Equal(4, committed.GetProperty("projectedRowCount").GetInt32());
            var notices = committed.GetProperty("dateNotices").EnumerateArray().ToArray();
            Assert.Equal(2, notices.Length);
            var text = notices[1];
            Assert.Equal("NativeDate", text.GetProperty("sourceColumn").GetString());
            Assert.Equal("textInWorkingPaper", text.GetProperty("kind").GetString());
            Assert.Equal("kept", text.GetProperty("handling").GetString());
            Assert.Equal(expectedText.Length, text.GetProperty("count").GetInt64());
            Assert.Equal(
                expectedText,
                text.GetProperty("values").EnumerateArray().Select(value =>
                    value.GetProperty("value").GetString() + "|"
                    + value.GetProperty("markStart").GetInt32() + "|" + value.GetProperty("markLength").GetInt32() + "|"
                    + string.Join(",", value.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("row").GetInt32()))));
            var notice = notices[0];
            Assert.Equal("NativeDate", notice.GetProperty("sourceColumn").GetString());
            Assert.Equal("unrecognized", notice.GetProperty("kind").GetString());
            Assert.Equal("notUsed", notice.GetProperty("handling").GetString());
            Assert.Equal(expected.Length, notice.GetProperty("count").GetInt64());
            Assert.Equal(
                expected,
                notice.GetProperty("values").EnumerateArray().Select(value =>
                    value.GetProperty("value").GetString() + "|" + value.GetProperty("problem").GetString() + "|"
                    + value.GetProperty("markStart").GetInt32() + "|" + value.GetProperty("markLength").GetInt32() + "|"
                    + string.Join(",", value.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("row").GetInt32()))));
        }
        finally { File.Delete(path); }
    }

    // 內建格式 32 在繁體中文版 Excel 顯示「13時30分」這類只有時間的寫法，JET 仍當成日期格式讀。
    // 只有時間、沒有日期部分的值不換成日期，交出序列值的文字，不同的時間仍分得出來；帶日期的值照舊只取日期。
    [Fact]
    public async Task TraditionalChineseTimeFormat_TimeOnlyValueIsKeptAsSerialText()
    {
        var path = WriteSource("xlsx", date1904: false, [0.5625, 0.375, 45000.5625], numberFormatId: 32);
        try
        {
            var read = new List<string>();
            await foreach (var row in new OpenXmlSaxTableReader().ReadRowsAsync(new TabularSourceRequest(path), CancellationToken.None))
            {
                read.Add(row.Values["NativeDate"]);
            }

            Assert.Equal(["0.5625", "0.375", "2023-03-15"], read);
        }
        finally { File.Delete(path); }
    }

    private static readonly string[] Header = ["Document", "Date", "Account", "Name", "Description", "Amount", "NativeDate"];

    private static string WriteSource(string format, bool date1904, double[] serials, int numberFormatId = 14)
    {
        if (format == "xls")
        {
            var xls = Path.Combine(Path.GetTempPath(), $"jet-serial-date-{Guid.NewGuid():N}.xls");
            WriteBiff2(xls, serials, date1904);
            return xls;
        }

        var headerXml = string.Join("", Header.Select((value, index) =>
            $"<c r=\"{(char)('A' + index)}1\" t=\"inlineStr\"><is><t>{value}</t></is></c>"));
        string DataRow(int row, double serial) => $"<row r=\"{row}\">"
            + $"<c r=\"A{row}\" t=\"inlineStr\"><is><t>V{row - 1}</t></is></c>"
            + $"<c r=\"B{row}\" t=\"inlineStr\"><is><t>2025-03-05</t></is></c>"
            + $"<c r=\"C{row}\" t=\"inlineStr\"><is><t>A</t></is></c>"
            + $"<c r=\"D{row}\" t=\"inlineStr\"><is><t>Synthetic</t></is></c>"
            + $"<c r=\"E{row}\" t=\"inlineStr\"><is><t>Normal</t></is></c>"
            + $"<c r=\"F{row}\"><v>1</v></c>"
            + $"<c r=\"G{row}\" s=\"1\"><v>{serial.ToString("R", CultureInfo.InvariantCulture)}</v></c></row>";
        var rows = string.Concat(serials.Select((serial, index) => DataRow(index + 2, serial)));
        var path = new RawXlsxBuilder()
            .WithStyles($"""<cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="{numberFormatId}" applyNumberFormat="1"/></cellXfs>""")
            .AddSheet("Sheet1", $"<sheetData><row r=\"1\">{headerXml}</row>{rows}</sheetData>")
            .Save();
        if (date1904)
        {
            using var workbook = SpreadsheetDocument.Open(path, true);
            var workbookElement = workbook.WorkbookPart!.RequiredWorkbook();
            workbookElement.WorkbookProperties = new WorkbookProperties { Date1904 = true };
            workbookElement.Save();
        }

        return path;
    }

    // BIFF2 的 .xls：欄名與文字用 LABEL，日期欄用 NUMBER，三個屬性位元組的第二個是數字格式 14，也就是日期。
    private static void WriteBiff2(string path, double[] serials, bool date1904)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        void Record(ushort id, Action<BinaryWriter> write)
        {
            using var body = new MemoryStream();
            using (var data = new BinaryWriter(body, Encoding.ASCII, leaveOpen: true)) write(data);
            writer.Write(id); writer.Write((ushort)body.Length); writer.Write(body.ToArray());
        }
        void Label(ushort row, ushort column, string value) => Record(0x0004, data =>
        {
            data.Write(row); data.Write(column); data.Write(new byte[3]);
            var text = Encoding.ASCII.GetBytes(value); data.Write((byte)text.Length); data.Write(text);
        });
        Record(0x0009, data => { data.Write((ushort)0x0002); data.Write((ushort)0x0010); });
        Record(0x0022, data => data.Write((ushort)(date1904 ? 1 : 0)));
        Record(0x0000, data =>
        {
            data.Write((ushort)0); data.Write((ushort)(serials.Length + 1)); data.Write((ushort)0); data.Write((ushort)Header.Length);
        });
        for (ushort column = 0; column < Header.Length; column++) Label(0, column, Header[column]);
        for (ushort row = 1; row <= serials.Length; row++)
        {
            string[] values = [$"V{row}", "2025-03-05", "A", "Synthetic", "Normal", "1"];
            for (ushort column = 0; column < values.Length; column++) Label(row, column, values[column]);
            var serial = serials[row - 1];
            Record(0x0003, data => { data.Write(row); data.Write((ushort)6); data.Write(new byte[] { 0, 14, 0 }); data.Write(serial); });
        }
        Record(0x000A, _ => { });
    }
}
