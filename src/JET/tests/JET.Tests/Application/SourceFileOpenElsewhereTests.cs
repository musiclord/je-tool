using System.Text.Json;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 審計員常在 Excel 開著來源檔時選它匯入（裁定 2026-11-06-12）。Excel 開檔時自己能寫入、只准別人讀，
/// 這裡用同樣的開檔方式佔住檔案：JET 照樣讀到最後一次存檔的內容，匯入回應多一則提醒，說尚未存檔的修改沒有匯入。
/// 其他程式獨占檔案、連讀都不准時照舊擋下，那一組在 TbImportBranchTests。這些是新增的測試。
/// </summary>
public sealed class SourceFileOpenElsewhereTests
{
    private const string GlHeader = "傳票號碼,總帳日期,科目代號,金額\n";

    [Fact]
    public void OpenRead_WhileHeldLikeExcel_ReadsTheSavedContent()
    {
        var path = WriteTempText("held.csv", "科目代號,科目名稱\n1000,合成資產\n");
        try
        {
            using (HoldLikeExcel(path))
            using (var reader = new StreamReader(SourceFileAccess.OpenRead(path)))
            {
                Assert.Equal("科目代號,科目名稱\n1000,合成資產\n", reader.ReadToEnd());
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsOpenElsewhere_OnlyWhileAnotherProgramCanWrite()
    {
        var path = WriteTempText("probe.csv", "a\n");
        try
        {
            Assert.False(SourceFileAccess.IsOpenElsewhere(path));
            using (HoldLikeExcel(path))
            {
                Assert.True(SourceFileAccess.IsOpenElsewhere(path));
            }

            // 只讀不寫的程式（例如預覽窗格）改不了檔案，不算開著。
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                Assert.False(SourceFileAccess.IsOpenElsewhere(path));
            }

            Assert.False(SourceFileAccess.IsOpenElsewhere(path));
            Assert.False(SourceFileAccess.IsOpenElsewhere(Path.Combine(Path.GetDirectoryName(path)!, "missing.csv")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task GlImport_OneOfTwoSourcesHeldLikeExcel_ImportsBothAndWarnsOnlyForThatFile()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectName = "合成開檔提醒", periodStart = "2025-01-01", periodEnd = "2025-12-31"
        }));
        var held = await WriteCsvAsync(host, "gl-held.csv", "V1,2025-06-01,1000,10\nV1,2025-06-01,4000,-10\n");
        var free = await WriteCsvAsync(host, "gl-free.csv", "V2,2025-07-01,1000,5\nV2,2025-07-01,4000,-5\n");

        JsonElement imported;
        using (HoldLikeExcel(held))
        {
            imported = await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
            {
                sources = new object[] { new { filePath = held }, new { filePath = free } }
            }));
        }

        Assert.Equal(4, imported.GetProperty("rowCount").GetInt32());
        Assert.Equal([OpenElsewhere("gl-held.csv")], Warnings(imported));
        Assert.Equal([OpenElsewhere("gl-held.csv")], OpenElsewhereWarnings(imported));
    }

    /// <summary>
    /// 畫面把開著檔案的提醒放在匯入結果旁，其他提醒照舊只進訊息面板，所以回應要分得出是哪一種：
    /// 追加同一個檔案時，重複來源的提醒在 warnings 裡，不在 openElsewhereWarnings 裡。
    /// </summary>
    [Fact]
    public async Task GlAppend_HeldDuplicateSource_ListsOnlyTheOpenFileReminderSeparately()
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectName = "合成開檔與重複來源", periodStart = "2025-01-01", periodEnd = "2025-12-31"
        }));
        var path = await WriteCsvAsync(host, "gl-repeat.csv", "V1,2025-06-01,1000,10\nV1,2025-06-01,4000,-10\n");
        var first = await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
        {
            sources = new object[] { new { filePath = path } }
        }));
        Assert.Empty(OpenElsewhereWarnings(first));

        JsonElement appended;
        using (HoldLikeExcel(path))
        {
            appended = await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
            {
                mode = "append",
                sources = new object[] { new { filePath = path } }
            }));
        }

        Assert.Equal(4, appended.GetProperty("rowCount").GetInt32());
        var warnings = Warnings(appended);
        Assert.Equal(2, warnings.Length);
        Assert.Contains(OpenElsewhere("gl-repeat.csv"), warnings);
        Assert.Contains(warnings, warning => warning.EndsWith("請確認是否重複匯入。", StringComparison.Ordinal));
        Assert.Equal([OpenElsewhere("gl-repeat.csv")], OpenElsewhereWarnings(appended));
    }

    /// <summary>使用者回報的情境：第四步「用 Excel 配對」選到 Excel 正開著的檔案，先列工作表再匯入，兩步都要讀得到。</summary>
    [Fact]
    public async Task AccountMappingImport_WorkbookHeldLikeExcel_ListsSheetsImportsAndWarns()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "科目代號";
            sheet.Cell(1, 2).Value = "科目名稱";
            sheet.Cell(1, 3).Value = "標準化分類";
            sheet.Cell(2, 1).Value = "1000";
            sheet.Cell(2, 2).Value = "合成資產";
            sheet.Cell(2, 3).Value = "Cash";
            sheet.Cell(3, 1).Value = "4000";
            sheet.Cell(3, 2).Value = "合成收入";
            sheet.Cell(3, 3).Value = "Revenue";
        });
        try
        {
            JsonElement inspected;
            JsonElement heldImport;
            using (HoldLikeExcel(path))
            {
                inspected = await host.DispatchAsync("import.inspectFile", JsonSerializer.Serialize(new { filePath = path }));
                heldImport = await ImportAccountMappingAsync(host, path);
            }

            var sheet = Assert.Single(inspected.GetProperty("worksheets").EnumerateArray());
            Assert.Equal("Sheet1", sheet.GetProperty("name").GetString());
            Assert.Equal(["科目代號", "科目名稱", "標準化分類"],
                sheet.GetProperty("columns").EnumerateArray().Select(column => column.GetString()));
            Assert.Equal(2, heldImport.GetProperty("rowCount").GetInt32());
            Assert.Equal([OpenElsewhere("synthetic-account-mapping.xlsx")], Warnings(heldImport));
            Assert.Equal([OpenElsewhere("synthetic-account-mapping.xlsx")], OpenElsewhereWarnings(heldImport));

            var freeImport = await ImportAccountMappingAsync(host, path);
            Assert.Equal(2, freeImport.GetProperty("rowCount").GetInt32());
            Assert.Empty(Warnings(freeImport));
            Assert.Empty(OpenElsewhereWarnings(freeImport));
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task AuthorizedPreparerImport_WorkbookHeldLikeExcel_ImportsAndWarns()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            sheet.Cell(1, 1).Value = "AUTHORIZED_PREPARER";
            sheet.Cell(2, 1).Value = "王小明";
            sheet.Cell(3, 1).Value = "李大華";
        });
        try
        {
            JsonElement heldImport;
            using (HoldLikeExcel(path))
            {
                heldImport = await ImportAuthorizedPreparerAsync(host, path);
            }

            Assert.Equal(2, heldImport.GetProperty("rowCount").GetInt32());
            Assert.Equal([OpenElsewhere("authorized-preparers.xlsx")], Warnings(heldImport));
            Assert.Equal([OpenElsewhere("authorized-preparers.xlsx")], OpenElsewhereWarnings(heldImport));

            var freeImport = await ImportAuthorizedPreparerAsync(host, path);
            Assert.Equal(2, freeImport.GetProperty("rowCount").GetInt32());
            Assert.Empty(Warnings(freeImport));
            Assert.Empty(OpenElsewhereWarnings(freeImport));
        }
        finally
        {
            TestWorkbookBuilder.Delete(path);
        }
    }

    [Theory]
    [InlineData("import.holiday.fromFile", ReportTemplateCatalog.Holiday)]
    [InlineData("import.makeupDay.fromFile", ReportTemplateCatalog.MakeUpDay)]
    public async Task CalendarImport_TemplateHeldLikeExcel_ImportsTheSameDaysAndWarns(string action, string templateName)
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);
        var path = Path.Combine(host.ProjectsRoot, templateName);
        await using (var output = File.Create(path))
        {
            await ReportTemplateCatalog.Default.CopyToAsync(templateName, output, CancellationToken.None);
        }

        JsonElement heldImport;
        using (HoldLikeExcel(path))
        {
            heldImport = await host.DispatchAsync(action, JsonSerializer.Serialize(new { filePath = path }));
        }

        var freeImport = await host.DispatchAsync(action, JsonSerializer.Serialize(new { filePath = path }));
        Assert.True(heldImport.GetProperty("count").GetInt32() > 0);
        Assert.Equal(freeImport.GetProperty("count").GetInt32(), heldImport.GetProperty("count").GetInt32());
        Assert.Equal([OpenElsewhere(templateName)], Warnings(heldImport));
        Assert.Equal([OpenElsewhere(templateName)], OpenElsewhereWarnings(heldImport));
        Assert.Empty(Warnings(freeImport));
        Assert.Empty(OpenElsewhereWarnings(freeImport));
    }

    /// <summary>和 ImportMessages.SourceFileOpenElsewhere 同一句，這裡逐字寫出來當獨立的預期值。</summary>
    private static string OpenElsewhere(string fileName) =>
        $"檔案「{fileName}」正被其他程式開啟（例如 Excel）。JET 匯入的是這個檔案最後一次存檔的內容，尚未存檔的修改沒有匯入。";

    /// <summary>Excel 開著檔案時的開檔方式：自己可以寫入，只准別人讀。</summary>
    private static FileStream HoldLikeExcel(string path) =>
        new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

    private static string[] Warnings(JsonElement response) =>
        response.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()!).ToArray();

    private static string[] OpenElsewhereWarnings(JsonElement response) =>
        response.GetProperty("openElsewhereWarnings").EnumerateArray().Select(warning => warning.GetString()!).ToArray();

    private static string WriteTempText(string fileName, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"jet-open-elsewhere-{Guid.NewGuid():N}-{fileName}");
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<string> WriteCsvAsync(HandlerTestHost host, string fileName, string rows)
    {
        var path = Path.Combine(host.ProjectsRoot, fileName);
        await File.WriteAllTextAsync(path, GlHeader + rows, TestCsvBuilder.Utf8Bom);
        return path;
    }

    private static Task<JsonElement> ImportAccountMappingAsync(HandlerTestHost host, string path) =>
        host.DispatchAsync("import.accountMapping.fromFile", JsonSerializer.Serialize(new
        {
            filePath = path, fileName = "synthetic-account-mapping.xlsx", sheetName = "Sheet1"
        }));

    private static Task<JsonElement> ImportAuthorizedPreparerAsync(HandlerTestHost host, string path) =>
        host.DispatchAsync("import.authorizedPreparer.fromFile", JsonSerializer.Serialize(new
        {
            filePath = path, fileName = "authorized-preparers.xlsx", sourceColumn = "AUTHORIZED_PREPARER"
        }));
}
