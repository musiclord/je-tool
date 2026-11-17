using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class ExcelNativeDateImportTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var provider in new[] { "sqlite" })
        foreach (var format in new[] { "csv", "xlsx", "xlsx1904", "xls", "xls1904" })
        // 日期角色沒有 voucherDate。
        foreach (var field in new[] { "glDate", "approvalDate", "rde" }) yield return [provider, format, field];
    }

    // Excel 日期儲存格的年份在 2100 年之後時照樣讀成日期並保留，確認配對後的日期提醒說明年份不尋常。
    // 文字檔沒有日期型儲存格，同一格的純數字是無法辨識的日期。三種日期角色都試。
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NativeDateAfter2100_IsKeptAndReportedInEveryDateRole(string provider, string format, string field)
    {
        var path = WriteSource(format);
        try
        {
            // 控制列直接驗讀取器，確定 1904 fixture 確實啟用且日期樣式不是普通數字。
            if (format != "csv")
            {
                ITabularFileReader reader = format.StartsWith("xlsx", StringComparison.Ordinal)
                    ? new OpenXmlSaxTableReader() : new BinaryExcelTableReader();
                var rows = new List<StagingRow>();
                await foreach (var row in reader.ReadRowsAsync(new TabularSourceRequest(path), CancellationToken.None)) rows.Add(row);
                Assert.Equal(2, rows.Count);
                Assert.Equal("2101-01-01", rows[0].Values["NativeDate"]);
                Assert.Equal("2024-01-01", rows[1].Values["NativeDate"]);
            }
            using var host = new HandlerTestHost();
            await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
            { projectName = "Native date boundary", periodStart = "2024-01-01", periodEnd = "2025-12-31", databaseProvider = provider }));
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            var mapping = MappingCaseTemplateFixture.Mapping();
            object[] rde = [];
            if (field == "rde") rde = [new { sourceColumn = "NativeDate", label = "Native date", valueType = "date" }];
            else mapping[field] = "NativeDate";

            var committed = JsonSerializer.SerializeToElement(await host.DispatchAsync("mapping.commit.gl",
                JsonSerializer.Serialize(new { mapping, amountMode = "signed", rdeFields = rde })));

            Assert.Equal(2, committed.GetProperty("projectedRowCount").GetInt32());
            var notice = Assert.Single(committed.GetProperty("dateNotices").EnumerateArray());
            Assert.Equal("NativeDate", notice.GetProperty("sourceColumn").GetString());
            Assert.Equal(1, notice.GetProperty("count").GetInt64());
            var value = Assert.Single(notice.GetProperty("values").EnumerateArray());
            Assert.Equal(2, Assert.Single(value.GetProperty("rows").EnumerateArray()).GetProperty("row").GetInt32());
            if (format == "csv")
            {
                Assert.Equal("unrecognized", notice.GetProperty("kind").GetString());
                Assert.Equal(field == "glDate" ? "excluded" : "notUsed", notice.GetProperty("handling").GetString());
                Assert.Equal("240115", value.GetProperty("value").GetString());
                Assert.Equal("number", value.GetProperty("problem").GetString());
            }
            else
            {
                // 標出來的是年份那四個字。
                Assert.Equal("unusualYear", notice.GetProperty("kind").GetString());
                Assert.Equal("kept", notice.GetProperty("handling").GetString());
                Assert.Equal("2101-01-01", value.GetProperty("value").GetString());
                Assert.Equal(JsonValueKind.Null, value.GetProperty("problem").ValueKind);
                Assert.Equal(0, value.GetProperty("markStart").GetInt32());
                Assert.Equal(4, value.GetProperty("markLength").GetInt32());
            }

            // 配成總帳日期時，那一列列入 GL(未納入測試)：文字檔的原因是總帳日期無法辨識，Excel 的是晚於查核期間。
            var stats = JsonSerializer.SerializeToElement(await host.DispatchAsync("query.dataPreview",
                JsonSerializer.Serialize(new { dataset = "glExcluded" }))).GetProperty("stats");
            Assert.Equal(field == "glDate" && format == "csv" ? 1 : 0, stats.GetProperty("unrecognizedGlDateCount").GetInt64());
            Assert.Equal(field == "glDate" && format != "csv" ? 1 : 0, stats.GetProperty("afterPeriodCount").GetInt64());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AccessNativeDateTime_StillPreservesExplicitYearOutsideExcelSerialGuard()
    {
        var native = NativeTabularValue.Read(new DateTime(2110, 12, 31));
        Assert.Equal("2110-12-31", native.Text);
        Assert.Equal(LegacyFieldKind.Date, native.Kind);
    }

    // 七位數的序列值 1,140,611 是 5022-11-19。它照日期儲存格讀成那一天，不當成民國 114 年 6 月 11 日的文字：
    // 讀成民國日期的話會落在查核期間內，不會有提醒，也不會列入 GL(未納入測試)。
    [Theory]
    [InlineData("sqlite", "xlsx")]
    [InlineData("sqlite", "xlsx1904")]
    [InlineData("sqlite", "xls")]
    [InlineData("sqlite", "xls1904")]
    public async Task SevenDigitNativeSerial_CannotFallBackToRocText(string provider, string format)
    {
        var path = WriteSource(format, badOaSerial: 1_140_611);
        try
        {
            using var host = new HandlerTestHost();
            await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
            { projectName = "Native serial is not ROC text", periodStart = "2024-01-01", periodEnd = "2025-12-31", databaseProvider = provider }));
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            var mapping = MappingCaseTemplateFixture.Mapping();
            mapping["glDate"] = "NativeDate";

            var committed = JsonSerializer.SerializeToElement(await host.DispatchAsync("mapping.commit.gl",
                JsonSerializer.Serialize(new { mapping, amountMode = "signed" })));

            var notice = Assert.Single(committed.GetProperty("dateNotices").EnumerateArray());
            Assert.Equal("NativeDate", notice.GetProperty("sourceColumn").GetString());
            Assert.Equal("unusualYear", notice.GetProperty("kind").GetString());
            var value = Assert.Single(notice.GetProperty("values").EnumerateArray());
            Assert.Equal("5022-11-19", value.GetProperty("value").GetString());
            Assert.Equal(2, Assert.Single(value.GetProperty("rows").EnumerateArray()).GetProperty("row").GetInt32());

            var stats = JsonSerializer.SerializeToElement(await host.DispatchAsync("query.dataPreview",
                JsonSerializer.Serialize(new { dataset = "glExcluded" }))).GetProperty("stats");
            Assert.Equal(1, stats.GetProperty("afterPeriodCount").GetInt64());
        }
        finally { File.Delete(path); }
    }

    private static string WriteSource(string format, int badOaSerial = 73416)
    {
        var date1904 = format.EndsWith("1904", StringComparison.Ordinal);
        // 73416 = 2101-01-01；45292 = 2024-01-01。1904 制原序列各少 1462 天。
        var bad = date1904 ? badOaSerial - 1462 : badOaSerial;
        var good = date1904 ? 43830 : 45292;
        string[] header = ["Document", "Date", "Account", "Name", "Description", "Amount", "NativeDate"];
        if (format == "csv")
        {
            // 文字檔沒有日期型儲存格，第 3 列用文字日期 2024-01-01 當控制列。
            // 純數字不當成日期，第 2 列的 240115 是無法辨識。
            var csv = Path.Combine(Path.GetTempPath(), $"jet-date-{Guid.NewGuid():N}.csv");
            File.WriteAllText(csv, string.Join(',', header) + "\nV1,2025-03-05,A,Synthetic,Normal,1,240115\nV2,2025-03-05,A,Synthetic,Normal,1,2024-01-01\n", new UTF8Encoding(false));
            return csv;
        }
        if (format.StartsWith("xls", StringComparison.Ordinal) && !format.StartsWith("xlsx", StringComparison.Ordinal))
        {
            var xls = Path.Combine(Path.GetTempPath(), $"jet-date-{Guid.NewGuid():N}.xls");
            WriteBiff2(xls, header, bad, good, date1904);
            return xls;
        }
        var headerXml = string.Join("", header.Select((value, index) =>
            $"<c r=\"{(char)('A' + index)}1\" t=\"inlineStr\"><is><t>{value}</t></is></c>"));
        string DataRow(int row, int serial) => $"<row r=\"{row}\">"
            + $"<c r=\"A{row}\" t=\"inlineStr\"><is><t>V{row - 1}</t></is></c>"
            + $"<c r=\"B{row}\" t=\"inlineStr\"><is><t>2025-03-05</t></is></c>"
            + $"<c r=\"C{row}\" t=\"inlineStr\"><is><t>A</t></is></c>"
            + $"<c r=\"D{row}\" t=\"inlineStr\"><is><t>Synthetic</t></is></c>"
            + $"<c r=\"E{row}\" t=\"inlineStr\"><is><t>Normal</t></is></c>"
            + $"<c r=\"F{row}\"><v>1</v></c><c r=\"G{row}\" s=\"1\"><v>{serial}</v></c></row>";
        var path = new RawXlsxBuilder()
            .WithStyles("""<cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="14" applyNumberFormat="1"/></cellXfs>""")
            .AddSheet("Sheet1", $"<sheetData><row r=\"1\">{headerXml}</row>{DataRow(2, bad)}{DataRow(3, good)}</sheetData>")
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

    private static void WriteBiff2(string path, string[] header, double bad, double good, bool date1904)
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
        Record(0x0000, data => { data.Write((ushort)0); data.Write((ushort)3); data.Write((ushort)0); data.Write((ushort)7); });
        for (ushort column = 0; column < header.Length; column++) Label(0, column, header[column]);
        for (ushort row = 1; row <= 2; row++)
        {
            string[] values = [$"V{row}", "2025-03-05", "A", "Synthetic", "Normal", "1"];
            for (ushort column = 0; column < values.Length; column++) Label(row, column, values[column]);
            // BIFF2 NUMBER: 三個 attribute bytes 的第二個是 number-format index；14 為日期。
            // 來源：ExcelDataReader/Core/BinaryFormat/XlsBiffBlankCell.cs 的 Format 與 XlsBiffNumberCell.Value。
            Record(0x0003, data => { data.Write(row); data.Write((ushort)6); data.Write(new byte[] { 0, 14, 0 }); data.Write(row == 1 ? bad : good); });
        }
        Record(0x000A, _ => { });
    }
}
