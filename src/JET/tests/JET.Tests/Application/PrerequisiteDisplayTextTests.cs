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
        await host.DispatchAsync("project.create", """{"projectName":"Batch8-Missing-Mapping","periodStart":"2025-01-01","periodEnd":"2025-12-31","databaseProvider":"sqlite"}""");
        var error = await Assert.ThrowsAsync<JetActionException>(() => host.DispatchAsync(action));
        if (action == "prescreen.run")
        {
            // New fixture correction after 20261004-091002406-dfa24f8c74e24391940f8999ec834708:
            // prescreen checks completeness first; a wording task must not change error priority or codes.
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
        // 裁定 2026-10-30-16 確認沒有 TB 時的兩種原因句，預期值換成新句子；提醒直接沿用原因句，不再另寫一句。
        // 第一次失敗：Focused 收據 20261030-233622780-566597ad2cc44fb3810e08946fb63078。
        // 裁定 2026-11-01-06 把這句的「還沒確認」改成「尚未確認」，其餘字不動；第一次失敗：Focused 收據 20261101-062343383-804cee29d9d9428f9e5f502ea4e7ae94。
        Assert.Equal("TB 欄位配對尚未確認，無法執行 GL 與 TB 逐科目比對。", AccountComparisonProcedure.MissingTbMappingReason);
        Assert.Equal("沒有匯入 TB，無法執行 GL 與 TB 逐科目比對。", AccountComparisonProcedure.MissingTbImportReason);
        // New fixture correction after 20261004-091447328-b0a62ddca30e458fb0489598e96c656e:
        // a completed N/A account comparison still stores count 0. Under ruling 2026-09-17-01,
        // missing TB is a warning that does not block later work; this test changes wording only.
        var decision = CompletenessEligibility.Evaluate(new(true, true, true, true, false, 0, AccountComparisonProcedure.MissingTbMappingReason));
        Assert.True(decision.IsEligible);
        Assert.Null(decision.Reason);
        Assert.Equal("TB 欄位配對尚未確認，無法執行 GL 與 TB 逐科目比對。 審計員可說明差異原因並繼續篩選與匯出。", decision.Warning);
        // 第四步的原因句改由規則需求表產生，原本的常數已拿掉，改讀表的句子。
        // 第一次失敗：收據 20261023-031856475-04f811a69e3346428de91091f0d02655（編譯失敗）。
        // 第四步與第五步要給同一個原因，句子寫出缺什麼、到哪裡補，預期值換成新句子；
        // 「只檢查了總帳日期」放在週末與假日卡的核准側說明前面。第一次失敗：Focused 收據
        // 20261023-032913610-af3152ec3aad4548b80c4ab69eb34cdc。
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
