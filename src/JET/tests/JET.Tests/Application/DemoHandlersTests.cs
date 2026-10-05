using System.Text.Json;
using JET.Application;
using JET.Infrastructure;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class DemoHandlersTests
{
    [Fact]
    public async Task LoadDemo_ReturnsProjectMappingsAndCalendarSeeds()
    {
        var data = await DemoProjectPipeline.DispatchDemoFixtureAsync("project.loadDemo");

        Assert.Equal("DEMO-2025-001", data.GetProperty("project").GetProperty("projectCode").GetString());
        Assert.Equal("2025-01-01", data.GetProperty("project").GetProperty("periodStart").GetString());

        var gl = data.GetProperty("gl");
        Assert.Equal("flag", gl.GetProperty("amountMode").GetString());
        Assert.Equal("1", gl.GetProperty("mapping").GetProperty("dcDebitCode").GetString());
        Assert.Equal(DemoDataFactory.GlVoucherCount * DemoDataFactory.LinesPerVoucher, gl.GetProperty("rowCount").GetInt32());

        var tb = data.GetProperty("tb");
        Assert.Equal("debitCredit", tb.GetProperty("changeMode").GetString());
        Assert.Equal(DemoDataFactory.TbAccountCount, tb.GetProperty("rowCount").GetInt32());

        Assert.True(data.GetProperty("holidays").GetArrayLength() > 0);
        Assert.True(data.GetProperty("makeupDays").GetArrayLength() > 0);
    }

    [Fact]
    public async Task ExportGlFile_ProducesWorkbookReadableByImportReader()
    {
        var exported = await DemoProjectPipeline.DispatchDemoFixtureAsync("demo.exportGlFile");
        var filePath = exported.GetProperty("filePath").GetString();

        Assert.NotNull(filePath);
        Assert.True(File.Exists(filePath));
        Assert.Equal("JE-demo-2025.xlsx", exported.GetProperty("fileName").GetString());

        // 匯出的檔案必須能被正式 import reader 讀出（同一條 pipeline）
        var columns = await new OpenXmlSaxTableReader().ReadColumnsAsync(
            new JET.Domain.TabularSourceRequest(filePath!), CancellationToken.None);
        Assert.Equal(12, columns.Count);
        Assert.Contains("傳票號碼", columns);
        Assert.Contains("金額", columns);
        Assert.Contains("借方旗標", columns);
        // 2026-10-04 第 8 批 L21：示範來源欄依既有用途改名為「傳票日期」，欄數與 reader 檢查不變。
        // 第一次失敗：20261004-092023464-13b0a6928d5e400492fbe8a6a24eb69d。
        Assert.Contains("傳票日期", columns);
    }

    [Fact]
    public async Task ExportAuthorizedPreparerFile_WritesFileWithContractName()
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host);

        var data = await DemoProjectPipeline.DispatchDemoFixtureAsync(
            "demo.exportAuthorizedPreparerFile");

        Assert.Equal("AuthorizedPreparer-demo-2025.xlsx", data.GetProperty("fileName").GetString());
        Assert.True(File.Exists(data.GetProperty("filePath").GetString()));
    }

    [Fact]
    public async Task FullMockPipeline_CreatesImportsCommitsAndResumes()
    {
        using var host = new HandlerTestHost();

        // 1. loadDemo 提供 metadata 與 mapping（與前端 mock 按鈕相同流程）
        var demo = await DemoProjectPipeline.DispatchDemoFixtureAsync("project.loadDemo");
        var project = demo.GetProperty("project");

        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectCode = project.GetProperty("projectCode").GetString(),
            entityName = project.GetProperty("entityName").GetString(),
            operatorId = project.GetProperty("operatorId").GetString(),
            periodStart = project.GetProperty("periodStart").GetString(),
            periodEnd = project.GetProperty("periodEnd").GetString(),
            lastPeriodStart = project.GetProperty("lastPeriodStart").GetString()
        }));
        var projectId = created.GetProperty("projectId").GetString()!;

        // 2. 匯出 demo 檔案 → 走正式 file-based import
        var glFile = await DemoProjectPipeline.DispatchDemoFixtureAsync("demo.exportGlFile");
        var glImport = await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
        {
            filePath = glFile.GetProperty("filePath").GetString(),
            fileName = glFile.GetProperty("fileName").GetString()
        }));
        Assert.Equal(DemoDataFactory.GlVoucherCount * DemoDataFactory.LinesPerVoucher, glImport.GetProperty("rowCount").GetInt32());

        var tbFile = await DemoProjectPipeline.DispatchDemoFixtureAsync("demo.exportTbFile");
        var tbImport = await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new
        {
            filePath = tbFile.GetProperty("filePath").GetString(),
            fileName = tbFile.GetProperty("fileName").GetString()
        }));
        Assert.Equal(DemoDataFactory.TbAccountCount, tbImport.GetProperty("rowCount").GetInt32());

        // 3. 假日 / 補班日
        var holidays = demo.GetProperty("holidays").EnumerateArray().Select(h => h.GetString()).ToList();
        var holidayImport = await host.DispatchAsync(
            "import.holiday", JsonSerializer.Serialize(new { dates = holidays }));
        Assert.Equal(holidays.Count, holidayImport.GetProperty("count").GetInt32());

        var makeupDays = demo.GetProperty("makeupDays").EnumerateArray().Select(h => h.GetString()).ToList();
        await host.DispatchAsync("import.makeupDay", JsonSerializer.Serialize(new { dates = makeupDays }));

        // 4. 以 loadDemo 的 mapping 提交（flag / debitCredit）
        var glCommit = await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                demo.GetProperty("gl").GetProperty("mapping").GetRawText()),
            amountMode = demo.GetProperty("gl").GetProperty("amountMode").GetString()
        }));
        Assert.Equal(DemoDataFactory.GlVoucherCount * DemoDataFactory.LinesPerVoucher, glCommit.GetProperty("projectedRowCount").GetInt32());

        var tbCommit = await host.DispatchAsync("mapping.commit.tb", JsonSerializer.Serialize(new
        {
            mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                demo.GetProperty("tb").GetProperty("mapping").GetRawText()),
            changeMode = demo.GetProperty("tb").GetProperty("changeMode").GetString()
        }));
        Assert.Equal(DemoDataFactory.TbAccountCount, tbCommit.GetProperty("projectedRowCount").GetInt32());

        // 5. 投影後全 GL scaled 淨額 = 0（逐張平衡的端到端證據）
        var db = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await using (var connection = db.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT SUM(amount_scaled) FROM target_gl_entry;";
            Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }

        // 6. resume：project.load 回傳 calendar 計數
        var loaded = await host.DispatchAsync(
            "project.load", JsonSerializer.Serialize(new { projectId }));
        var calendar = loaded.GetProperty("importState").GetProperty("calendar");
        Assert.Equal(holidays.Count, calendar.GetProperty("holidayCount").GetInt32());
        Assert.Equal(makeupDays.Count, calendar.GetProperty("makeupDayCount").GetInt32());
    }
}
