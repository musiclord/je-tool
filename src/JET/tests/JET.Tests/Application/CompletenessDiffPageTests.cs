using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class CompletenessDiffPageTests(DemoProjectFixture fixture)
    : IClassFixture<DemoProjectFixture>
{
    [Fact]
    public async Task WalkAllPages_EqualsFullSet_NoGapNoDupOrderStable()
    {
        var all = new List<string>();
        string? cursor = null;
        do
        {
            var page = await fixture.Host.DispatchAsync("query.completenessDiffPage",
                JsonSerializer.Serialize(new { cursor, pageSize = 200 }));
            foreach (var r in page.GetProperty("rows").EnumerateArray())
                all.Add(r.GetProperty("accountNumber").GetString()!);
            var nc = page.GetProperty("nextCursor");
            cursor = nc.ValueKind == JsonValueKind.Null ? null : nc.GetString();
        } while (cursor is not null);

        // gl 側限本期(§2)、tb 側取全部(TB 本身即本期變動)——與完整性 CTE 同口徑;期外對照組不入 gl 彙總。
        var total = await DemoProjectPipeline.QueryScalarAsync(fixture.Host, fixture.ProjectId,
            "WITH gl_by_account AS (SELECT account_number, SUM(amount_scaled) s FROM gl " +
            "  WHERE gl_date >= '2025-01-01' AND gl_date <= '2025-12-31' GROUP BY account_number), " +
            "tb_by_account AS (SELECT account_number, SUM(change_amount_scaled) s FROM tb GROUP BY account_number) " +
            "SELECT COUNT(*) FROM (" +
            " SELECT t.account_number FROM tb_by_account t LEFT JOIN gl_by_account g ON g.account_number=t.account_number " +
            "   WHERE COALESCE(g.s,0) <> t.s " +
            " UNION ALL " +
            " SELECT g.account_number FROM gl_by_account g LEFT JOIN tb_by_account t ON t.account_number=g.account_number " +
            "   WHERE t.account_number IS NULL AND g.s <> 0) x;");

        Assert.Equal(total, all.Count);
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.True(all.SequenceEqual(all.OrderBy(x => x, System.StringComparer.Ordinal)));
    }

    [Fact]
    public async Task MalformedCursor_ThrowsInvalidPayload()
    {
        // 有傳 cursor 但無法解碼(非 Base64)→ fail loud,不靜默當首頁。
        var ex = await Assert.ThrowsAsync<JetActionException>(() => fixture.Host.DispatchAsync(
            "query.completenessDiffPage",
            JsonSerializer.Serialize(new { cursor = "not-a-valid-base64-cursor!!!", pageSize = 200 })));

        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
    }

    [Fact]
    public async Task OmittedCursor_ReturnsFirstPage()
    {
        // cursor 省略(首頁)維持正常,不丟例外。
        var page = await fixture.Host.DispatchAsync(
            "query.completenessDiffPage", JsonSerializer.Serialize(new { pageSize = 200 }));

        // >= 0 對陣列長度永遠成立。Demo TB 逐科目取本期 GL 同額彙總，
        // 兩張期外傳票不計入任一側，因此首頁固定沒有差異，也沒有下一頁。
        Assert.Equal(0, page.GetProperty("rows").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
    }
}
