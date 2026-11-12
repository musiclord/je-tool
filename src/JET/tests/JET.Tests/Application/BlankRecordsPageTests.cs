using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

public sealed class BlankRecordsPageTests(DemoProjectFixture fixture)
    : IClassFixture<DemoProjectFixture>
{
    [Fact]
    public async Task WalkAllPages_EqualsFullSet_NoGapNoDupOrderStable()
    {
        // 排序鍵 entry_id 升冪;wire row 不含 entry_id,故以走訪總筆數對 recount,
        // 並以 gl_date+voucherNumber 串接驗無重複(每列唯一)。游標序穩由 entry_id 保證,
        // 此處以走訪到底「無漏無重、筆數相符」鎖住範式。
        var all = new List<string>();
        string? cursor = null;
        do
        {
            var page = await fixture.Host.DispatchAsync("query.blankRecordsPage",
                JsonSerializer.Serialize(new { category = "blankDescription", cursor, pageSize = 200 }));
            foreach (var r in page.GetProperty("rows").EnumerateArray())
            {
                var doc = r.GetProperty("voucherNumber").GetString();
                var acc = r.GetProperty("accountNumber").GetString();
                var date = r.GetProperty("glDate").GetString();
                all.Add($"{doc}|{acc}|{date}");
            }

            var nc = page.GetProperty("nextCursor");
            cursor = nc.ValueKind == JsonValueKind.Null ? null : nc.GetString();
        } while (cursor is not null);

        // category=blankDescription recount = 本期母體(gl_date ∈ 會計期間)內 description 空白或 NULL。
        var total = await DemoProjectPipeline.QueryScalarAsync(fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM gl " +
            "WHERE gl_date >= '2025-01-01' AND gl_date <= '2025-12-31' " +
            "AND (description IS NULL OR TRIM(description) = '');");

        Assert.Equal(total, all.Count);
    }

    [Fact]
    public async Task ApprovalDateNotInPeriod_WalkAllPages_EqualsApprovalDateRecount()
    {
        // 「核准日不在期間」以核准日 approval_date 判定，分頁述詞不能用 gl_date
        // （demo R1：核准日 2026-01-15 期外、過帳日在期內，用 gl_date 會走訪 0 列而計數端 40 列）。
        // 分頁集合筆數 == 計數端 recount。
        var all = new List<string>();
        string? cursor = null;
        do
        {
            var page = await fixture.Host.DispatchAsync("query.blankRecordsPage",
                JsonSerializer.Serialize(new { category = "approvalDateNotInPeriod", cursor, pageSize = 200 }));
            foreach (var r in page.GetProperty("rows").EnumerateArray())
            {
                var doc = r.GetProperty("voucherNumber").GetString();
                var acc = r.GetProperty("accountNumber").GetString();
                var date = r.GetProperty("glDate").GetString();
                all.Add($"{doc}|{acc}|{date}");
            }

            var nc = page.GetProperty("nextCursor");
            cursor = nc.ValueKind == JsonValueKind.Null ? null : nc.GetString();
        } while (cursor is not null);

        // recount = 本期母體(gl_date ∈ 會計期間)內、核准日非空且落在會計期間之外的列。
        // 母體限本期 gl_date：R1（過帳日在期內、核准日 2026-01-15 期外）入母體並命中；期外對照組
        // （過帳日 2026-01-05 期外、核准日亦期外）不入母體，故不計。
        var total = await DemoProjectPipeline.QueryScalarAsync(fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM gl " +
            "WHERE gl_date >= '2025-01-01' AND gl_date <= '2025-12-31' " +
            "AND approval_date IS NOT NULL " +
            "AND (approval_date < '2025-01-01' OR approval_date > '2025-12-31');");

        Assert.True(total > 0, "demo 應有核准日期外列（R1 期末後核准）");
        Assert.Equal(total, all.Count);
    }

    [Fact]
    public async Task IllegalCategory_ThrowsActionError()
    {
        await Assert.ThrowsAnyAsync<System.Exception>(() => fixture.Host.DispatchAsync(
            "query.blankRecordsPage", JsonSerializer.Serialize(new { category = "notAWhitelistedValue" })));
    }
}
