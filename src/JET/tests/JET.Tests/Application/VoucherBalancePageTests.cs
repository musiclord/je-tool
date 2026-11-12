using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace JET.Tests.Application;

public sealed class VoucherBalancePageTests(DemoProjectFixture fixture)
    : IClassFixture<DemoProjectFixture>
{
    [Fact]
    public async Task WalkAllPages_EqualsFullSet_NoGapNoDupOrderStable()
    {
        var all = new List<string>();
        string? cursor = null;
        do
        {
            var page = await fixture.Host.DispatchAsync("query.voucherBalancePage",
                JsonSerializer.Serialize(new { cursor, pageSize = 200 }));
            foreach (var r in page.GetProperty("rows").EnumerateArray())
                all.Add(r.GetProperty("voucherNumber").GetString()!);
            var nc = page.GetProperty("nextCursor");
            cursor = nc.ValueKind == JsonValueKind.Null ? null : nc.GetString();
        } while (cursor is not null);

        // 借貸不平 = 本期母體(gl_date ∈ 會計期間)GROUP BY voucher_number HAVING SUM(amount_scaled)<>0 的組數。
        // recount 與分頁同口徑(限本期)——期外對照組的不平傳票不入母體,故不計。
        var total = await DemoProjectPipeline.QueryScalarAsync(fixture.Host, fixture.ProjectId,
            "SELECT COUNT(*) FROM (" +
            " SELECT voucher_number FROM gl " +
            " WHERE gl_date >= '2025-01-01' AND gl_date <= '2025-12-31' " +
            " GROUP BY voucher_number HAVING SUM(amount_scaled) <> 0) x;");

        Assert.Equal(total, all.Count);
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.True(all.SequenceEqual(all.OrderBy(x => x, System.StringComparer.Ordinal)));
    }
}
