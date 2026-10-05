using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch9ExcelDateWorkflowTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var provider in new[] { "sqlite", "duckdb" })
        foreach (var format in new[] { "csv", "xlsx", "xlsx1904", "xls", "xls1904" })
        foreach (var field in new[] { "postDate", "docDate", "voucherDate", "rde" }) yield return [provider, format, field];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NativeSerialOutsideYearRange_IsARowErrorInEveryDateRole(string provider, string format, string field)
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
                Assert.Equal("2024-01-01", rows[1].Values["NativeDate"]);
            }
            using var host = new HandlerTestHost();
            await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
            { caseName = "Native date boundary", periodStart = "2024-01-01", periodEnd = "2025-12-31", databaseProvider = provider }));
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            var mapping = Batch9MappingTestFixture.Mapping();
            object[] rde = [];
            if (field == "rde") rde = [new { sourceColumn = "NativeDate", label = "Native date", valueType = "date" }];
            else mapping[field] = "NativeDate";
            var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("mapping.commit.gl",
                JsonSerializer.Serialize(new { mapping, amountMode = "signed", rdeFields = rde })));
            Assert.Equal(JetErrorCodes.ProjectionFailed, error.Code);
            Assert.StartsWith("1 列無法轉換", error.Message, StringComparison.Ordinal);
            var detail = Assert.Single(error.Details!);
            Assert.Equal("NativeDate", detail.SourceColumn);
            Assert.Contains("第 2 列", detail.Message, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("sqlite", "xlsx")]
    [InlineData("duckdb", "xlsx")]
    [InlineData("sqlite", "xlsx1904")]
    [InlineData("duckdb", "xlsx1904")]
    [InlineData("sqlite", "xls")]
    [InlineData("duckdb", "xls")]
    [InlineData("sqlite", "xls1904")]
    [InlineData("duckdb", "xls1904")]
    public async Task SevenDigitNativeSerial_CannotFallBackToRocText(string provider, string format)
    {
        var path = WriteSource(format, badOaSerial: 1_140_611);
        try
        {
            using var host = new HandlerTestHost();
            await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
            { caseName = "Native serial is not ROC text", periodStart = "2024-01-01", periodEnd = "2025-12-31", databaseProvider = provider }));
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            var mapping = Batch9MappingTestFixture.Mapping();
            mapping["postDate"] = "NativeDate";
            var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("mapping.commit.gl",
                JsonSerializer.Serialize(new { mapping, amountMode = "signed" })));
            Assert.Equal(JetErrorCodes.ProjectionFailed, error.Code);
            Assert.StartsWith("1 列無法轉換", error.Message, StringComparison.Ordinal);
            var detail = Assert.Single(error.Details!);
            Assert.Equal("NativeDate", detail.SourceColumn);
            Assert.Contains("第 2 列", detail.Message, StringComparison.Ordinal);
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
            var csv = Path.Combine(Path.GetTempPath(), $"jet-batch9-date-{Guid.NewGuid():N}.csv");
            File.WriteAllText(csv, string.Join(',', header) + "\nV1,2025-03-05,A,Synthetic,Normal,1,240115\nV2,2025-03-05,A,Synthetic,Normal,1,45292\n", new UTF8Encoding(false));
            return csv;
        }
        if (format.StartsWith("xls", StringComparison.Ordinal) && !format.StartsWith("xlsx", StringComparison.Ordinal))
        {
            var xls = Path.Combine(Path.GetTempPath(), $"jet-batch9-date-{Guid.NewGuid():N}.xls");
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
            workbook.WorkbookPart!.Workbook.WorkbookProperties = new WorkbookProperties { Date1904 = true };
            workbook.WorkbookPart.Workbook.Save();
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
