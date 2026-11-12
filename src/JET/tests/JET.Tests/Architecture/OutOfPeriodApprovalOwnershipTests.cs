using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 「核准日不在期間」能不能檢查只判斷一次：資料驗證讀欄位目錄算出的可用欄位決定，驗證報告讀驗證結果，
/// 不自己看傳票核准日的配對模式。
/// </summary>
public sealed class OutOfPeriodApprovalOwnershipTests
{
    [Fact]
    public void ValidationDecidesFromTheFieldCatalog_AndTheReportReadsTheValidationResult()
    {
        var handler = ProductSources.Product("Application", "Handlers", "ValidateRunHandler.cs");
        Assert.Contains("JetFieldCatalog.AvailableGlFields(", handler, StringComparison.Ordinal);

        var writer = ProductSources.Product("Infrastructure", "Export", "LegacyReportWriter.Validation.cs");
        Assert.DoesNotContain("ApprovalDateMode", writer, StringComparison.Ordinal);
    }
}
