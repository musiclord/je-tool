using System.Text.Json;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch6MappingDiagnosticsTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task OverlappingManualCodes_NameTheValueAndSourceWithStableReasonCode(string provider)
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupGlAsync(host, provider, ["M", "A"]);
        var error = await Assert.ThrowsAsync<JetActionException>(() => CommitManualAsync(host, [" M "], ["m"]));
        Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
        Assert.Contains("M", error.Message, StringComparison.Ordinal);
        Assert.Contains("同時", error.Message, StringComparison.Ordinal);
        var detail = Assert.Single(ErrorDetails(error).EnumerateArray());
        Assert.Equal("manual_overlap", detail.GetProperty("reasonCode").GetString());
        Assert.Equal("模式", detail.GetProperty("sourceColumn").GetString());
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        Assert.False(loaded.GetProperty("mapping").GetProperty("gl").GetProperty("mapping").TryGetProperty("manual", out _));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ErrorsAfterFirstFiftyRows_AreCountedByValue_AndBlankHasItsOwnReasonCode(string provider)
    {
        using var host = new HandlerTestHost();
        var values = Enumerable.Range(0, 60).Select(index => index % 2 == 0 ? "X" : " x ")
            .Concat(["Y", "Y", "Y", "", "　", "M"]).ToArray();
        var projectId = await SetupGlAsync(host, provider, values);
        var before = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        var error = await Assert.ThrowsAsync<JetActionException>(() => CommitManualAsync(host, ["M"], ["A"]));
        Assert.Equal(JetErrorCodes.ProjectionFailed, error.Code);
        Assert.StartsWith("65 列無法轉換", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("以下依前", error.Message, StringComparison.Ordinal);
        var details = ErrorDetails(error).EnumerateArray().ToArray();
        Assert.Equal(2, details.Length);
        var unlisted = details.Single(detail => detail.GetProperty("reasonCode").GetString() == "manual_unlisted");
        var blank = details.Single(detail => detail.GetProperty("reasonCode").GetString() == "manual_blank");
        Assert.All(details, detail => Assert.Equal("模式", detail.GetProperty("sourceColumn").GetString()));
        var text = unlisted.GetProperty("message").GetString()!;
        Assert.Contains("有 63 列未歸類為人工或自動", text, StringComparison.Ordinal);
        Assert.Contains("「X」在第 2、3、4、5、6、7、8、9、10、11 列，共 60 列", text, StringComparison.Ordinal);
        Assert.Contains("「Y」在第 62、63、64 列", text, StringComparison.Ordinal);
        Assert.Contains("有 2 列是空白：第 65、66 列", blank.GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.InRange(error.Message.Length, 1, 2000);
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId }));
        Assert.Equal(before.GetProperty("mapping").GetProperty("gl").GetRawText(),
            loaded.GetProperty("mapping").GetProperty("gl").GetRawText());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task ManyDistinctInvalidValues_KeepTheFullRowCountAndABoundedSummary(string provider)
    {
        using var host = new HandlerTestHost();
        await SetupGlAsync(host, provider, Enumerable.Range(1, 400).Select(index => $"BAD-{index:000}").ToArray());
        var error = await Assert.ThrowsAsync<JetActionException>(() => CommitManualAsync(host, ["M"], ["A"]));
        Assert.StartsWith("400 列無法轉換", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("以下依前", error.Message, StringComparison.Ordinal);
        var detail = Assert.Single(ErrorDetails(error).EnumerateArray());
        Assert.Equal("manual_unlisted", detail.GetProperty("reasonCode").GetString());
        var text = detail.GetProperty("message").GetString()!;
        Assert.Contains("有 400 列未歸類為人工或自動", text, StringComparison.Ordinal);
        Assert.Contains("另有 390 列", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BAD-011", text, StringComparison.Ordinal);
        Assert.InRange(error.Message.Length, 1, 2500);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task TwoSources_CountEveryRepeatedValueAndLateValue_AndExplainBoundedSourceSamples(string provider)
    {
        using var host = new HandlerTestHost();
        await SetupGlAsync(host, provider, Enumerable.Repeat("X", 60).ToArray());
        var second = GlBuilder()
            .AddRow("SECOND-1", "2025-03-01", "1000", "Synthetic account", "Second source", 1, 1, " x ")
            .AddRow("SECOND-2", "2025-03-01", "1000", "Synthetic account", "Second source", 1, 1, "X")
            .AddRow("SECOND-3", "2025-03-01", "1000", "Synthetic account", "Second source", 1, 1, "Y")
            .AddRow("SECOND-4", "2025-03-01", "1000", "Synthetic account", "Second source", 1, 1, "Y")
            .AddRow("SECOND-5", "2025-03-01", "1000", "Synthetic account", "Second source", 1, 1, "Y").WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
            { filePath = second, fileName = "second-source.xlsx", mode = "append" }));
            var error = await Assert.ThrowsAsync<JetActionException>(() => CommitManualAsync(host, ["M"], ["A"]));
            Assert.StartsWith("65 列無法轉換", error.Message, StringComparison.Ordinal);
            var detail = Assert.Single(ErrorDetails(error).EnumerateArray());
            var text = detail.GetProperty("message").GetString()!;
            Assert.Contains("有 65 列未歸類為人工或自動", text, StringComparison.Ordinal);
            Assert.Contains("共 62 列", text, StringComparison.Ordinal);
            Assert.Contains("「Y」在 second-source.xlsx 第 4、5、6 列", text, StringComparison.Ordinal);
            Assert.Contains("inline-gl.xlsx", text, StringComparison.Ordinal);
            Assert.Contains("以下只列出部分來源與列號，未列出的錯誤仍計入總數。", error.Message, StringComparison.Ordinal);
            Assert.InRange(error.Message.Length, 1, 2000);
        }
        finally { TestWorkbookBuilder.Delete(second); }
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task TbProjectionErrors_IncludeLateValuesAndTheFullErrorCount(string provider)
    {
        using var host = new HandlerTestHost();
        await SetupGlAsync(host, provider, ["M"]);
        var builder = new InlineTbWorkbookBuilder();
        for (var index = 0; index < 60; index++) builder.AddRow("A", "Synthetic account", "bad-X");
        for (var index = 0; index < 3; index++) builder.AddRow("B", "Synthetic account", "bad-Y");
        var path = builder.WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            var error = await Assert.ThrowsAsync<JetActionException>(() => CommitTbAsync(host));
            Assert.StartsWith("63 列無法轉換", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("以下依前", error.Message, StringComparison.Ordinal);
            var detail = Assert.Single(ErrorDetails(error).EnumerateArray());
            Assert.Equal("變動金額", detail.GetProperty("sourceColumn").GetString());
            var text = detail.GetProperty("message").GetString()!;
            Assert.Contains("有 63 列不是有效金額", text, StringComparison.Ordinal);
            Assert.Contains("共 60 列", text, StringComparison.Ordinal);
            Assert.Contains("「bad-Y」在第 62、63、64 列", text, StringComparison.Ordinal);
        }
        finally { TestWorkbookBuilder.Delete(path); }
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task TbCommit_AllRequiredTextColumnsBlank_SucceedsWithSourceSpecificWarnings(string provider)
    {
        using var host = new HandlerTestHost();
        await SetupGlAsync(host, provider, ["M"]);
        var path = new InlineTbWorkbookBuilder().AddRow(null, "　", 1m).AddRow(" ", null, 2m).WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            var committed = await CommitTbAsync(host);
            Assert.Equal(2, committed.GetProperty("projectedRowCount").GetInt32());
            var warnings = committed.GetProperty("warnings").EnumerateArray().Select(value => value.GetString()!).ToArray();
            Assert.Equal(2, warnings.Length);
            Assert.Contains(warnings, text => text.Contains("來源欄「科目代號」整欄空白", StringComparison.Ordinal));
            Assert.Contains(warnings, text => text.Contains("來源欄「科目名稱」整欄空白", StringComparison.Ordinal));
        }
        finally { TestWorkbookBuilder.Delete(path); }
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task TbCommit_PartiallyBlankRequiredText_DoesNotWarnThatTheWholeColumnIsBlank(string provider)
    {
        using var host = new HandlerTestHost();
        await SetupGlAsync(host, provider, ["M"]);
        var path = new InlineTbWorkbookBuilder().AddRow("A", null, 1m).AddRow("B", "Synthetic name", 2m).WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.tb.fromFile", JsonSerializer.Serialize(new { filePath = path }));
            var committed = await CommitTbAsync(host);
            Assert.Empty(committed.GetProperty("warnings").EnumerateArray());
        }
        finally { TestWorkbookBuilder.Delete(path); }
    }

    private static JsonElement ErrorDetails(JetActionException error) =>
        JsonSerializer.SerializeToElement(error.Details, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static Task<JsonElement> CommitTbAsync(HandlerTestHost host) => host.DispatchAsync("mapping.commit.tb",
        JsonSerializer.Serialize(new { mapping = InlineTbWorkbookBuilder.BuildDirectModeMapping(), changeMode = "direct" }));

    private static Task<JsonElement> CommitManualAsync(HandlerTestHost host, string[] manualValues, string[] automaticValues)
    {
        var mapping = GlBuilder().BuildFlagModeMapping();
        mapping[GlMappingKeys.Manual] = "模式";
        return host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping, amountMode = "flag", manualAutoPolicy = new
            { manualValues, automaticValues, unlistedValueKind = "reject", blankValueKind = "reject" }
        }));
    }

    private static InlineGlWorkbookBuilder GlBuilder() => new InlineGlWorkbookBuilder()
        .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標", "模式");

    private static Task<string> SetupGlAsync(HandlerTestHost host, string provider, IReadOnlyList<string> values) =>
        InlineWorkbookProject.SetupAsync(host, builder =>
        {
            builder.WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標", "模式");
            for (var index = 0; index < values.Count; index++)
                builder.AddRow($"B6-{index + 1}", "2025-03-01", "1000", "Synthetic account", "Synthetic row", 1, 1, values[index]);
        }, databaseProvider: provider);
}
