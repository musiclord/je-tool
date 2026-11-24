using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 排除補班日是看每一筆分錄自己的日期：補班日是上班日，那一天入帳的分錄不算在週末入帳，
/// 不論同一個傳票號碼有沒有其他日期的分錄。要「整張傳票至少有一筆不在補班日才列入」時，另外加整張傳票條件。
///
/// 2025-03-01 與 2025-03-08 是星期六，2025-03-12 是星期三；補班日清單只有 2025-03-01。
///   V1 第 1 列 2025-03-01（補班的星期六），第 2 列 2025-03-12（同一個號碼又用在平日）
///   V2 兩列都在 2025-03-01
///   V3 兩列都在 2025-03-08（一般的星期六）
/// </summary>
public sealed class MakeupDayRowLevelTests
{
    private const string Seed =
        """
        INSERT INTO gl(batch_id,source_row_number,voucher_number,line_item,gl_date,approval_date,
            account_number,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_excluded)
        VALUES ('makeup',1,'V1','1','2025-03-01','2025-03-01','1000', 100,100,0,'DEBIT',0),
               ('makeup',2,'V1','2','2025-03-12','2025-03-12','2000',-100,0,100,'CREDIT',0),
               ('makeup',3,'V2','1','2025-03-01','2025-03-01','1000', 100,100,0,'DEBIT',0),
               ('makeup',4,'V2','2','2025-03-01','2025-03-01','2000',-100,0,100,'CREDIT',0),
               ('makeup',5,'V3','1','2025-03-08','2025-03-08','1000', 100,100,0,'DEBIT',0),
               ('makeup',6,'V3','2','2025-03-08','2025-03-08','2000',-100,0,100,'CREDIT',0);
        INSERT INTO calendar_day(day_type,date) VALUES ('makeup','2025-03-01');
        """;

    private static readonly FilterConditionContext Context = new(100, null, "2025-01-01", "2025-12-31");

    private const string Weekend = """{"type":"fieldValue","field":"glDate","operator":"isWeekend"}""";
    private const string NotMakeupDay = """{"join":"AND","type":"fieldValue","field":"glDate","operator":"isNotMakeupDay"}""";
    private const string NonBusinessDay = """{"type":"fieldValue","field":"glDate","operator":"isNonBusinessDay"}""";
    private const string VoucherHasARowOffMakeupDays =
        """{"join":"AND","type":"voucher","quantifier":"any","conditions":[{"type":"fieldValue","field":"glDate","operator":"isNotMakeupDay"}]}""";

    private static async Task<string[]> HitsAsync(FilterPredicateProviderFixture fixture, string rules)
    {
        var result = await fixture.Repository.PreviewAsync(
            fixture.ProjectId, FilterCompletenessTests.Scenario(rules), Context, CancellationToken.None);
        return result.PreviewRows.Select(row => $"{row.VoucherNumber}|{row.LineItem}").Order(StringComparer.Ordinal).ToArray();
    }

    // V1 第 1 列在補班的星期六，不選入；同一個號碼另有平日的分錄也不改變這一列的判斷。
    [Fact]
    public async Task WeekendExcludingMakeupDays_IsDecidedRowByRow()
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync("sqlite", Seed);

        Assert.Equal(["V1|1", "V2|1", "V2|2", "V3|1", "V3|2"], await HitsAsync(fixture, Weekend));
        Assert.Equal(["V3|1", "V3|2"], await HitsAsync(fixture, Weekend + "," + NotMakeupDay));
        Assert.Equal(["V3|1", "V3|2"], await HitsAsync(fixture, NonBusinessDay));
    }

    // 改用整張傳票條件時，V1 有一列不在補班日，補班星期六那一列就會列入；V2 整張都在補班日，仍然不列入。
    [Fact]
    public async Task WholeVoucherCondition_KeepsAWeekendRowWhenTheVoucherHasAnotherRowOffMakeupDays()
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync("sqlite", Seed);

        Assert.Equal(["V1|1", "V3|1", "V3|2"], await HitsAsync(fixture, Weekend + "," + VoucherHasARowOffMakeupDays));
    }
}
