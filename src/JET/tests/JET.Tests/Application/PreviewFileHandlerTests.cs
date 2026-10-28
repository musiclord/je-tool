using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// import.previewFile 契約測試（manifest 為驗收基準）。不需 active project——刻意不呼叫 project.create。
/// </summary>
public sealed class PreviewFileHandlerTests
{
    [Fact]
    public async Task PreviewFile_Xlsx_ReturnsHeaderAndBoundedSampleRows()
    {
        using var host = new HandlerTestHost();

        var path = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Name = "Q1";
            ws.Cell(1, 1).Value = "傳票號碼";
            ws.Cell(1, 2).Value = "金額";
            for (var r = 2; r <= 15; r++)
            {
                ws.Cell(r, 1).Value = "D" + r;
                ws.Cell(r, 2).Value = r * 100;
            }
        });

        try
        {
            var data = await host.DispatchAsync(
                "import.previewFile",
                JsonSerializer.Serialize(new { filePath = path, sheetName = "Q1", limit = 10 }));

            Assert.Equal("傳票號碼", data.GetProperty("columns")[0].GetString());
            Assert.Equal("金額", data.GetProperty("columns")[1].GetString());

            var rows = data.GetProperty("sampleRows").EnumerateArray().ToList();
            Assert.Equal(10, rows.Count); // limit 夾擠：14 列資料只回前 10 列
            Assert.Equal("D2", rows[0][0].GetString());
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task PreviewFile_HeaderlessLookingFile_ExposesFirstRowAsHeader()
    {
        // 無標頭檔：第一列其實是資料 → 會被當成 columns，讓使用者一眼看出沒有標頭列。
        using var host = new HandlerTestHost();
        var path = TestCsvBuilder.WriteFile("1001,現金,5000\n1002,銀行,8000\n", TestCsvBuilder.Utf8NoBom, ".csv");

        try
        {
            var data = await host.DispatchAsync(
                "import.previewFile", JsonSerializer.Serialize(new { filePath = path }));

            // 第一列被當成標頭
            Assert.Equal("1001", data.GetProperty("columns")[0].GetString());
            // 其餘列為 sampleRows
            var rows = data.GetProperty("sampleRows").EnumerateArray().ToList();
            Assert.Equal("1002", rows[0][0].GetString());
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task PreviewFile_SparseRow_FillsMissingCellsWithNull()
    {
        using var host = new HandlerTestHost();
        var path = TestCsvBuilder.WriteFile("代號,名稱,金額\nA,,100\n", TestCsvBuilder.Utf8NoBom, ".csv");

        try
        {
            var data = await host.DispatchAsync(
                "import.previewFile", JsonSerializer.Serialize(new { filePath = path }));

            var firstRow = data.GetProperty("sampleRows")[0];
            Assert.Equal("A", firstRow[0].GetString());
            Assert.Equal(JsonValueKind.Null, firstRow[1].ValueKind); // 空 cell → null
            Assert.Equal("100", firstRow[2].GetString());
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task PreviewFile_EmptyCsv_ThrowsEmptyWorkbook()
    {
        // 空白/無標頭 CSV：讀取鏈找不到標頭列 → empty_workbook（manifest 錯誤碼約定）。
        using var host = new HandlerTestHost();
        var path = TestCsvBuilder.WriteFile("   \n", TestCsvBuilder.Utf8NoBom, ".csv");

        try
        {
            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => host.DispatchAsync("import.previewFile", JsonSerializer.Serialize(new { filePath = path })));

            Assert.Equal(JetErrorCodes.EmptyWorkbook, ex.Code);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task PreviewFile_MissingFile_ThrowsFileNotFound()
    {
        using var host = new HandlerTestHost();

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("import.previewFile", """{ "filePath": "C:\\nope.xlsx" }"""));

        Assert.Equal(JetErrorCodes.FileNotFound, ex.Code);
    }

    [Fact]
    public async Task PreviewFile_EncodingOnXlsx_ThrowsInvalidPayload()
    {
        using var host = new HandlerTestHost();
        var path = TestWorkbookBuilder.WriteWorkbook(ws => { ws.Cell(1, 1).Value = "A"; ws.Cell(2, 1).Value = "1"; });

        try
        {
            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => host.DispatchAsync(
                    "import.previewFile",
                    JsonSerializer.Serialize(new { filePath = path, encoding = "big5" })));

            Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    // 預篩選收斂計畫階段六：使用者 2026-10-22 裁定「欄名在第幾列」開放給 GL 與 TB 的一般匯入，Excel 與文字檔都適用。
    // 預覽帶 headerRow 時，從那一列起找第一個有內容的列當欄名，前面的標題列與空白列都不算資料。
    // 第一次失敗：Focused 收據 20261023-193734239-f6249c3be1e046cfa0c55b4181d2cabd（headerRow 被忽略，標題列被當成欄名）。
    [Fact]
    public async Task PreviewFile_HeaderRow_UsesThatRowAsHeader_ForXlsxAndCsv()
    {
        using var host = new HandlerTestHost();
        var xlsxPath = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "合成公司 總帳明細";
            ws.Cell(3, 1).Value = "傳票號碼";
            ws.Cell(3, 2).Value = "金額";
            ws.Cell(4, 1).Value = "V1";
            ws.Cell(4, 2).Value = 10;
            ws.Cell(5, 1).Value = "V2";
            ws.Cell(5, 2).Value = 20;
        });
        var csvPath = TestCsvBuilder.WriteFile("合成公司 總帳明細\n\n傳票號碼,金額\nV1,10\n", TestCsvBuilder.Utf8NoBom);

        try
        {
            var xlsx = await host.DispatchAsync(
                "import.previewFile",
                JsonSerializer.Serialize(new { filePath = xlsxPath, sheetName = "Sheet1", headerRow = 3 }));
            Assert.Equal(["傳票號碼", "金額"], xlsx.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToArray());
            var xlsxRows = xlsx.GetProperty("sampleRows").EnumerateArray().ToList();
            Assert.Equal(2, xlsxRows.Count);
            Assert.Equal("V1", xlsxRows[0][0].GetString());

            var csv = await host.DispatchAsync(
                "import.previewFile",
                JsonSerializer.Serialize(new { filePath = csvPath, headerRow = 3 }));
            Assert.Equal(["傳票號碼", "金額"], csv.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToArray());
            Assert.Equal("V1", Assert.Single(csv.GetProperty("sampleRows").EnumerateArray())[0].GetString());

            // 不帶 headerRow 時維持原本的行為：第一個有內容的列是欄名，所以標題列會被當成欄名。
            var plain = await host.DispatchAsync("import.previewFile", JsonSerializer.Serialize(new { filePath = csvPath }));
            Assert.Equal(["合成公司 總帳明細"], plain.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToArray());
        }
        finally
        {
            TestWorkbookBuilder.Delete(xlsxPath);
            TestCsvBuilder.Delete(csvPath);
        }
    }

    // Access 的資料表沒有列的概念，不適用；小於 1 的列號畫面照正常操作送不出來。兩者都走畫面送錯資料的拒絕。
    [Theory]
    [InlineData(".accdb", 2, "header_row_not_applicable")]
    [InlineData(".csv", 0, "header_row_invalid")]
    [InlineData(".xlsx", -1, "header_row_invalid")]
    public async Task PreviewFile_HeaderRowNotApplicableOrInvalid_ThrowsInvalidPayload(string extension, int headerRow, string check)
    {
        using var host = new HandlerTestHost();
        // 檢查在開檔之前，所以空檔就夠了；Access 讀取器只看副檔名。
        var path = Path.Combine(Path.GetTempPath(), $"jet-header-row-{Guid.NewGuid():N}{extension}");
        await File.WriteAllBytesAsync(path, []);

        try
        {
            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => host.DispatchAsync(
                    "import.previewFile",
                    JsonSerializer.Serialize(new { filePath = path, headerRow })));

            Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
            Assert.Equal(check, JetPayloadRejection.CheckOf(ex));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
