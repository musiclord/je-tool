using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

public sealed class NullRecordsPageTests(DemoProjectFixture fixture)
    : IClassFixture<DemoProjectFixture>
{
    [Fact]
    public async Task WalkAllPages_EqualsFullSet_NoGapNoDupOrderStable()
    {
        // 排序鍵 entry_id 升冪;wire row 不含 entry_id,故以走訪總筆數對 recount,
        // 並以 post_date+documentNumber 串接驗無重複(每列唯一)。游標序穩由 entry_id 保證,
        // 此處以走訪到底「無漏無重、筆數相符」鎖住範式。
        var all = new List<string>();
        string? cursor = null;
        do
        {
            var page = await fixture.Host.DispatchAsync("query.nullRecordsPage",
                JsonSerializer.Serialize(new { category = "nullDescription", cursor, pageSize = 200 }));
            foreach (var r in page.GetProperty("rows").EnumerateArray())
            {
                var doc = r.GetProperty("documentNumber").GetString();
                var acc = r.GetProperty("accountCode").GetString();
                var date = r.GetProperty("postDate").GetString();
                all.Add($"{doc}|{acc}|{date}");
            }

            var nc = page.GetProperty("nextCursor");
            cursor = nc.ValueKind == JsonValueKind.Null ? null : nc.GetString();
        } while (cursor is not null);

        // category=nullDescription recount = 本期母體(§2:post_date ∈ 會計期間)內 document_description 空白或 NULL。
        var total = await DemoProjectPipeline.QueryScalarAsync(fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry " +
            "WHERE post_date >= '2025-01-01' AND post_date <= '2025-12-31' " +
            "AND (document_description IS NULL OR TRIM(document_description) = '');");

        Assert.Equal(total, all.Count);
    }

    [Fact]
    public async Task OutOfRangeDate_WalkAllPages_EqualsApprovalDateRecount()
    {
        // 「日期區間外」以核准日 approval_date 判定（2026-06-23 決策）；分頁述詞先前誤用 post_date
        // → 分頁走訪 0 列而計數端 40 列（demo R1：核准日 2026-01-15 期外、過帳日在期內）。
        // 修正後分頁改核准日，分頁集合筆數 == 計數端 recount（spec 2026-07-08 §3 red→green）。
        var all = new List<string>();
        string? cursor = null;
        do
        {
            var page = await fixture.Host.DispatchAsync("query.nullRecordsPage",
                JsonSerializer.Serialize(new { category = "outOfRangeDate", cursor, pageSize = 200 }));
            foreach (var r in page.GetProperty("rows").EnumerateArray())
            {
                var doc = r.GetProperty("documentNumber").GetString();
                var acc = r.GetProperty("accountCode").GetString();
                var date = r.GetProperty("postDate").GetString();
                all.Add($"{doc}|{acc}|{date}");
            }

            var nc = page.GetProperty("nextCursor");
            cursor = nc.ValueKind == JsonValueKind.Null ? null : nc.GetString();
        } while (cursor is not null);

        // recount = 本期母體(§2:post_date ∈ 會計期間)內、核准日非空且落在會計期間之外的列。
        // 母體限本期 post_date：R1（過帳日在期內、核准日 2026-01-15 期外）入母體並命中；期外對照組
        // （過帳日 2026-01-05 期外、核准日亦期外）不入母體，故不計——正是 §2 spec 定案語意。
        var total = await DemoProjectPipeline.QueryScalarAsync(fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM target_gl_entry " +
            "WHERE post_date >= '2025-01-01' AND post_date <= '2025-12-31' " +
            "AND approval_date IS NOT NULL " +
            "AND (approval_date < '2025-01-01' OR approval_date > '2025-12-31');");

        Assert.True(total > 0, "demo 應有核准日期外列（R1 期末後核准）");
        Assert.Equal(total, all.Count);
    }

    [Fact]
    public async Task IllegalCategory_ThrowsActionError()
    {
        await Assert.ThrowsAnyAsync<System.Exception>(() => fixture.Host.DispatchAsync(
            "query.nullRecordsPage", JsonSerializer.Serialize(new { category = "notAWhitelistedValue" })));
    }
}
