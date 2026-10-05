using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class ManualAutoComplementWorkflowTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task BothComplementDirections_RespectEveryBlankChoiceAndFailedProjectionKeepsLastSavedPolicy(string provider)
    {
        using var host = new HandlerTestHost();
        var id = await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標", "模式")
            .AddRow("JV-1", "2025-03-01", "1000", "合成科目", "合成 1", "100", 1, "M")
            .AddRow("JV-1", "2025-03-01", "1000", "合成科目", "合成 2", "100", 0, "A")
            .AddRow("JV-1", "2025-03-01", "1000", "合成科目", "合成 3", "10", 1, "NEW")
            .AddRow("JV-1", "2025-03-01", "1000", "合成科目", "合成 4", "10", 0, " m ")
            .AddRow("JV-1", "2025-03-01", "1000", "合成科目", "合成 5", "0", 1, null),
            databaseProvider: provider, validateForDownstream: true);
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
        var gl = loaded.GetProperty("mapping").GetProperty("gl");
        var mapping = gl.GetProperty("mapping").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        mapping[GlMappingKeys.Manual] = "模式";
        var mode = gl.GetProperty("amountMode").GetString();
        Task<JsonElement> Commit(string unlisted, string blank) => host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping, amountMode = mode, manualAutoPolicy = new
            {
                manualValues = unlisted == "automatic" ? new[] { "M" } : Array.Empty<string>(),
                automaticValues = unlisted == "manual" ? new[] { "A" } : Array.Empty<string>(),
                unlistedValueKind = unlisted, blankValueKind = blank
            }
        }));
        async Task<long> Count(bool manual)
        {
            var result = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new
            {
                scenario = new { name = "合成人工政策", rationale = "核對補集與空白選擇", groups = new[] { new
                { rules = new[] { new { type = "manualAuto", isManual = manual } } } } }
            }));
            return result.GetProperty("scenario").GetProperty("count").GetInt64();
        }
        var cases = new[]
        {
            (Unlisted: "automatic", Blank: "unclassified", Manual: 2L, Automatic: 2L),
            (Unlisted: "automatic", Blank: "manual", Manual: 3L, Automatic: 2L),
            (Unlisted: "automatic", Blank: "automatic", Manual: 2L, Automatic: 3L),
            (Unlisted: "manual", Blank: "unclassified", Manual: 3L, Automatic: 1L),
            (Unlisted: "manual", Blank: "manual", Manual: 4L, Automatic: 1L),
            (Unlisted: "manual", Blank: "automatic", Manual: 3L, Automatic: 2L)
        };
        foreach (var expected in cases)
        {
            var committed = await Commit(expected.Unlisted, expected.Blank);
            Assert.Equal(expected.Unlisted, committed.GetProperty("manualAutoPolicy").GetProperty("unlistedValueKind").GetString());
            Assert.Equal(expected.Blank, committed.GetProperty("manualAutoPolicy").GetProperty("blankValueKind").GetString());
            await host.DispatchAsync("validate.run");
            Assert.Equal(expected.Manual, await Count(true));
            Assert.Equal(expected.Automatic, await Count(false));
            loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
            var savedPolicy = loaded.GetProperty("mapping").GetProperty("gl").GetProperty("manualAutoPolicy");
            Assert.Equal(expected.Unlisted, savedPolicy.GetProperty("unlistedValueKind").GetString());
            Assert.Equal(expected.Blank, savedPolicy.GetProperty("blankValueKind").GetString());

            var failure = await Assert.ThrowsAsync<JetActionException>(() => Commit(expected.Unlisted, "reject"));
            Assert.Contains("來源空白時", failure.Message, StringComparison.Ordinal);
            loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
            Assert.Equal(savedPolicy.GetRawText(), loaded.GetProperty("mapping").GetProperty("gl").GetProperty("manualAutoPolicy").GetRawText());
            Assert.Equal(expected.Manual, await Count(true));
            Assert.Equal(expected.Automatic, await Count(false));
        }
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task Complement_ReopenReportRestoreFailedRemapAndRetryPreserveAuditorChoice(string provider)
    {
        using var host = new HandlerTestHost();
        var id = await InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標", "模式")
            .AddRow("JV-1", "2025-03-01", "1000", "合成科目", "合成 1", "100", 1, "M")
            .AddRow("JV-1", "2025-03-01", "1000", "合成科目", "合成 2", "100", 0, "A")
            .AddRow("JV-1", "2025-03-01", "1000", "合成科目", "合成 3", "10", 1, "NEW")
            .AddRow("JV-1", "2025-03-01", "1000", "合成科目", "合成 4", "10", 0, null)
            .AddRow("JV-1", "2025-03-01", "1000", "合成科目", "合成 5", "5", 1, " m ")
            .AddRow("JV-1", "2025-03-01", "1000", "合成科目", "合成 6", "5", 0, "OTHER"),
            databaseProvider: provider, validateForDownstream: true);
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
        var gl = loaded.GetProperty("mapping").GetProperty("gl");
        var mapping = gl.GetProperty("mapping").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        mapping[GlMappingKeys.Manual] = "模式";
        var mode = gl.GetProperty("amountMode").GetString();
        Task<JsonElement> Commit(string blank) => host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(new
        {
            mapping, amountMode = mode, manualAutoPolicy = new
            {
                manualValues = new[] { "M" }, automaticValues = Array.Empty<string>(),
                unlistedValueKind = "automatic", blankValueKind = blank
            }
        }));
        await Commit("unclassified");
        var validation = await host.DispatchAsync("validate.run");
        async Task<long> Count(bool manual)
        {
            var result = await host.DispatchAsync("filter.preview", JsonSerializer.Serialize(new
            {
                scenario = new { name = "合成人工政策", rationale = "驗證手動選擇", groups = new[] { new
                { rules = new[] { new { type = "manualAuto", isManual = manual } } } } }
            }));
            return result.GetProperty("scenario").GetProperty("count").GetInt64();
        }
        Assert.Equal(2, await Count(true));
        Assert.Equal(3, await Count(false));
        var reports = await host.DispatchAsync("export.validationArtifacts", JsonSerializer.Serialize(new
        { runId = validation.GetProperty("resultRef").GetProperty("runId").GetString() }));
        var reportPath = reports.GetProperty("artifacts")[0].GetProperty("fullPath").GetString();
        var restored = await host.DispatchAsync("mapping.restoreDraft", JsonSerializer.Serialize(new { filePath = reportPath }));
        Assert.Equal("automatic", restored.GetProperty("gl").GetProperty("manualAutoPolicy").GetProperty("unlistedValueKind").GetString());
        Assert.Equal("unclassified", restored.GetProperty("gl").GetProperty("manualAutoPolicy").GetProperty("blankValueKind").GetString());
        await Assert.ThrowsAsync<JetActionException>(() => Commit("reject"));
        loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = id }));
        Assert.Equal("unclassified", loaded.GetProperty("mapping").GetProperty("gl").GetProperty("manualAutoPolicy").GetProperty("blankValueKind").GetString());
        Assert.Equal(2, await Count(true));
        Assert.Equal(3, await Count(false));
        await Commit("automatic");
        await host.DispatchAsync("validate.run");
        Assert.Equal(2, await Count(true));
        Assert.Equal(4, await Count(false));
    }
}
