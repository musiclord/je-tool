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
                all.Add(r.GetProperty("accountCode").GetString()!);
            var nc = page.GetProperty("nextCursor");
            cursor = nc.ValueKind == JsonValueKind.Null ? null : nc.GetString();
        } while (cursor is not null);

        // gl 側限本期(§2)、tb 側取全部(TB 本身即本期變動)——與完整性 CTE 同口徑;期外對照組不入 gl 彙總。
        var total = await DemoProjectPipeline.QueryScalarAsync(fixture.Host, fixture.ProjectId,
            "WITH gl AS (SELECT account_code, SUM(amount_scaled) s FROM target_gl_entry " +
            "  WHERE post_date >= '2025-01-01' AND post_date <= '2025-12-31' GROUP BY account_code), " +
            "tb AS (SELECT account_code, SUM(change_amount_scaled) s FROM target_tb_balance GROUP BY account_code) " +
            "SELECT COUNT(*) FROM (" +
            " SELECT t.account_code FROM tb t LEFT JOIN gl ON gl.account_code=t.account_code " +
            "   WHERE COALESCE(gl.s,0) <> t.s " +
            " UNION ALL " +
            " SELECT g.account_code FROM gl g LEFT JOIN tb ON tb.account_code=g.account_code " +
            "   WHERE tb.account_code IS NULL AND g.s <> 0) x;");

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

        // 第 9 批中低 13：>= 0 對陣列長度永遠成立。Demo TB 逐科目取本期 GL 同額彙總，
        // 兩張期外傳票不計入任一側，因此首頁固定沒有差異，也沒有下一頁。
        Assert.Equal(0, page.GetProperty("rows").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
    }
}
