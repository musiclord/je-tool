using System.Text.Json;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// DuckDB 第二本地引擎的 Application 層 journey 驗收（spec §5）：刪除路徑的檔案系統物證。
/// 述詞等價（整套規則）見 <c>DuckDbGlRuleSqlEquivalenceTests</c>；旅程／oracle 等價見
/// <c>ProviderParityJourneyTests</c>／<c>DemoRuleOracleTests</c> 的 DuckDb 鏡射；可攜性見
/// <c>DualSourceProjectTests</c>；建案契約見 <c>ProjectHandlersTests.Create_RecordsDuckDbDatabaseProvider</c>。
/// DuckDB 是本地檔引擎、不需外部後端，一律 <c>[Fact]</c>。
/// </summary>
public sealed class DuckDbProviderTests
{
    [Fact]
    public async Task Delete_DuckDbProject_RemovesFolderAndDatabaseFile()
    {
        using var host = new HandlerTestHost();

        // 建案（duckdb）→ 匯入兩列 GL（產生 jet.duckdb 實體檔）。
        var created = await host.DispatchAsync(
            "project.create",
            """
            { "caseName": "刪除案duck", "projectCode": "DEL-D", "entityName": "刪除實體", "operatorId": "op",
              "periodStart": "2024-01-01", "periodEnd": "2024-12-31", "databaseProvider": "duckdb" }
            """);
        var id = created.GetProperty("projectId").GetString()!;

        var glFile = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "傳票號碼"; ws.Cell(1, 2).Value = "傳票日期"; ws.Cell(1, 3).Value = "科目代號"; ws.Cell(1, 4).Value = "金額";
            ws.Cell(2, 1).Value = "JV-1"; ws.Cell(2, 2).Value = "2024-03-01"; ws.Cell(2, 3).Value = "1101"; ws.Cell(2, 4).Value = "100";
            ws.Cell(3, 1).Value = "JV-2"; ws.Cell(3, 2).Value = "2024-03-02"; ws.Cell(3, 3).Value = "4101"; ws.Cell(3, 4).Value = "100";
        });

        var projectDir = Path.Combine(host.ProjectsRoot, id);
        var dbPath = Path.Combine(projectDir, "jet.duckdb");

        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = glFile }));
            Assert.True(File.Exists(dbPath), "匯入後 jet.duckdb 應存在");

            // project.delete → 資料庫檔與整個專案資料夾確實消失（連線釋放後刪檔成功）。
            var deleted = await host.DispatchAsync("project.delete", $$"""{ "projectId": "{{id}}" }""");
            Assert.True(deleted.GetProperty("ok").GetBoolean());

            Assert.False(File.Exists(dbPath), "刪除後 jet.duckdb 應消失");
            Assert.False(Directory.Exists(projectDir), "刪除後專案資料夾應消失");
        }
        finally
        {
            TestWorkbookBuilder.Delete(glFile);
        }
    }
}
