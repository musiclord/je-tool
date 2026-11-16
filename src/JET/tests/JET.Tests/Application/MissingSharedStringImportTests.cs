using System.Text;
using System.Text.Json;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// GL 取代匯入讀到一半，遇到共用字串編號找不到文字的儲存格時，整次匯入失敗，
/// 前面已經寫進暫存表的列跟著交易一起撤回，案件仍是上一次匯入的資料。活頁簿與值都是合成的。
/// </summary>
public sealed class MissingSharedStringImportTests
{
    private const string SheetName = "合成總帳";
    private const int GoodRowCount = 3000;

    [Fact]
    public async Task GlReplaceImport_CellNotInTheSharedStringTable_FailsAndKeepsThePreviousBatch()
    {
        using var host = new HandlerTestHost();
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectName = "合成共用字串損壞", periodStart = "2025-01-01", periodEnd = "2025-12-31"
        }));
        var projectId = created.GetProperty("projectId").GetString()!;
        var baselinePath = Path.Combine(host.ProjectsRoot, "baseline.csv");
        await File.WriteAllTextAsync(baselinePath, "doc,amount\nV1,1\nV2,2\n");
        var baseline = await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = baselinePath }));

        var damagedRow = GoodRowCount + 2;
        var path = DamagedWorkbook(damagedRow);
        try
        {
            var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(
                "import.gl.fromFile",
                JsonSerializer.Serialize(new { filePath = path, sheetName = SheetName })));

            Assert.Equal(JetErrorCodes.FileReadError, error.Code);
            Assert.Equal(
                $"檔案「{Path.GetFileName(path)}」工作表「{SheetName}」的儲存格 B{damagedRow} 讀不到文字內容，檔案可能已損壞。"
                + $"請用 Excel 開啟這個檔案確認 B{damagedRow} 的內容，另存新檔後再匯入。",
                error.Message);

            var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
            var gl = loaded.GetProperty("importState").GetProperty("gl");
            Assert.Equal(baseline.GetProperty("batchId").GetString(), gl.GetProperty("batchId").GetString());
            Assert.Equal(2, gl.GetProperty("rowCount").GetInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>前 3,000 列正常，下一列的 B 欄指到共用字串表裡沒有的編號。</summary>
    private static string DamagedWorkbook(int damagedRow)
    {
        var rows = new StringBuilder();
        rows.Append("""<row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c></row>""");
        for (var row = 2; row < damagedRow; row++)
        {
            rows.Append($"""<row r="{row}"><c r="A{row}" t="inlineStr"><is><t>V{row}</t></is></c><c r="B{row}"><v>{row}</v></c></row>""");
        }

        rows.Append($"""<row r="{damagedRow}"><c r="A{damagedRow}" t="inlineStr"><is><t>V{damagedRow}</t></is></c><c r="B{damagedRow}" t="s"><v>9</v></c></row>""");
        return new RawXlsxBuilder()
            .WithSharedStrings("<si><t>doc</t></si><si><t>amount</t></si>")
            .AddSheet(SheetName, $"<sheetData>{rows}</sheetData>")
            .Save();
    }
}
