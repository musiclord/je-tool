using System.Text.Json;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class UserFeedbackXlsmImportTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Xlsm_SelectedSheetIsPreviewedImportedAndProjectedWithoutChangingSource(string provider)
    {
        using var host = new HandlerTestHost(principalName: "EXAMPLE\\xlsm.reader");
        // A different first sheet proves that explicit sheet selection survives every action.
        var path = new RawXlsxBuilder()
            .AddSheet("Ignore", Sheet(["Wrong"], [["do not import"]]))
            .AddSheet("GL", Sheet(["Doc", "Date", "Account", "Name", "Memo", "Amount"],
                Enumerable.Range(1, 12).Select(i => new[] { "V" + i, "2026-01-01", "1101", "Cash", "Synthetic", "2.50" })))
            .AddSheet("TB", Sheet(["Account", "Name", "Change"], [["1101", "Cash", "30.00"]]))
            .Save(".xlsm", macroEnabled: true);
        try
        {
            var original = await File.ReadAllBytesAsync(path);
            var inspect = await host.DispatchAsync("import.inspectFile", JsonSerializer.Serialize(new { filePath = path }));
            Assert.Equal(3, inspect.GetProperty("worksheets").GetArrayLength());
            var preview = await host.DispatchAsync("import.previewFile",
                JsonSerializer.Serialize(new { filePath = path, sheetName = "GL", limit = 100 }));
            Assert.Equal("Doc", preview.GetProperty("columns")[0].GetString());
            Assert.Equal(10, preview.GetProperty("sampleRows").GetArrayLength());
            Assert.Equal("V1", preview.GetProperty("sampleRows")[0][0].GetString());

            await host.DispatchAsync("project.create", JsonSerializer.Serialize(new {
                caseName = "xlsm-test", databaseProvider = provider, periodStart = "2026-01-01", periodEnd = "2026-12-31"
            }));
            var gl = await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path, sheetName = "GL" }));
            Assert.Equal(12, gl.GetProperty("rowCount").GetInt32());
            var projectedGl = await host.DispatchAsync("mapping.commit.gl", """
                {"amountMode":"signed","mapping":{"docNum":"Doc","postDate":"Date","accNum":"Account","accName":"Name","description":"Memo","amount":"Amount"}}
                """);
            Assert.Equal(12, projectedGl.GetProperty("projectedRowCount").GetInt32());
            var tb = await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new { filePath = path, sheetName = "TB" }));
            Assert.Equal(1, tb.GetProperty("rowCount").GetInt32());
            var projectedTb = await host.DispatchAsync("mapping.commit.tb", """
                {"changeMode":"direct","mapping":{"accNum":"Account","accName":"Name","amount":"Change"}}
                """);
            Assert.Equal(1, projectedTb.GetProperty("projectedRowCount").GetInt32());
            var glTable = await host.DispatchAsync("dev.db.tableData", """{"tableName":"target_gl_entry","limit":20}""");
            var glColumns = glTable.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList();
            var amountIndex = glColumns.IndexOf("amount_scaled");
            Assert.True(amountIndex >= 0);
            Assert.Equal(12, glTable.GetProperty("rows").GetArrayLength());
            Assert.All(glTable.GetProperty("rows").EnumerateArray(), row => Assert.Equal("25000", row[amountIndex].GetString()));
            var tbTable = await host.DispatchAsync("dev.db.tableData", """{"tableName":"target_tb_balance","limit":20}""");
            var tbColumns = tbTable.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList();
            var changeIndex = tbColumns.IndexOf("change_amount_scaled");
            Assert.True(changeIndex >= 0);
            Assert.Equal("300000", tbTable.GetProperty("rows")[0][changeIndex].GetString());
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally { TestWorkbookBuilder.Delete(path); }
    }

    private static string Sheet(string[] headers, IEnumerable<string[]> rows)
    {
        var all = new[] { headers }.Concat(rows);
        return "<sheetData>" + string.Concat(all.Select(row => "<row>" + string.Concat(row.Select(value =>
            "<c t=\"inlineStr\"><is><t>" + System.Security.SecurityElement.Escape(value) + "</t></is></c>")) + "</row>")) + "</sheetData>";
    }
}
