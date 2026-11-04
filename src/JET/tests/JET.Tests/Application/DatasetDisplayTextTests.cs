using System.Text.Json;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 資料集與欄位在示範資料、INF 抽樣結果、資料預覽頁籤，以及 GL 金額全為 0 的錯誤訊息裡顯示的名稱。
/// </summary>
public sealed class DatasetDisplayTextTests
{
    [Fact]
    public async Task DemoDateColumns_DescribeTheSameTwoDatesInMetadataAndExportedWorkbook()
    {
        var demo = DemoDataFactory.Create();
        Assert.Equal("總帳日期", demo.GlMapping[GlMappingKeys.GlDate]);
        // 裁定 2026-10-08-03：拿掉傳票日期與回溯過帳，示範資料只剩總帳日期與傳票核准日。
        Assert.DoesNotContain("voucherDate", demo.GlMapping.Keys);
        Assert.Equal("傳票核准日", demo.GlMapping[GlMappingKeys.ApprovalDate]);
        string[] expected = ["總帳日期", "傳票號碼", "傳票項次", "科目代號", "科目名稱", "摘要", "金額", "借方旗標", "建立人員", "傳票核准日", "人工傳票"];
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
            ("voucherNumber", "傳票號碼", "text"), ("accountNumber", "會計科目編號", "text"),
            ("accountName", "會計科目名稱", "text"), ("debit", "借方金額", "amount"),
            ("credit", "貸方金額", "amount"), ("glDate", "總帳日期", "date"),
            ("approvalDate", "傳票核准日", "date"), ("createdBy", "傳票建立人員", "text"),
            ("approvedBy", "傳票核准人員", "text"), ("description", "傳票摘要", "text")
        }, plan.Columns.Select(column => (column.Key, column.Label, column.ValueType)).ToArray());
        Assert.All(plan.Columns, column => Assert.False(column.IsCustom));
    }

    [Fact]
    public void ConfirmedTbPreview_UsesUnifiedTbNameAndKey()
    {
        // 裁定 2026-10-08-04：資料名稱全系統統一，已確認配對的試算表叫 TB，資料集的鍵是 tb。
        Assert.Equal("TB", DataPreviewDatasetLabels.MainTabs.Single(tab => tab.Dataset == "tb").Label);
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
        // 裁定 2026-10-08-04：資料名稱全系統統一，納入測試的分錄叫 GL。
        // 這句的用字依裁定 2026-10-13-28，預期值換成確認後的句子（第一次失敗：收據 20261013-130645716-968482adb634494a97e2594c7e73decf）。
        Assert.Contains("GL 借方與貸方總額皆為 0", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("納入測試的分錄", error.Message, StringComparison.Ordinal);
        Assert.Contains("借方與貸方總額皆為 0", error.Message, StringComparison.Ordinal);
        Assert.Contains("請確認所選金額欄是每列的分錄金額", error.Message, StringComparison.Ordinal);
        var store = new JsonFileProjectStore(new JetProjectFolder(host.ProjectsRoot));
        var project = Assert.Single(await store.ListAsync(CancellationToken.None));
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = project.ProjectId }));
        Assert.Equal(JsonValueKind.Null, loaded.GetProperty("mapping").GetProperty("gl").ValueKind);
    }
}
