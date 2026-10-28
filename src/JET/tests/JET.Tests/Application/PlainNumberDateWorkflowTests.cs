using System.Text;
using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 預篩選收斂計畫階段七（使用者 2026-10-22 裁定）：GL 日期欄出現 2025、2025.12、45292 這類純數字時，不再當成 Excel 序列值換成日期，
/// 確認配對時和其他無法辨識的日期一樣寫出列號，本次配對不儲存。SQLite 與 DuckDB 各跑一次，三種日期角色都試。
/// 第一次失敗：收據 20261024-044716893-6a6900c538364667949441681dcd3b6d，當時 2025 被換成 1905-07-17、2025.12 也是 1905 年、45292 被換成 2024-01-01，配對照常儲存。
/// </summary>
public sealed class PlainNumberDateWorkflowTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var provider in new[] { "sqlite", "duckdb" })
        foreach (var field in new[] { "postDate", "docDate", "rde" }) yield return [provider, field];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PlainNumbersInDateColumn_AreRowErrorsWithRowNumbers(string provider, string field)
    {
        var path = Path.Combine(Path.GetTempPath(), $"jet-plain-number-date-{Guid.NewGuid():N}.csv");
        // 第 2 到第 4 列是純數字，第 5 列是文字日期；只有第 5 列可以讀成日期。
        File.WriteAllText(path,
            "Document,Date,Account,Name,Description,Amount,PlainNumber\n"
            + "V1,2025-03-05,A,Synthetic,Normal,1,2025\n"
            + "V2,2025-03-05,A,Synthetic,Normal,1,2025.12\n"
            + "V3,2025-03-05,A,Synthetic,Normal,1,45292\n"
            + "V4,2025-03-05,A,Synthetic,Normal,1,2025-03-05\n", new UTF8Encoding(false));
        try
        {
            using var host = new HandlerTestHost();
            await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
            { caseName = "Plain number dates", periodStart = "2024-01-01", periodEnd = "2025-12-31", databaseProvider = provider }));
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            var mapping = Batch9MappingTestFixture.Mapping();
            object[] rde = [];
            if (field == "rde") rde = [new { sourceColumn = "PlainNumber", label = "Plain number", valueType = "date" }];
            else mapping[field] = "PlainNumber";

            var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("mapping.commit.gl",
                JsonSerializer.Serialize(new { mapping, amountMode = "signed", rdeFields = rde })));

            Assert.Equal(JetErrorCodes.ProjectionFailed, error.Code);
            Assert.StartsWith("有 3 列的值無法讀取，本次配對未儲存。", error.Message, StringComparison.Ordinal);
            var detail = Assert.Single(error.Details!);
            Assert.Equal("PlainNumber", detail.SourceColumn);
            Assert.Contains("無法辨識為日期", detail.Message, StringComparison.Ordinal);
            Assert.Contains(field == "rde" ? "請確認來源的日期格式，或將此攸關資料元素欄位改為文字" : "請確認來源的日期格式，或改選正確的日期欄",
                detail.Message, StringComparison.Ordinal);
            Assert.Contains("「2025」在第 2 列", detail.Message, StringComparison.Ordinal);
            Assert.Contains("「2025.12」在第 3 列", detail.Message, StringComparison.Ordinal);
            Assert.Contains("「45292」在第 4 列", detail.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("第 5 列", detail.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }
}
