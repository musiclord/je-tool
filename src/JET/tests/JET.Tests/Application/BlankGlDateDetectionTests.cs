using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class BlankGlDateDetectionTests(DemoProjectFixture fixture)
    : IClassFixture<DemoProjectFixture>
{
    [Fact]
    public async Task Validate_BlankGlDates_AreSourceQualityAndBlankRecordsCategoryIsClosed()
    {
        // 總帳日期空白的列依裁定 2026-10-21-04 記成 blank_gl_date（原本記成日期不符的 period），斷言不變。
        await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "INSERT INTO gl " +
            "(batch_id, source_row_number, voucher_number, line_item, gl_date, account_number, account_name, " +
            " description, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr, " +
            " is_excluded, exclusion_reason) " +
            "VALUES ('blank-gl-date', 1, 'BLANK-DATE-1', '1', NULL, '1000', '測試科目', " +
            $" '空白過帳日偵測', 10000, 10000, 0, 'DEBIT', 1, '{GlExcludedPopulation.BlankGlDateStorageReason}'); " +
            "SELECT 0;");

        var expectedBlankDates = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM gl WHERE gl_date IS NULL;");
        Assert.True(expectedBlankDates > 0, "demo fixture 必須含空白過帳日，避免偵測測試空轉。");

        var validation = await fixture.Host.DispatchAsync("validate.run");
        var blankRecords = validation.GetProperty("blankRecordsTest");
        Assert.False(blankRecords.TryGetProperty("blankGlDateCount", out _));
        Assert.Equal(
            expectedBlankDates,
            validation.GetProperty("sourceQuality").GetProperty("findingCount").GetInt64());

        var expectedBlankAccountNumbers = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM gl WHERE gl_date >= '2025-01-01' AND gl_date <= '2025-12-31' " +
            "AND (account_number IS NULL OR TRIM(account_number) = '');");
        Assert.Equal(expectedBlankAccountNumbers, blankRecords.GetProperty("blankAccountNumberCount").GetInt64());

        var invalid = await Assert.ThrowsAsync<JetActionException>(() =>
            fixture.Host.DispatchAsync(
                "query.blankRecordsPage",
                JsonSerializer.Serialize(new { category = "blankGlDate", pageSize = 2 })));
        Assert.Equal(JetErrorCodes.InvalidPayload, invalid.Code);
    }
}
