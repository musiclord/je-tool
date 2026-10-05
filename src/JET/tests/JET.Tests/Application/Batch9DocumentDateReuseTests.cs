using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Application;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch9DocumentDateReuseTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Validation_CountsDistinctNumbersAcrossDatesWithinEffectiveRows_AndPersistsTheSameFacts(string provider)
    {
        using var host = new HandlerTestHost();
        var id = await PrepareAsync(host, provider, reused: true);
        var before = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
        Assert.Equal(JsonValueKind.Null, before.GetProperty("latestRuns").GetProperty("validate").ValueKind);
        var validation = await host.DispatchAsync("validate.run");
        AssertReuse(validation, numbers: 2, entries: 5);
        // R10 warns only: voucher identity still counts number alone, not number plus date.
        Assert.Equal(5, validation.GetProperty("stats").GetProperty("voucherCount").GetInt64());
        Assert.Equal(11, validation.GetProperty("populationSummary").GetProperty("effective").GetProperty("rowCount").GetInt64());
        Assert.Equal(2, validation.GetProperty("populationSummary").GetProperty("excluded").GetProperty("rowCount").GetInt64());
        Assert.True(ValidationSummaryShapeValidator.IsValid(validation.GetRawText()));
        var reopened = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
        AssertReuse(reopened.GetProperty("latestRuns").GetProperty("validate"), numbers: 2, entries: 5);
        Assert.False(reopened.GetProperty("staleState").GetProperty("validation").GetBoolean());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Validation_ExplicitlyReportsZeroAfterMeasuring_NoSameDayOrBlankNumberFalsePositive(string provider)
    {
        using var host = new HandlerTestHost();
        await PrepareAsync(host, provider, reused: false);
        var validation = await host.DispatchAsync("validate.run");
        AssertReuse(validation, numbers: 0, entries: 0);
        Assert.Equal(1, validation.GetProperty("stats").GetProperty("voucherCount").GetInt64());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task CurrentSummary_RequiresMeasuredCountsAndRejectsMissingMalformedOrImpossibleValues(string provider)
    {
        using var host = new HandlerTestHost();
        await PrepareAsync(host, provider, reused: true);
        var validation = await host.DispatchAsync("validate.run");
        var complete = JsonNode.Parse(validation.GetRawText())!.AsObject();
        // Independent literal oracle, not a value computed by the implementation under test.
        complete["documentDateReuse"] = new JsonObject { ["documentNumberCount"] = 2, ["entryCount"] = 5 };
        Assert.True(ValidationSummaryShapeValidator.IsValid(complete.ToJsonString()));
        var missing = complete.DeepClone().AsObject();
        missing.Remove("documentDateReuse");
        Assert.False(ValidationSummaryShapeValidator.IsValid(missing.ToJsonString()));
        foreach (var malformed in new[]
        {
            "null", "{}", "{\"documentNumberCount\":2}",
            "{\"documentNumberCount\":\"2\",\"entryCount\":5}",
            "{\"documentNumberCount\":-1,\"entryCount\":5}",
            "{\"documentNumberCount\":2,\"entryCount\":3}",
            "{\"documentNumberCount\":0,\"entryCount\":1}",
            "{\"documentNumberCount\":2,\"entryCount\":12}",
            "{\"documentNumberCount\":6,\"entryCount\":12}",
            "{\"documentNumberCount\":2,\"entryCount\":5,\"extra\":0}"
        })
        {
            var changed = complete.DeepClone().AsObject();
            changed["documentDateReuse"] = JsonNode.Parse(malformed);
            Assert.False(ValidationSummaryShapeValidator.IsValid(changed.ToJsonString()), malformed);
        }
    }

    private static void AssertReuse(JsonElement summary, long numbers, long entries)
    {
        var counts = summary.GetProperty("documentDateReuse");
        Assert.Equal(new[] { "documentNumberCount", "entryCount" }, counts.EnumerateObject().Select(item => item.Name).Order());
        Assert.Equal(numbers, counts.GetProperty("documentNumberCount").GetInt64());
        Assert.Equal(entries, counts.GetProperty("entryCount").GetInt64());
    }

    private static async Task<string> PrepareAsync(HandlerTestHost host, string provider, bool reused)
    {
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        { caseName = "合成日期重用", periodStart = "2025-01-01", periodEnd = "2025-12-31", databaseProvider = provider }));
        var id = created.GetProperty("projectId").GetString()!;
        (string? Number, string Date, int Amount)[] rows = reused ?
        [
            ("CROSS", "2025-01-01", 10), ("CROSS", "2025-01-01", -10), ("CROSS", "2025-02-01", 5),
            ("SAME_MONTH", "2025-03-01", 10), ("SAME_MONTH", "2025-03-02", -10),
            ("SAME_DAY", "2025-04-01", 10), ("SAME_DAY", "2025-04-01", -10),
            ("ONE", "2025-05-01", 2), (null, "2025-06-01", 3), ("", "2025-06-02", -3),
            ("ONLY_OUT", "2025-01-05", 1), ("ONLY_OUT", "2026-01-05", -1), ("CROSS", "2026-01-01", 11)
        ] :
        [
            ("SAME_DAY", "2025-04-01", 10), ("SAME_DAY", "2025-04-01", -10),
            (null, "2025-06-01", 3), ("", "2025-06-02", -3)
        ];
        var path = TestWorkbookBuilder.WriteWorkbook(sheet =>
        {
            string[] headers = ["Number", "Date", "Account", "Description", "Amount", "Name"];
            for (var column = 0; column < headers.Length; column++) sheet.Cell(1, column + 1).Value = headers[column];
            for (var index = 0; index < rows.Length; index++)
            {
                if (rows[index].Number is { } number) sheet.Cell(index + 2, 1).Value = number;
                sheet.Cell(index + 2, 2).Value = rows[index].Date;
                sheet.Cell(index + 2, 3).Value = "1000";
                sheet.Cell(index + 2, 4).Value = "合成分錄";
                sheet.Cell(index + 2, 5).Value = rows[index].Amount;
                sheet.Cell(index + 2, 6).Value = "合成科目";
            }
        });
        try { await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new { filePath = path })); }
        finally { TestWorkbookBuilder.Delete(path); }
        await host.DispatchAsync("mapping.commit.gl", """{"amountMode":"signed","mapping":{"docNum":"Number","postDate":"Date","accNum":"Account","accName":"Name","description":"Description","amount":"Amount"}}""");
        return id;
    }
}
