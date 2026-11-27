using System.Text.Json;
using JET.Tests.Architecture;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 確認新案件的結果帶齊畫面要讀的欄位：確認 GL 欄位配對時就算出資料驗證要用的數字；
/// 預篩選有配對傳票建立人員時一定帶完整人數，沒有配對時人數是 null，另有 PreparerCountTests 核對。
/// 執行當下與重新開案讀回的結果，都帶齊金額級距、預篩選結果分布、人員分布與每條預篩選規則的欄位；
/// 開案時規則邏輯不是目前版本的結果不會交給畫面。
/// </summary>
public sealed class NewCaseResultFieldsTests
{
    private static readonly string[] RuleKeys =
    [
        "prepPeriodApproval", "suspiciousKeywords", "unexpectedAccountPair", "trailingZeros", "preparerSummary",
        "rareAccounts", "weekendActivity", "holidayActivity", "blankDescription", "nonAuthorizedPreparer"
    ];

    [Theory]
    [InlineData("sqlite")]
    public async Task NewCase_ValidationHasStoredTotals_AndPrescreenHasThePreparerCount(string provider)
    {
        using var host = new HandlerTestHost();
        await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);

        var validation = await host.DispatchAsync("validate.run");
        var storedTotals = validation.GetProperty("completenessTest").GetProperty("storedTotals");
        Assert.Equal(JsonValueKind.Object, storedTotals.ValueKind);
        Assert.Contains(storedTotals.GetProperty("rowCountMatch").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.Contains(storedTotals.GetProperty("amountMatch").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.Equal(JsonValueKind.Number, storedTotals.GetProperty("eligibleSource").GetProperty("rowCount").ValueKind);

        var preparerSummary = (await host.DispatchAsync("prescreen.run")).GetProperty("preparerSummary");
        Assert.Equal(JsonValueKind.Null, preparerSummary.GetProperty("naReason").ValueKind);
        Assert.Equal(JsonValueKind.Number, preparerSummary.GetProperty("totalPreparerCount").ValueKind);
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task NewCase_RunAndReopen_CarryEveryFieldTheOverviewAndTheValidateStepRead(string provider)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host, databaseProvider: provider);
        var validation = await host.DispatchAsync("validate.run");
        var prescreen = await host.DispatchAsync("prescreen.run");
        var loaded = await host.DispatchAsync("project.load", JsonSerializer.Serialize(new { projectId = context.ProjectId }));

        foreach (var result in new[] { validation, loaded.GetProperty("validationResult") })
        {
            Assert.Equal(AmountDistributionWireKeys.BinKeys.Length,
                result.GetProperty("amountDistribution").GetProperty("bins").GetArrayLength());
        }

        var ruleCount = prescreen.GetProperty("rulePeriod").GetProperty("rules").GetArrayLength();
        Assert.True(ruleCount > 0);
        foreach (var result in new[] { prescreen, loaded.GetProperty("prescreenResult") })
        {
            Assert.Equal(ruleCount, result.GetProperty("rulePeriod").GetProperty("rules").GetArrayLength());
            Assert.Equal(JsonValueKind.Object, result.GetProperty("concentration").ValueKind);
            foreach (var key in RuleKeys)
            {
                Assert.Equal(JsonValueKind.Object, result.GetProperty(key).ValueKind);
            }
        }
    }
}
