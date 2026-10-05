using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch8DisplayTextTests
{
    [Fact]
    public async Task DemoDateColumns_DescribeTheSameThreeDatesInMetadataAndExportedWorkbook()
    {
        var demo = DemoDataFactory.Create();
        Assert.Equal("總帳入帳日", demo.GlMapping[GlMappingKeys.PostDate]);
        Assert.Equal("傳票日期", demo.GlMapping[GlMappingKeys.VoucherDate]);
        Assert.Equal("傳票核准日", demo.GlMapping[GlMappingKeys.DocDate]);
        string[] expected = ["總帳入帳日", "傳票號碼", "傳票項次", "科目代號", "科目名稱", "摘要", "金額", "借方旗標", "建立人員", "傳票核准日", "人工傳票", "傳票日期"];
        Assert.Equal(expected, demo.GlColumns);
        var exported = await DemoProjectPipeline.DispatchDemoFixtureAsync("demo.exportGlFile");
        var path = exported.GetProperty("filePath").GetString()!;
        var columns = await new OpenXmlSaxTableReader().ReadColumnsAsync(new TabularSourceRequest(path), CancellationToken.None);
        Assert.Equal(expected, columns);
    }

    [Fact]
    public void InfHeaders_KeepKeysTypesAndOrderWhileUsingFullFieldNames()
    {
        var plan = ResultPageColumnRegistry.ForInf(null);
        Assert.Equal(new[]
        {
            ("documentNumber", "傳票號碼", "text"), ("accountCode", "會計科目編號", "text"),
            ("accountName", "會計科目名稱", "text"), ("debit", "借方金額", "money"),
            ("credit", "貸方金額", "money"), ("postDate", "總帳入帳日", "date"),
            ("approvalDate", "傳票核准日", "date"), ("createdBy", "傳票建立人員", "text"),
            ("approvedBy", "傳票核准人員", "text"), ("description", "傳票摘要", "text")
        }, plan.Columns.Select(column => (column.Key, column.Label, column.ValueType)).ToArray());
        Assert.All(plan.Columns, column => Assert.False(column.IsCustom));
    }

    [Fact]
    public void ConfirmedTbPreview_UsesConfirmedMappingStatusWithoutChangingDatasetKey()
    {
        Assert.Equal("已確認配對的試算表", DataPreviewDatasetLabels.MainTabs.Single(tab => tab.Dataset == "tbBalances").Label);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task AllZeroEffectiveAmounts_ExplainTheAffectedEntriesWithoutAuditJargon(string provider)
    {
        using var host = new HandlerTestHost();
        var error = await Assert.ThrowsAsync<JetActionException>(() => InlineWorkbookProject.SetupAsync(host, builder => builder
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("ZERO", "2025-06-30", "1101", "合成", "期內零", "0.0000", 1)
            .AddRow("OUT", "2024-12-31", "4101", "合成", "期外非零", "1.0000", 0), databaseProvider: provider));
        Assert.Equal(JetErrorCodes.GlAmountsAllZero, error.Code);
        Assert.DoesNotContain("母體", error.Message, StringComparison.Ordinal);
        Assert.Contains("納入測試的分錄", error.Message, StringComparison.Ordinal);
        Assert.Contains("借方與貸方總額都是 0", error.Message, StringComparison.Ordinal);
        Assert.Contains("確認配對", error.Message, StringComparison.Ordinal);
        var store = new JsonFileProjectStore(new JetProjectFolder(host.ProjectsRoot));
        var project = Assert.Single(await store.ListAsync(CancellationToken.None));
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = project.ProjectId }));
        Assert.Equal(JsonValueKind.Null, loaded.GetProperty("mapping").GetProperty("gl").ValueKind);
    }

    [Theory]
    [InlineData("validate.run")]
    [InlineData("prescreen.run")]
    [InlineData("filter.preview")]
    [InlineData("query.filterVoucherPage")]
    public async Task MissingPrerequisites_KeepErrorPriorityAndNameTheActualNextStep(string action)
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", """{"caseName":"Batch8-Missing-Mapping","periodStart":"2025-01-01","periodEnd":"2025-12-31","databaseProvider":"sqlite"}""");
        var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(action));
        if (action == "prescreen.run")
        {
            // New fixture correction after 20261004-091002406-dfa24f8c74e24391940f8999ec834708:
            // prescreen checks completeness first; a wording task must not change error priority or codes.
            Assert.Equal(JetErrorCodes.CompletenessPrerequisiteFailed, error.Code);
            Assert.Equal("目前資料尚無可用的資料驗證結果，請重新執行資料驗證。", error.Message);
            return;
        }
        Assert.Equal(JetErrorCodes.NoTargetData, error.Code);
        Assert.Equal("尚未確認 GL 欄位配對，請先到第三步按「確認配對」。", error.Message);
    }

    [Fact]
    public void MissingTbAndOptionalFields_UseConfirmationLanguageWithoutChangingAvailability()
    {
        Assert.Equal("尚未確認 TB 欄位配對，無法執行完整性測試。", CompletenessPartBProcedure.MissingTbMappingReason);
        // New fixture correction after 20261004-091447328-b0a62ddca30e458fb0489598e96c656e:
        // a completed N/A part B still stores count 0. Under the existing 2026-09-17 decision,
        // missing TB is a warning that does not block later work; this test changes wording only.
        var decision = CompletenessEligibility.Evaluate(new(true, true, true, true, false, 0));
        Assert.True(decision.IsEligible);
        Assert.Null(decision.Reason);
        Assert.Equal("完整性測試無法執行 GL 與 TB 的逐科目比對：TB 欄位配對尚未確認，補齊後可重新驗證。 審計員可說明差異原因並繼續篩選與匯出。", decision.Warning);
        Assert.Equal("請先確認 GL「傳票核准日」欄位配對。", PrescreenProcedures.MissingApprovalDateMappingReason);
        Assert.Equal("請先確認 GL「傳票建立人員」欄位配對。", PrescreenProcedures.MissingCreatedByMappingReason);
        Assert.Equal("請先確認 GL「傳票日期」欄位配對，才能比較是否回溯過帳。", PrescreenProcedures.MissingVoucherDateMappingReason);
        Assert.Equal("尚未確認 GL「傳票核准日」欄位配對，因此僅檢查總帳入帳日。", PrescreenProcedures.MissingApprovalDateForActivityReason);
    }
}
