using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class UserFeedbackManualAutoProjectionTests
{
    [Theory]
    [InlineData("", "是空白")]
    [InlineData("UNKNOWN", "未歸類為人工或自動")]
    public void ProjectionFailure_ExplainsManualAutoValueAndCorrection(string raw, string expected)
    {
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GlMappingKeys.DocNum] = "傳票號碼",
            [GlMappingKeys.PostDate] = "過帳日期",
            [GlMappingKeys.AccNum] = "科目編號",
            [GlMappingKeys.AccName] = "科目名稱",
            [GlMappingKeys.Description] = "摘要",
            [GlMappingKeys.Amount] = "金額",
            [GlMappingKeys.Manual] = "人工自動代碼"
        };
        var options = GlMappingOptions.NormalizeLegacy(mapping) with
        {
            ManualAutoPolicy = new GlManualAutoPolicy(["M"], ["A"])
        };
        var spec = new GlMappingSpec(mapping, GlAmountMode.SignedAmount) { Options = options };
        var row = new StagingRow(
            7,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["傳票號碼"] = "JV-001",
                ["過帳日期"] = "2026-01-15",
                ["科目編號"] = "1000",
                ["科目名稱"] = "現金",
                ["摘要"] = "測試",
                ["金額"] = "1",
                ["人工自動代碼"] = raw
            });

        Assert.False(GlRowProjector.TryProject(
            row,
            spec,
            ProjectDocument.DefaultMoneyScale,
            out _,
            out var error));
        Assert.NotNull(error);
        Assert.Contains(expected, error.Reason, StringComparison.Ordinal);
        Assert.Contains("回到欄位配對", error.Reason, StringComparison.Ordinal);
    }
}
