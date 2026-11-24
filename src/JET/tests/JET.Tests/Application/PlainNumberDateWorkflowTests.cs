using System.Text;
using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// GL 日期欄出現 2025、2025.12、45292 這類純數字時，不當成 Excel 序列值換成日期，也不擋確認配對：
/// 這些值列為無法辨識的日期，回應的日期提醒寫出欄位、筆數、值與列號。三種日期角色都試。
/// 當成序列值的話，2025 會換成 1905-07-17、2025.12 也是 1905 年、45292 會換成 2024-01-01。
/// </summary>
public sealed class PlainNumberDateWorkflowTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var provider in new[] { "sqlite" })
        foreach (var field in new[] { "glDate", "approvalDate", "rde" }) yield return [provider, field];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PlainNumbersInDateColumn_AreReportedWithoutBlockingTheMapping(string provider, string field)
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
            { projectName = "Plain number dates", periodStart = "2024-01-01", periodEnd = "2025-12-31", databaseProvider = provider }));
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            var mapping = MappingCaseTemplateFixture.Mapping();
            object[] rde = [];
            if (field == "rde") rde = [new { sourceColumn = "PlainNumber", label = "Plain number", valueType = "date" }];
            else mapping[field] = "PlainNumber";

            var committed = JsonSerializer.SerializeToElement(await host.DispatchAsync("mapping.commit.gl",
                JsonSerializer.Serialize(new { mapping, amountMode = "signed", rdeFields = rde })));

            // 四列都存下來；日期提醒只有一則，說的是配到 PlainNumber 的那個欄位。
            Assert.Equal(4, committed.GetProperty("projectedRowCount").GetInt32());
            var notice = Assert.Single(committed.GetProperty("dateNotices").EnumerateArray());
            Assert.Equal("PlainNumber", notice.GetProperty("sourceColumn").GetString());
            Assert.Equal("unrecognized", notice.GetProperty("kind").GetString());
            Assert.Equal(3, notice.GetProperty("count").GetInt64());
            Assert.Equal(0, notice.GetProperty("omittedValueRowCount").GetInt64());
            // 總帳日期讀不成日期時整列列入 GL(未納入測試)；其他日期欄只是那一格當成沒有日期。
            Assert.Equal(field == "glDate" ? "excluded" : "notUsed", notice.GetProperty("handling").GetString());
            Assert.Equal(field switch { "glDate" => "總帳日期", "approvalDate" => "傳票核准日", _ => "Plain number" },
                notice.GetProperty("label").GetString());
            Assert.Equal(
                ["2025|1|2", "2025.12|1|3", "45292|1|4"],
                notice.GetProperty("values").EnumerateArray().Select(value =>
                    value.GetProperty("value").GetString() + "|" + value.GetProperty("count").GetInt64() + "|"
                    + string.Join(",", value.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("row").GetInt32()))));
            foreach (var value in notice.GetProperty("values").EnumerateArray())
            {
                // 純數字整個值都標出來。
                Assert.Equal("number", value.GetProperty("problem").GetString());
                Assert.Equal(0, value.GetProperty("markStart").GetInt32());
                Assert.Equal(value.GetProperty("value").GetString()!.Length, value.GetProperty("markLength").GetInt32());
            }

            var excluded = JsonSerializer.SerializeToElement(await host.DispatchAsync("query.dataPreview",
                JsonSerializer.Serialize(new { dataset = "glExcluded" })));
            var stats = excluded.GetProperty("stats");
            Assert.Equal(field == "glDate" ? 3 : 0, stats.GetProperty("unrecognizedGlDateCount").GetInt64());
            Assert.Equal(0, stats.GetProperty("blankGlDateCount").GetInt64());
            Assert.Equal(field == "glDate" ? 3 : 0, excluded.GetProperty("totalCount").GetInt64());
        }
        finally { File.Delete(path); }
    }

    // 每一列的總帳日期都讀不成日期時沒有任何分錄納入 GL，確認配對照舊不儲存，並列出那些值與列號。
    [Fact]
    public async Task EveryGlDateUnrecognized_StillFailsAndListsTheValues()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jet-plain-number-date-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path,
            "Document,Date,Account,Name,Description,Amount,PlainNumber\n"
            + "V1,2025-03-05,A,Synthetic,Normal,1,2025\n"
            + "V2,2025-03-05,A,Synthetic,Normal,1,45292\n", new UTF8Encoding(false));
        try
        {
            using var host = new HandlerTestHost();
            await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
            { projectName = "Plain number dates", periodStart = "2024-01-01", periodEnd = "2025-12-31", databaseProvider = "sqlite" }));
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            var mapping = MappingCaseTemplateFixture.Mapping();
            mapping["glDate"] = "PlainNumber";

            var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync("mapping.commit.gl",
                JsonSerializer.Serialize(new { mapping, amountMode = "signed", rdeFields = Array.Empty<object>() })));

            Assert.Equal(JetErrorCodes.ProjectionFailed, error.Code);
            Assert.StartsWith("有 2 列的值無法讀取，本次配對未儲存。", error.Message, StringComparison.Ordinal);
            var detail = Assert.Single(error.Details!);
            Assert.Equal("PlainNumber", detail.SourceColumn);
            Assert.Contains("無法辨識為日期", detail.Message, StringComparison.Ordinal);
            Assert.Contains("請確認來源的日期格式，或改選正確的日期欄", detail.Message, StringComparison.Ordinal);
            Assert.Contains("「2025」在第 2 列", detail.Message, StringComparison.Ordinal);
            Assert.Contains("「45292」在第 3 列", detail.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    // 年份多打成五位數以上的日期，例如 99999/02/01，JET 不猜是哪一年，照樣列為無法辨識；
    // 日期提醒只標出年份那一段，問題種類是年份超過四位數。
    [Fact]
    public async Task YearLongerThanFourDigits_IsReportedWithOnlyTheYearMarked()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jet-long-year-date-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path,
            "Document,Date,Account,Name,Description,Amount,Approved\n"
            + "V1,2025-03-05,A,Synthetic,Normal,1,99999/02/01\n"
            + "V2,2025-03-05,A,Synthetic,Normal,1,2025-03-06\n", new UTF8Encoding(false));
        try
        {
            using var host = new HandlerTestHost();
            await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
            { projectName = "Long year dates", periodStart = "2024-01-01", periodEnd = "2025-12-31", databaseProvider = "sqlite" }));
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            var mapping = MappingCaseTemplateFixture.Mapping();
            mapping["approvalDate"] = "Approved";

            var committed = JsonSerializer.SerializeToElement(await host.DispatchAsync("mapping.commit.gl",
                JsonSerializer.Serialize(new { mapping, amountMode = "signed", rdeFields = Array.Empty<object>() })));

            Assert.Equal(2, committed.GetProperty("projectedRowCount").GetInt32());
            var notice = Assert.Single(committed.GetProperty("dateNotices").EnumerateArray());
            Assert.Equal("unrecognized", notice.GetProperty("kind").GetString());
            Assert.Equal("notUsed", notice.GetProperty("handling").GetString());
            var value = Assert.Single(notice.GetProperty("values").EnumerateArray());
            Assert.Equal("99999/02/01", value.GetProperty("value").GetString());
            Assert.Equal("yearTooLong", value.GetProperty("problem").GetString());
            Assert.Equal(0, value.GetProperty("markStart").GetInt32());
            Assert.Equal(5, value.GetProperty("markLength").GetInt32());
        }
        finally { File.Delete(path); }
    }

    // 日期欄用全形數字寫，例如 ２０２５/０６/１１，不是常見的日期寫法：確認配對照常完成，
    // 這些值列為無法辨識的日期，日期提醒照「不是日期」標出整個值。
    [Fact]
    public async Task FullWidthDigitDates_AreReportedWithoutFailingTheMapping()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jet-full-width-date-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path,
            "Document,Date,Account,Name,Description,Amount,Approved\n"
            + "V1,2025-03-05,A,Synthetic,Normal,1,２０２５/０６/１１\n"
            + "V2,2025-03-05,A,Synthetic,Normal,1,１１４/０６/１１\n"
            + "V3,2025-03-05,A,Synthetic,Normal,1,２０２５０６１１\n"
            + "V4,2025-03-05,A,Synthetic,Normal,1,2025-03-06\n", new UTF8Encoding(false));
        try
        {
            using var host = new HandlerTestHost();
            await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
            { projectName = "Full width dates", periodStart = "2024-01-01", periodEnd = "2025-12-31", databaseProvider = "sqlite" }));
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            var mapping = MappingCaseTemplateFixture.Mapping();
            mapping["approvalDate"] = "Approved";

            object? response = null;
            var commitError = await Record.ExceptionAsync(async () => response = await host.DispatchAsync("mapping.commit.gl",
                JsonSerializer.Serialize(new { mapping, amountMode = "signed", rdeFields = Array.Empty<object>() })));

            Assert.Null(commitError);
            var committed = JsonSerializer.SerializeToElement(response);
            Assert.Equal(4, committed.GetProperty("projectedRowCount").GetInt32());
            var notice = Assert.Single(committed.GetProperty("dateNotices").EnumerateArray());
            Assert.Equal("unrecognized", notice.GetProperty("kind").GetString());
            Assert.Equal("notUsed", notice.GetProperty("handling").GetString());
            Assert.Equal(3, notice.GetProperty("count").GetInt64());
            Assert.Equal(
                ["２０２５/０６/１１|2", "１１４/０６/１１|3", "２０２５０６１１|4"],
                notice.GetProperty("values").EnumerateArray().Select(value =>
                    value.GetProperty("value").GetString() + "|"
                    + string.Join(",", value.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("row").GetInt32()))));
            foreach (var value in notice.GetProperty("values").EnumerateArray())
            {
                Assert.Equal("notDate", value.GetProperty("problem").GetString());
                Assert.Equal(0, value.GetProperty("markStart").GetInt32());
                Assert.Equal(value.GetProperty("value").GetString()!.Length, value.GetProperty("markLength").GetInt32());
            }
        }
        finally { File.Delete(path); }
    }
}
