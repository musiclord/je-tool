using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 案件裡有超過 Excel 每格 32,767 字上限的文字時，匯出照樣完成：那幾格寫到上限並在結尾註明已截斷，
/// 匯出結果的 warnings 寫出檔名、工作表與格數，案件資料裡的完整文字不變。文字都是合成的。
/// </summary>
public sealed class ExcelCellTextLimitExportTests
{
    private const int Limit = 32_767;
    private const string Notice = "（JET 已截斷：超過 Excel 每格 32,767 字的上限）";
    private static readonly string LongText = string.Concat(Enumerable.Repeat("合成摘要文字", 7_000));

    [Fact]
    public async Task ValidationReports_LongDescription_WritesUpToTheLimitAndWarnsForEachFile()
    {
        using var host = new HandlerTestHost(enableDevTools: false);
        await CreateCaseAsync(host, description: LongText, accountName: "合成資產");
        var validation = await host.DispatchAsync("validate.run");
        var resultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString()!;

        var exported = await host.DispatchAsync("export.validationReports", Json(new { resultId }));

        // 資料驗證報告一定列出 V1 的摘要；INF 報告只在抽到 V1 那一列時才有，所以只在有截斷時才預期它的提醒。
        var expected = new List<string>();
        foreach (var report in exported.GetProperty("publishedReports").EnumerateArray())
        {
            var truncated = TruncatedCellsBySheet(report.GetProperty("fullPath").GetString()!);
            if (report.GetProperty("kind").GetString() == "validationReport")
            {
                Assert.NotEmpty(truncated);
            }

            if (truncated.Count > 0)
            {
                expected.Add(ExpectedWarning(report.GetProperty("fileName").GetString()!, truncated));
            }
        }

        Assert.Equal(expected, Warnings(exported));
        var preview = await host.DispatchAsync("query.dataPreview", """{ "dataset": "gl" }""");
        Assert.Contains(
            preview.GetProperty("rows").EnumerateArray(),
            row => row.EnumerateArray().Any(cell => cell.ValueKind == JsonValueKind.String && cell.GetString() == LongText));
    }

    [Fact]
    public async Task PrescreenCriteriaAndWorkingPaper_LongDescription_WarnForEachTruncatedFile()
    {
        using var host = new HandlerTestHost(enableDevTools: false);
        await CreateCaseAsync(host, description: LongText, accountName: "合成資產");
        var validation = await host.DispatchAsync("validate.run");
        var validationResultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString();
        var prescreen = await host.DispatchAsync("prescreen.run");
        var filtered = await host.DispatchAsync(
            "filter.commit",
            """{"scenarios":[{"name":"合成借方","rationale":"合成理由","groups":[{"conditions":[{"type":"drCrOnly","drCr":"debit"}]}]}]}""");
        var revision = filtered.GetProperty("resultRef").GetProperty("revision").GetString();

        // 預篩選報告只在某些表列出 V1 的摘要，所以只核對提醒和檔案裡的截斷格子一致；篩選結果與底稿一定列出 V1 的借方分錄。
        AssertWarnsForTruncatedCells(
            await host.DispatchAsync("export.prescreenReport", Json(new
            {
                resultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString()
            })),
            mustTruncate: false);
        AssertWarnsForTruncatedCells(
            await host.DispatchAsync("export.criteriaSelectionReport", Json(new { validationResultId, revision })),
            mustTruncate: true);
        AssertWarnsForTruncatedCells(
            await host.DispatchAsync("export.workingPaperStream", Json(new
            {
                validationResultId, scenarioRevision = revision, scenarioPositions = new[] { 1 }
            })),
            mustTruncate: true);
    }

    [Fact]
    public void Warning_ListsAtMostFiveSheetsAndSumsTheRest()
    {
        var sheets = Enumerable.Range(1, 7).Select(index => new TruncatedTextCells($"合成{index}", index)).ToArray();

        Assert.Equal(
            "檔案「合成.xlsx」有 28 個儲存格的文字超過 Excel 每格 32,767 字的上限，已寫到上限並在儲存格結尾註明已截斷："
            + "工作表「合成1」1 個、工作表「合成2」2 個、工作表「合成3」3 個、工作表「合成4」4 個、工作表「合成5」5 個，"
            + "另有 2 張工作表共 13 個。JET 案件資料裡的完整文字不變。",
            ReportMessages.TruncatedCellText("合成.xlsx", sheets));
        Assert.Null(ReportMessages.TruncatedCellText("合成.xlsx", []));
    }

    private static void AssertWarnsForTruncatedCells(JsonElement exported, bool mustTruncate)
    {
        var report = exported.GetProperty("publishedReport");
        var truncated = TruncatedCellsBySheet(report.GetProperty("fullPath").GetString()!);
        if (mustTruncate)
        {
            Assert.NotEmpty(truncated);
        }

        string[] expected = truncated.Count == 0 ? [] : [ExpectedWarning(report.GetProperty("fileName").GetString()!, truncated)];
        Assert.Equal(expected, Warnings(exported));
    }

    /// <summary>給使用者看的句子逐字寫在這裡，當成獨立的預期值；這裡的檔案都不超過五張工作表。</summary>
    private static string ExpectedWarning(string fileName, IReadOnlyList<(string Sheet, int Count)> sheets) =>
        $"檔案「{fileName}」有 {sheets.Sum(sheet => sheet.Count)} 個儲存格的文字超過 Excel 每格 32,767 字的上限，"
        + "已寫到上限並在儲存格結尾註明已截斷："
        + string.Join("、", sheets.Select(sheet => $"工作表「{sheet.Sheet}」{sheet.Count} 個"))
        + "。JET 案件資料裡的完整文字不變。";

    /// <summary>
    /// 依活頁簿的工作表順序，數出結尾是已截斷註明的格子。每個格子寫出的文字都不超過上限，
    /// 截斷的格子是原文的開頭加上註明。
    /// </summary>
    private static List<(string Sheet, int Count)> TruncatedCellsBySheet(string path)
    {
        using var document = SpreadsheetDocument.Open(path, isEditable: false);
        var workbook = document.WorkbookPart!;
        var sharedItems = JET.Tests.Infrastructure.WrittenCellText.SharedItems(workbook);
        var result = new List<(string, int)>();
        foreach (var sheet in workbook.Workbook!.Sheets!.Elements<Sheet>())
        {
            var part = (WorksheetPart)workbook.GetPartById(sheet.Id!.Value!);
            var count = 0;
            foreach (var cell in part.Worksheet!.Descendants<Cell>())
            {
                if (JET.Tests.Infrastructure.WrittenCellText.Of(cell, sharedItems) is not { } written)
                {
                    continue;
                }

                Assert.True(written.Length <= Limit, $"{sheet.Name} {cell.CellReference} 寫出 {written.Length} 字");
                var text = ReportTextEncodingTests.Decode(written);
                if (text.EndsWith(Notice, StringComparison.Ordinal))
                {
                    Assert.StartsWith(text[..^Notice.Length], LongText, StringComparison.Ordinal);
                    count++;
                }
            }

            if (count > 0)
            {
                result.Add((sheet.Name!.Value!, count));
            }
        }

        return result;
    }

    /// <summary>V1 借方 10、貸方 9 借貸不平，報告會列出它；科目 1000 的名稱與 V1 的摘要用呼叫端給的文字。</summary>
    private static async Task CreateCaseAsync(HandlerTestHost host, string description, string accountName)
    {
        await host.DispatchAsync("project.create", Json(new
        {
            projectName = "合成超長文字", periodStart = "2025-01-01", periodEnd = "2025-12-31"
        }));
        var gl = new StringBuilder()
            .Append("傳票號碼,傳票日期,核准日期,科目代號,科目名稱,摘要,建立人員,金額,借方旗標\n")
            .Append($"V1,2025-06-01,2025-06-02,1000,{accountName},{description},合成人員,10,1\n")
            .Append("V1,2025-06-01,2025-06-02,4000,合成收入,合成貸方,合成人員,9,0\n");
        var glPath = Path.Combine(host.ProjectsRoot, "long-gl.csv");
        await File.WriteAllTextAsync(glPath, gl.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await host.DispatchAsync("import.gl.fromFile", Json(new { filePath = glPath, encoding = "utf-8", delimiter = "," }));

        var tb = new StringBuilder()
            .Append("科目代號,科目名稱,變動金額\n")
            .Append($"1000,{accountName},10\n")
            .Append("4000,合成收入,-9\n");
        var tbPath = Path.Combine(host.ProjectsRoot, "long-tb.csv");
        await File.WriteAllTextAsync(tbPath, tb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await host.DispatchAsync("import.tb.fromFile", Json(new { filePath = tbPath, encoding = "utf-8", delimiter = "," }));

        await host.DispatchAsync("mapping.commit.gl", Json(new
        {
            mapping = new Dictionary<string, string>
            {
                ["voucherNumber"] = "傳票號碼", ["glDate"] = "傳票日期", ["approvalDate"] = "核准日期", ["accountNumber"] = "科目代號",
                ["accountName"] = "科目名稱", ["description"] = "摘要", ["createdBy"] = "建立人員", ["amount"] = "金額",
                ["drCr"] = "借方旗標", ["drCrDebitCode"] = "1", ["drCrCreditCode"] = "0",
            },
            amountMode = "flag",
            manualAutoPolicy = new { manualValues = new[] { "true", "1" }, automaticValues = new[] { "false", "0" }, blankValueKind = "reject" },
        }));
        await host.DispatchAsync("mapping.commit.tb", Json(new
        {
            mapping = InlineTbWorkbookBuilder.BuildDirectModeMapping(),
            changeMode = "direct",
        }));
    }

    private static string[] Warnings(JsonElement response) =>
        response.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()!).ToArray();

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
