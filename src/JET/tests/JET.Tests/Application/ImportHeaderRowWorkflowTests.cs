using System.Text.Json;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 預篩選收斂計畫階段六的匯入固定答案。使用者 2026-10-22 裁定「欄名在第幾列」開放給 GL 與 TB 的一般匯入，含文字檔，Access 不適用。
/// 審計員在第二步的待匯入清單替每個來源填欄名在第幾列，匯入時從那一列起找欄名，前面的標題列與空白列都不算資料；
/// 這個選擇和工作表、編碼、分隔符一樣跟著來源保存，匯入回應與重開案件都帶回來，審計員才知道這份來源是怎麼讀的。
/// 第一次失敗：Focused 收據 20261023-193734239-f6249c3be1e046cfa0c55b4181d2cabd（headerRow 被忽略，標題列被當成欄名；回應的來源沒有 headerRow）。
/// </summary>
public sealed class ImportHeaderRowWorkflowTests
{
    private static string CreatePayload(string provider) => JsonSerializer.Serialize(new
    {
        projectCode = "HEADER-ROW-001",
        entityName = "合成欄名列案件",
        operatorId = "tester",
        periodStart = "2025-01-01",
        periodEnd = "2025-12-31",
        databaseProvider = provider
    });

    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static int? HeaderRowOf(JsonElement source) =>
        source.GetProperty("headerRow").ValueKind == JsonValueKind.Null ? null : source.GetProperty("headerRow").GetInt32();

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ImportWithHeaderRow_UsesThatRowAsHeader_AndKeepsTheChoiceWithTheSource(string provider)
    {
        using var host = new HandlerTestHost();
        var created = await host.DispatchAsync("project.create", CreatePayload(provider));
        var projectId = created.GetProperty("projectId").GetString()!;

        // GL：Excel 第 1 列是公司名稱，第 2 列空白，第 3 列才是欄名。
        var glPath = TestWorkbookBuilder.WriteWorkbook(ws =>
        {
            ws.Cell(1, 1).Value = "合成公司 總帳明細";
            string[] header = ["傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標"];
            for (var c = 0; c < header.Length; c++)
            {
                ws.Cell(3, c + 1).Value = header[c];
            }

            object[][] rows =
            [
                ["JV-1", "2025-03-01", "1000", "合成現金", "合成摘要", 10, 1],
                ["JV-1", "2025-03-01", "4000", "合成收入", "合成摘要", 10, 0],
            ];
            for (var r = 0; r < rows.Length; r++)
            {
                for (var c = 0; c < rows[r].Length; c++)
                {
                    ws.Cell(r + 4, c + 1).Value = XLCellValueOf(rows[r][c]);
                }
            }
        });
        // TB：文字檔第 1 列是報表標題，第 2 列才是欄名。
        var tbPath = TestCsvBuilder.WriteFile("合成試算表\n科目代號,科目名稱,變動金額\n1000,合成現金,20\n4000,合成收入,-20\n", TestCsvBuilder.Utf8NoBom);
        // 第二份 GL 來源沒有填欄名在第幾列，欄名在第 1 列，欄位和第一份相同。
        var glAppendPath = TestCsvBuilder.WriteFile(
            "傳票號碼,傳票日期,科目代號,科目名稱,摘要,金額,借方旗標\nJV-2,2025-03-04,1000,合成現金,合成摘要,20,1\n",
            TestCsvBuilder.Utf8NoBom);

        try
        {
            var gl = await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
            {
                mode = "replace",
                sources = new object[] { new { filePath = glPath, fileName = "合成總帳.xlsx", sheetName = "Sheet1", headerRow = 3 } }
            }));
            Assert.Equal(["傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標"], Strings(gl.GetProperty("columns")));
            Assert.Equal(2, gl.GetProperty("rowCount").GetInt32());
            var glSource = Assert.Single(gl.GetProperty("sources").EnumerateArray());
            Assert.Equal(3, HeaderRowOf(glSource));

            var appended = await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
            {
                mode = "append",
                sources = new object[] { new { filePath = glAppendPath, fileName = "合成總帳補充.csv" } }
            }));
            Assert.Equal(3, appended.GetProperty("rowCount").GetInt32());
            var appendedSources = appended.GetProperty("sources").EnumerateArray().ToArray();
            Assert.Equal([3, null], appendedSources.Select(HeaderRowOf).ToArray());

            var tb = await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new
            {
                mode = "replace",
                sources = new object[] { new { filePath = tbPath, fileName = "合成試算表.csv", encoding = "utf-8", delimiter = ",", headerRow = 2 } }
            }));
            Assert.Equal(["科目代號", "科目名稱", "變動金額"], Strings(tb.GetProperty("columns")));
            Assert.Equal(2, tb.GetProperty("rowCount").GetInt32());
            Assert.Equal(2, HeaderRowOf(Assert.Single(tb.GetProperty("sources").EnumerateArray())));

            // 重開案件：來源清單帶回同一個欄名列，審計員看得到這份來源是從第幾列讀的。
            var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
            var importState = loaded.GetProperty("importState");
            Assert.Equal([3, null],
                importState.GetProperty("gl").GetProperty("sources").EnumerateArray().Select(HeaderRowOf).ToArray());
            Assert.Equal(2, HeaderRowOf(Assert.Single(importState.GetProperty("tb").GetProperty("sources").EnumerateArray())));

            // PBC-GL 的資料預覽直接讀暫存表：標題列不在裡面，兩份來源合計 3 列。
            // 2026-10-28 複審：原註解另寫「列號是來源檔的實際列號」，但資料預覽不回傳列號，這裡沒有斷言它，所以拿掉那半句。
            var preview = await host.DispatchAsync("query.dataPreview", JsonSerializer.Serialize(new { dataset = "pbcGl", limit = 10, offset = 0 }));
            Assert.Equal(3, preview.GetProperty("rows").GetArrayLength());
        }
        finally
        {
            TestWorkbookBuilder.Delete(glPath);
            TestCsvBuilder.Delete(tbPath);
            TestCsvBuilder.Delete(glAppendPath);
        }
    }

    private static ClosedXML.Excel.XLCellValue XLCellValueOf(object value) => value switch
    {
        int i => i,
        string s => s,
        _ => throw new InvalidOperationException($"合成資料不支援的型別：{value.GetType().Name}")
    };
}
