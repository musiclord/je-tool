using System.Text.Json;
using ClosedXML.Excel;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 裁定 2026-11-05-07：第四步「用 Excel 配對」也能匯入工作底稿的「自動化工具-科目配對資訊」工作表。
/// 底稿的第一個工作表不是科目配對，審計員選了工作表後帶 sheetName 匯入；GL 有、TB 沒有的科目在底稿寫「Not in TB」，
/// 匯入時當成沒有科目名稱，不存成名稱。
/// </summary>
public sealed class AccountMappingWorkingPaperSheetTests
{
    [Theory]
    [InlineData("sqlite")]
    public async Task WorkingPaperSheet_ImportsTheChosenSheet_AndNotInTbIsNotStoredAsAName(string provider)
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("V-1", "2025-01-01", "c", "Cash", "Synthetic", 100, 1)
            .AddRow("V-1", "2025-01-01", "g", "GL only", "Synthetic", 100, 0), databaseProvider: provider,
            validateForDownstream: true);
        var path = WorkingPaperLikeWorkbook();
        try
        {
            var imported = await host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new
            {
                filePath = path, fileName = "合成_WorkingPaper.xlsx", sheetName = WorkingPaperSheetCatalog.AccountMapping,
            }));

            Assert.Equal(2, imported.GetProperty("rowCount").GetInt32());
            Assert.Equal(JsonValueKind.Null, imported.GetProperty("columnMappingWarning").ValueKind);
            Assert.Equal(["GL_NUMBER", "GL_NAME", "STANDARDIZED_ACCOUNT_NAME"],
                imported.GetProperty("columns").EnumerateArray().Select(column => column.GetString()).ToArray());
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }

        var preview = await host.DispatchAsync("query.dataPreview", JsonSerializer.Serialize(new { dataset = "accountMapping" }));
        var columns = preview.GetProperty("columns").EnumerateArray().Select(column => column.GetString()).ToList();
        var names = preview.GetProperty("rows").EnumerateArray()
            .ToDictionary(
                row => row[columns.IndexOf("accountNumber")].GetString()!,
                row => row[columns.IndexOf("accountName")].ValueKind == JsonValueKind.Null
                    ? null
                    : row[columns.IndexOf("accountName")].GetString());
        Assert.Equal("Cash", names["c"]);
        Assert.Null(names["g"]);
    }

    // 文字檔沒有工作表，帶 sheetName 時和其他匯入一樣以 sheet_name_not_applicable 拒絕。
    [Fact]
    public async Task SheetName_OnATextFile_IsRejected()
    {
        using var host = new HandlerTestHost();
        await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("V-1", "2025-01-01", "c", "Cash", "Synthetic", 100, 1)
            .AddRow("V-1", "2025-01-01", "g", "GL only", "Synthetic", 100, 0), validateForDownstream: true);
        var path = Path.Combine(Path.GetTempPath(), $"jet-account-mapping-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, "GL_NUMBER,GL_NAME,STANDARDIZED_ACCOUNT_NAME\nc,Cash,Cash\n");
        try
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
                "import.accountMapping.fromFile",
                JsonSerializer.Serialize(new { filePath = path, sheetName = WorkingPaperSheetCatalog.AccountMapping })));
            Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>第一個工作表是封面，第二個是和 JET 工作底稿同名、同欄名的科目配對工作表。</summary>
    private static string WorkingPaperLikeWorkbook()
    {
        var path = TestWorkbookBuilder.WriteWorkbook(cover =>
        {
            cover.Name = "合成封面";
            cover.Cell(1, 1).Value = "合成底稿標題";
            cover.Cell(2, 1).Value = "合成";
        });
        using var workbook = new XLWorkbook(path);
        var sheet = workbook.AddWorksheet(WorkingPaperSheetCatalog.AccountMapping);
        sheet.Cell(1, 1).Value = "GL_NUMBER"; sheet.Cell(1, 2).Value = "GL_NAME"; sheet.Cell(1, 3).Value = "STANDARDIZED_ACCOUNT_NAME";
        sheet.Cell(2, 1).Value = "c"; sheet.Cell(2, 2).Value = "Cash"; sheet.Cell(2, 3).Value = "Cash";
        sheet.Cell(3, 1).Value = "g"; sheet.Cell(3, 2).Value = AccountMappingExportRow.NotInTbName; sheet.Cell(3, 3).Value = "Others";
        workbook.Save();
        return path;
    }
}
