using System.Text;
using System.Text.Json;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 2026-10-05 最後獨立複審 V8 與 V11：文字檔讀取在測試環境要靠支援日誌判讀。
/// V8：自動偵測編碼後，檔案前段就解碼失敗時，支援日誌要寫出實際用來解碼的編碼，不是 unknown。檢視、預覽與匯入三個入口都要。
/// V11：用引號包住、內含換行的欄位是合法寫法，匯入不擋；但引號放錯位置時後面的列會被併進同一格。
/// 匯入完成時要在提醒與支援日誌寫出這種資料的筆數與前幾個列號。列號是用 Excel 打開時的列號，也就是 JET 的來源列號。
/// 預期文字逐字寫在這裡，不由產品程式組出。
/// </summary>
public sealed class TextFileReadingDiagnosticsTests
{
    private static async Task<(HandlerTestHost Host, string ProjectId)> CreateAsync(string provider = "sqlite")
    {
        var host = new HandlerTestHost(enableDevTools: false);
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            caseName = "synthetic-text-file", periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider
        }));
        return (host, created.GetProperty("projectId").GetString()!);
    }

    private static async Task<JsonElement[]> SupportEntriesAsync(HandlerTestHost host, string projectId, string correlationId)
    {
        var export = await host.DispatchAsync("support.log.export", JsonSerializer.Serialize(new { projectId, correlationId }));
        var lines = await File.ReadAllLinesAsync(export.GetProperty("filePath").GetString()!);
        return lines.Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
    }

    /// <summary>0xA4 後面接換行，不是有效的 UTF-8，也不是有效的 Big5 雙位元組字，所以偵測選 Big5 之後，讀前段內容時就失敗。</summary>
    [Fact]
    public async Task EarlyDecodeFailure_AfterAutomaticDetection_LogsTheEncodingTried_InInspectPreviewAndImport()
    {
        var (host, projectId) = await CreateAsync();
        using var _ = host;
        var path = Path.Combine(host.ProjectsRoot, "PRIVATE_EARLY_DECODE.csv");
        await File.WriteAllBytesAsync(path,
            Encoding.ASCII.GetBytes("doc,amount\nA,1\n").Concat(new byte[] { 0xA4, 0x0A }).Concat(Encoding.ASCII.GetBytes("B,2\n")).ToArray());

        var calls = new (string Action, string Payload)[]
        {
            ("import.inspectFile", JsonSerializer.Serialize(new { filePath = path })),
            ("import.previewFile", JsonSerializer.Serialize(new { filePath = path, limit = 10 })),
            ("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path }))
        };
        foreach (var (action, payload) in calls)
        {
            var correlationId = "early-decode-" + action;
            var failure = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(action, payload, correlationId: correlationId));
            Assert.Equal(JetErrorCodes.FileReadError, failure.Code);

            var entries = await SupportEntriesAsync(host, projectId, correlationId);
            var fields = entries.Single(entry => entry.GetProperty("eventName").GetString() == "action.error").GetProperty("fields");
            Assert.Equal("decode_invalid_bytes", fields.GetProperty("failure_cause").GetString());
            Assert.Equal("big5", fields.GetProperty("encoding").GetString());
            Assert.DoesNotContain("PRIVATE_", string.Join('\n', entries.Select(entry => entry.GetRawText())));
        }
    }

    private const string TwoMultilineRecords =
        "doc,memo,amount\n" +
        "JV-1,\"line one\nline two\",1\n" +
        "JV-2,plain,2\n" +
        "JV-3,\"PRIVATE stray quote,3\n" +
        "JV-4,closes here\",4\n" +
        "JV-5,ok,5\n";

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task QuotedLineBreaks_ImportSucceeds_AndNamesCountAndRowsInWarningAndSupportLog(string provider)
    {
        var (host, projectId) = await CreateAsync(provider);
        using var _ = host;
        var path = Path.Combine(host.ProjectsRoot, "multiline.csv");
        await File.WriteAllTextAsync(path, TwoMultilineRecords, new UTF8Encoding(false));

        var imported = await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path }),
            correlationId: "multiline-import");

        // 第 4 列的欄位一路讀到下一個引號，JV-4 那一行併進同一格，所以只剩 4 筆。
        Assert.Equal(4, imported.GetProperty("rowCount").GetInt32());
        Assert.Equal(
            ["「multiline.csv」有 2 筆資料的欄位內含換行：第 2、4 列。欄位用引號包住時可以換行，不一定是錯誤。"
             + "但如果引號放錯位置，後面幾列會被併進同一格，匯入的筆數會變少。請用 Excel 打開原檔，核對這幾列。"],
            imported.GetProperty("warnings").EnumerateArray().Select(static item => item.GetString()!).ToArray());

        var entries = await SupportEntriesAsync(host, projectId, "multiline-import");
        var fields = entries.Single(entry => entry.GetProperty("eventName").GetString() == "import.multiline_fields")
            .GetProperty("fields");
        Assert.Equal(1, fields.GetProperty("source_no").GetInt32());
        Assert.Equal(2, fields.GetProperty("multiline_record_count").GetInt32());
        Assert.Equal("2,4", fields.GetProperty("multiline_record_rows").GetString());
        Assert.DoesNotContain("PRIVATE", string.Join('\n', entries.Select(entry => entry.GetRawText())));
        Assert.DoesNotContain("multiline.csv", string.Join('\n', entries.Select(entry => entry.GetRawText())));
    }

    [Fact]
    public async Task ManyQuotedLineBreaks_ListOnlyTheFirstFiveRows_AndFilesWithoutThemStayQuiet()
    {
        var (host, _) = await CreateAsync();
        using var hostScope = host;
        var many = new StringBuilder("doc,memo,amount\n");
        for (var i = 1; i <= 7; i++)
        {
            many.Append($"JV-{i},\"first\r\nsecond\",{i}\n").Append($"JV-{i}b,plain,{i}\n");
        }

        var manyPath = Path.Combine(host.ProjectsRoot, "many.csv");
        await File.WriteAllTextAsync(manyPath, many.ToString(), new UTF8Encoding(false));
        var plainPath = Path.Combine(host.ProjectsRoot, "plain.csv");
        await File.WriteAllTextAsync(plainPath, "doc,memo,amount\nJV-1,\"a, b\",1\nJV-2,3/4\",2\n", new UTF8Encoding(false));

        var imported = await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
        {
            mode = "replace",
            sources = new[] { new { filePath = plainPath }, new { filePath = manyPath } }
        }));

        Assert.Equal(16, imported.GetProperty("rowCount").GetInt32());
        Assert.Equal(
            ["「many.csv」有 7 筆資料的欄位內含換行，例如第 2、4、6、8、10 列。欄位用引號包住時可以換行，不一定是錯誤。"
             + "但如果引號放錯位置，後面幾列會被併進同一格，匯入的筆數會變少。請用 Excel 打開原檔，核對這幾列。"],
            imported.GetProperty("warnings").EnumerateArray().Select(static item => item.GetString()!).ToArray());
    }
}
