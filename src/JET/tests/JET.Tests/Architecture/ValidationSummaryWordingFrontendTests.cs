using Xunit;

namespace JET.Tests.Architecture;

public sealed class ValidationSummaryWordingFrontendTests
{
    [Fact]
    public void NullRecordCategorySum_IsNamedRepeatCountedExceptionTotal()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "wwwroot",
            "js",
            "steps",
            "validate-step.js"));

        Assert.Contains("異常項次合計", source, StringComparison.Ordinal);
        Assert.Contains("同一分錄可能重複計入", source, StringComparison.Ordinal);
        Assert.DoesNotContain("欄位異常 ' +", source, StringComparison.Ordinal);
    }
}
