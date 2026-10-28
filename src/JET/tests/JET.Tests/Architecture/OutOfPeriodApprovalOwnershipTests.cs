using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 「核准日不在期間」能不能檢查只判斷一次：資料驗證讀欄位目錄算出的可用欄位決定，驗證報告讀驗證結果，
/// 不再自己看傳票核准日的配對模式（預篩選收斂計畫階段四追加，使用者 2026-10-23 裁定）。
/// 第一次失敗：Focused 收據 20261023-101129018-372fcff87fc14cc1b75a4e2299f5bb65（資料驗證還沒有讀欄位目錄）。
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
