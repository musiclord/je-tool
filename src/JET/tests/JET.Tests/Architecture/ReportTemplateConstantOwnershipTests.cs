using ClosedXML.Excel;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 報告範本固定的數量只定義一次：INF 樣本數在 AuditCore 一個常數，第四步驗證與 INF 報告都引用它，範本的樣本列數也要相符；
/// 條件篩選報告的情境列從第 5 列起，列數就是已存情境上限（技術債一輪，2026-10-22）。
/// </summary>
public sealed class ReportTemplateConstantOwnershipTests
{
    [Fact]
    public void InfSampleSize_IsOneAuditCoreConstant_MatchingTheTemplateRows()
    {
        var type = ProductSources.ProductType("JET.AuditCore.InfSampling");
        Assert.NotNull(type);
        var size = Assert.IsType<int>(type!.GetField("DefaultSampleSize")!.GetRawConstantValue());
        Assert.Equal(59, size);

        var handler = ProductSources.Product("Application", "Handlers", "ValidateRunHandler.cs");
        Assert.Contains("InfSampling.DefaultSampleSize", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("= 59;", handler, StringComparison.Ordinal);
        var report = ProductSources.Product("Infrastructure", "Export", "LegacyReportWriter.Inf.cs");
        Assert.Contains("InfSampling.DefaultSampleSize", report, StringComparison.Ordinal);
        Assert.DoesNotContain("= 59;", report, StringComparison.Ordinal);

        // 範本的樣本列從第 53 列起，A 欄寫「1.」到「59.」；常數和範本一起改才會一致。
        using var workbook = new XLWorkbook(ProductSources.TemplatePath("INFReport.xlsx"));
        var sheet = workbook.Worksheet(1);
        Assert.Equal("1.", sheet.Cell(53, 1).GetString());
        Assert.Equal($"{size}.", sheet.Cell(52 + size, 1).GetString());
        Assert.Equal(string.Empty, sheet.Cell(53 + size, 1).GetString());
    }

    [Fact]
    public void CriteriaReportTemplate_ReservesOneRowPerSavedScenario_FromRowFive()
    {
        using var workbook = new XLWorkbook(ProductSources.TemplatePath("CriteriaSelectionReport.xlsx"));
        var sheet = workbook.Worksheet("Summary Inforamtion");
        Assert.Equal("條件的內容", sheet.Cell(4, 2).GetString());
        const int firstScenarioRow = 5;
        for (var row = firstScenarioRow; row < firstScenarioRow + FilterScenarioLimits.MaxSavedScenarios; row++)
        {
            Assert.Equal(string.Empty, sheet.Cell(row, 1).GetString());
            Assert.Equal(string.Empty, sheet.Cell(row, 2).GetString());
        }

        var criteria = ProductSources.Product("Infrastructure", "Export", "LegacyReportWriter.Criteria.cs");
        Assert.Contains("CriteriaFirstScenarioRow = 5;", criteria, StringComparison.Ordinal);
    }
}
