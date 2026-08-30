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
}
