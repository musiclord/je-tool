using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 前置條件不足時的原因句：還沒確認配對、沒有 TB，以及規則需要的欄位沒有配對，各自寫出缺什麼、到哪裡補。
/// </summary>
public sealed class PrerequisiteDisplayTextTests
{
    [Theory]
    [InlineData("validate.run")]
    [InlineData("prescreen.run")]
    [InlineData("filter.preview")]
    [InlineData("query.filterVoucherPage")]
    public async Task MissingPrerequisites_KeepErrorPriorityAndNameTheActualNextStep(string action)
    {
        using var host = new HandlerTestHost();
        await host.DispatchAsync("project.create", """{"projectName":"Missing-Mapping","periodStart":"2025-01-01","periodEnd":"2025-12-31","databaseProvider":"sqlite"}""");
        var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(action));
        if (action == "prescreen.run")
        {
            // prescreen checks completeness first; the display wording must not change error priority or codes.
            Assert.Equal(JetErrorCodes.CompletenessPrerequisiteFailed, error.Code);
            Assert.Equal("目前資料尚無可用的資料驗證結果，請重新執行資料驗證。", error.Message);
            return;
        }
        Assert.Equal(JetErrorCodes.GlMappingNotConfirmed, error.Code);
        Assert.Equal("尚未確認 GL 欄位配對，請先到第三步按「確認配對」。", error.Message);
    }

    [Fact]
    public void MissingTbAndOptionalFields_UseConfirmationLanguageWithoutChangingAvailability()
    {
        // 沒有 TB 時的兩種原因句用字已定案；提醒直接沿用原因句，不另寫一句。
        Assert.Equal("TB 欄位配對尚未確認，無法執行 GL 與 TB 逐科目比對。", AccountComparisonProcedure.MissingTbMappingReason);
        Assert.Equal("沒有匯入 TB，無法執行 GL 與 TB 逐科目比對。", AccountComparisonProcedure.MissingTbImportReason);
        // A completed N/A account comparison still stores count 0.
        // Missing TB is a warning that does not block later work.
        var decision = CompletenessEligibility.Evaluate(new(true, true, true, true, false, 0, AccountComparisonProcedure.MissingTbMappingReason));
        Assert.True(decision.IsEligible);
        Assert.Null(decision.Reason);
        Assert.Equal("TB 欄位配對尚未確認，無法執行 GL 與 TB 逐科目比對。 審計員可說明差異原因並繼續篩選與匯出。", decision.Warning);
        // 第四步的原因句由規則需求表產生，這裡讀表的句子。
        // 第四步與第五步要給同一個原因，句子寫出缺什麼、到哪裡補；
        // 「只檢查了總帳日期」放在週末與假日卡的核准側說明前面。
        var labels = AccountCategoryLabels.BuiltIn;
        Assert.Equal("「傳票核准日」尚未選擇來源欄，請回第三步欄位配對補上。", JetRuleRequirements.PrescreenSentence(
            new JetRulePrerequisite(JetRulePrerequisiteKind.GlField, JetFieldCatalog.GlApprovalDate), labels));
        Assert.Equal("「傳票建立人員」尚未選擇來源欄，請回第三步欄位配對補上。", JetRuleRequirements.PrescreenSentence(
            new JetRulePrerequisite(JetRulePrerequisiteKind.GlField, JetFieldCatalog.GlCreatedBy), labels));
        Assert.Equal("只檢查了總帳日期。「傳票核准日」尚未選擇來源欄，請回第三步欄位配對補上。", PrescreenProcedures.OnlyGlDateChecked
            + JetRuleRequirements.PrescreenSentence(
                new JetRulePrerequisite(JetRulePrerequisiteKind.GlField, JetFieldCatalog.GlApprovalDate), labels));
    }
}
