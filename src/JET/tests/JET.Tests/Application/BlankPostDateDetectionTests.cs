using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class BlankPostDateDetectionTests(DemoProjectFixture fixture)
    : IClassFixture<DemoProjectFixture>
{
    [Fact]
    public async Task Validate_BlankPostDates_AreSourceQualityAndNullRecordsCategoryIsClosed()
    {
        await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "INSERT INTO target_gl_entry " +
            "(batch_id, source_row_number, document_number, line_item, post_date, account_code, account_name, " +
            " document_description, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr, " +
            " is_effective, exclusion_reason) " +
            "VALUES ('blank-post-date', 1, 'BLANK-DATE-1', '1', NULL, '1000', '測試科目', " +
            $" '空白過帳日偵測', 10000, 10000, 0, 'DEBIT', 0, '{GlEffectivePopulation.PeriodStorageReason}'); " +
            "SELECT 0;");

        var expectedBlankDates = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry WHERE post_date IS NULL;");
        Assert.True(expectedBlankDates > 0, "demo fixture 必須含空白過帳日，避免偵測測試空轉。");

        var validation = await fixture.Host.DispatchAsync("validate.run");
        var nulls = validation.GetProperty("nullRecordsTest");
        Assert.False(nulls.TryGetProperty("nullPostDateCount", out _));
        Assert.Equal(
            expectedBlankDates,
            validation.GetProperty("sourceQuality").GetProperty("findingCount").GetInt64());

        var expectedNullAccounts = await DemoProjectPipeline.QueryScalarAsync(
            fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry WHERE post_date >= '2025-01-01' AND post_date <= '2025-12-31' " +
            "AND (account_code IS NULL OR TRIM(account_code) = '');");
        Assert.Equal(expectedNullAccounts, nulls.GetProperty("nullAccountCount").GetInt64());

        var invalid = await Assert.ThrowsAsync<JetActionException>(() =>
            fixture.Host.DispatchAsync(
                "query.nullRecordsPage",
                JsonSerializer.Serialize(new { category = "nullPostDate", pageSize = 2 })));
        Assert.Equal(JetErrorCodes.InvalidPayload, invalid.Code);
    }
}
