using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// import.inspectFile 與 host.selectFiles 的契約測試（manifest 為驗收基準）。
/// inspectFile 不需 active project——所有測試刻意不呼叫 project.create。
/// </summary>
public sealed class InspectAndSelectFilesHandlerTests
{
    [Fact]
    public async Task InspectFile_Xlsx_ListsWorksheetsWithNormalizedColumns()
    {
        using var host = new HandlerTestHost();

        // 三個工作表：一般、含重複欄名（正規化 _2 字尾）、空表（columns 空陣列）
        var path = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Name = "Q1";
            ws.Cell(1, 1).Value = "傳票號碼";
            ws.Cell(1, 2).Value = "金額";
            ws.Cell(2, 1).Value = "D1";

            var q2 = ws.Workbook.Worksheets.Add("Q2");
            q2.Cell(1, 1).Value = "金額";
            q2.Cell(1, 2).Value = "金額";
            q2.Cell(2, 1).Value = "1";

            ws.Workbook.Worksheets.Add("空表");
        });

        try
        {
            var data = await host.DispatchAsync(
                "import.inspectFile", JsonSerializer.Serialize(new { filePath = path }));

            Assert.Equal("xlsx", data.GetProperty("fileType").GetString());
            Assert.Equal(JsonValueKind.Null, data.GetProperty("columns").ValueKind);
            Assert.Equal(JsonValueKind.Null, data.GetProperty("encoding").ValueKind);

            var worksheets = data.GetProperty("worksheets").EnumerateArray().ToList();
            Assert.Equal(3, worksheets.Count);
            Assert.Equal("Q1", worksheets[0].GetProperty("name").GetString());
            Assert.Equal("傳票號碼", worksheets[0].GetProperty("columns")[0].GetString());

            // 重複欄名走 TabularHeaderNormalizer（與匯入同規則）：金額 / 金額_2
            Assert.Equal("金額_2", worksheets[1].GetProperty("columns")[1].GetString());
            Assert.Equal(0, worksheets[2].GetProperty("columns").GetArrayLength());
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task InspectFile_Xlsx_ReportsRowCountEstimateFromDimension()
    {
        using var host = new HandlerTestHost();

        // ClosedXML 會寫 dimension：估計值 = 末列 − 標頭列 = 精確資料列數；
        // 手刻無 dimension 的活頁簿 → null（manifest：估計值 nullable、僅顯示用）
        var withDimension = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "傳票號碼";
            ws.Cell(2, 1).Value = "D1";
            ws.Cell(3, 1).Value = "D2";
            ws.Cell(4, 1).Value = "D3";
        });

        var withoutDimension = new RawXlsxBuilder()
            .AddSheet("Sheet1",
                """
                <sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>傳票號碼</t></is></c></row>
                <row r="2"><c r="A2"><v>1</v></c></row>
                </sheetData>
                """)
            .Save();

        try
        {
            var data = await host.DispatchAsync(
                "import.inspectFile", JsonSerializer.Serialize(new { filePath = withDimension }));
            Assert.Equal(3, data.GetProperty("worksheets")[0].GetProperty("rowCountEstimate").GetInt32());

            var rawData = await host.DispatchAsync(
                "import.inspectFile", JsonSerializer.Serialize(new { filePath = withoutDimension }));
            Assert.Equal(
                JsonValueKind.Null,
                rawData.GetProperty("worksheets")[0].GetProperty("rowCountEstimate").ValueKind);
        }
        finally
        {
            TestWorkbookBuilder.Delete(withDimension);
            File.Delete(withoutDimension);
        }
    }

    [Fact]
    public async Task InspectFile_Big5Csv_ReportsDetectedEncodingAndDelimiter()
    {
        using var host = new HandlerTestHost();

        var path = TestCsvBuilder.WriteFile(
            "傳票號碼,金額\nD1,100\n", TestCsvBuilder.Big5, ".txt");

        try
        {
            var data = await host.DispatchAsync(
                "import.inspectFile", JsonSerializer.Serialize(new { filePath = path }));

            // 偵測結果可直接回填為 import.*.fromFile 的覆寫參數（manifest inspectFile 細節）
            Assert.Equal("csv", data.GetProperty("fileType").GetString());
            Assert.Equal("big5", data.GetProperty("encoding").GetString());
            Assert.Equal(",", data.GetProperty("delimiter").GetString());
            Assert.Equal("傳票號碼", data.GetProperty("columns")[0].GetString());
            Assert.Equal(JsonValueKind.Null, data.GetProperty("worksheets").ValueKind);
        }
        finally
        {
            TestCsvBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task InspectFile_MissingFile_ThrowsFileNotFound()
    {
        using var host = new HandlerTestHost();

        var ex = await Assert.ThrowsAsync<JetActionException>(
            () => host.DispatchAsync("import.inspectFile", """{ "filePath": "C:\\nope.xlsx" }"""));

        Assert.Equal(JetErrorCodes.FileNotFound, ex.Code);
    }

    [Fact]
    public async Task InspectFile_UnsupportedExtension_ThrowsUnsupportedFileType()
    {
        using var host = new HandlerTestHost();

        var xlsPath = Path.Combine(Path.GetTempPath(), $"jet-{Guid.NewGuid():N}.xls");
        await File.WriteAllTextAsync(xlsPath, "legacy format");
        try
        {
            var ex = await Assert.ThrowsAsync<JetActionException>(
                () => host.DispatchAsync(
                    "import.inspectFile", JsonSerializer.Serialize(new { filePath = xlsPath })));

            Assert.Equal(JetErrorCodes.UnsupportedFileType, ex.Code);
        }
        finally
        {
            File.Delete(xlsPath);
        }
    }

    [Fact]
    public async Task SelectFiles_Cancelled_ReturnsEmptyArray()
    {
        // HandlerTestHost 預設 stub 模擬使用者取消（回空清單）
        using var host = new HandlerTestHost();

        var data = await host.DispatchAsync("host.selectFiles", """{ "title": "選擇來源" }""");

        Assert.Equal(0, data.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public async Task SelectFiles_ReturnsSelectedFilesWithNames()
    {
        var shell = new MultiPickHostShell([@"C:\data\q1.csv", @"C:\data\q2.csv"]);
        using var host = new HandlerTestHost(shell);

        var data = await host.DispatchAsync(
            "host.selectFiles", """{ "extensions": [".csv"] }""");

        var files = data.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(2, files.Count);
        Assert.Equal(@"C:\data\q1.csv", files[0].GetProperty("filePath").GetString());
        Assert.Equal("q2.csv", files[1].GetProperty("fileName").GetString());
        Assert.Equal([".csv"], shell.ReceivedExtensions);
    }

    /// <summary>host boundary 的手寫 recording stub（jet-testing skill §1）。</summary>
    private sealed class MultiPickHostShell(IReadOnlyList<string> filePaths) : IHostShell
    {
        public IReadOnlyList<string>? ReceivedExtensions { get; private set; }

        public Task<string?> PickOpenFileAsync(
            string title, IReadOnlyList<string> extensions, CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<IReadOnlyList<string>> PickOpenFilesAsync(
            string title, IReadOnlyList<string> extensions, CancellationToken cancellationToken)
        {
            ReceivedExtensions = extensions;
            return Task.FromResult(filePaths);
        }

        public Task<string?> PickSavePathAsync(string baseFileName, CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>(null);
        }

        public Task RevealInExplorerAsync(string path, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public void RequestExit()
        {
        }
    }
}
