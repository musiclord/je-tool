using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// 預篩選「分錄金額中有連續 0 的尾數」的 0 的個數，照 IDEA 版 JET 的 R4 由 GL 決定（裁定 2026-11-05-25、2026-11-05-27）：
/// 借方金額合計除以金額倍率、再除以列入 GL 的筆數取整數部分，位數減一，至少 1 個、最多 12 個。預期值逐一手算，不由被測程式算出。
/// </summary>
public sealed class TrailingZeroThresholdTests
{
    // 改版前這裡是 DefaultZerosThreshold_IsSix，鎖住固定 6 個 0。照 IDEA 版改由 GL 算出後常數拿掉，
    // 改成核對算法（裁定 2026-11-05-25）。第一次失敗是建置失敗：收據 20261105-082826731-60a7ec59299d423aa561526e307e604b。
    [Theory]
    // IDEA 版說明的例子：借方平均 2,391,281 篩 000,000。兩筆分錄、借方合計 4,782,562，貸方那一筆算 0。
    [InlineData(4_782_562L * 10_000, 2L, 10_000, 6)]
    // 平均剛好 1,000,000：7 位數，6 個 0。
    [InlineData(2_000_000L * 10_000, 2L, 10_000, 6)]
    // 平均 999,999.9999：整數部分 999,999 是 6 位數，5 個 0；小數不進位。
    [InlineData(9_999_999_999L, 1L, 10_000, 5)]
    // 平均 100：3 位數，2 個 0。
    [InlineData(300L * 100, 3L, 100, 2)]
    // 平均 99：2 位數，1 個 0。
    [InlineData(99L * 100, 1L, 100, 1)]
    // 平均 9：IDEA 版算出 0 個 0、每一筆都會選進來；JET 至少 1 個。
    [InlineData(9L * 100, 1L, 100, 1)]
    // 平均 0.5 元：整數部分是 0，仍是 1 個。
    [InlineData(50L, 1L, 100, 1)]
    // 沒有分錄，或分錄全在貸方：平均當 0，1 個。
    [InlineData(0L, 0L, 10_000, 1)]
    [InlineData(0L, 5L, 10_000, 1)]
    // 平均 10^12：13 位數，12 個 0，剛好是上限。
    [InlineData(1_000_000_000_000L * 100, 1L, 100, 12)]
    // 平均 10^13：14 位數，IDEA 版算出 13 個，JET 最多 12 個。
    [InlineData(10_000_000_000_000L * 100, 1L, 100, 12)]
    public void FromGlTotals_UsesTheDigitsOfTheDebitAverageMinusOne(long debitScaled, long rowCount, int moneyScale, int expected)
    {
        var totals = new GlTotals(rowCount, rowCount, 0, 0, debitScaled, debitScaled);
        Assert.Equal(expected, TrailingZeroThreshold.FromGlTotals(totals, moneyScale));
    }

    [Fact]
    public void FromGlTotals_CountsOnlyEntriesInTheGlPopulation()
    {
        // 匯入 10 列，其中 8 列不在查核期間或過帳狀態不符，不列入 GL。平均只除以列入 GL 的 2 筆：4,782,562 / 2 = 2,391,281，6 個 0；
        // 如果誤除以匯入的 10 列，平均是 478,256，會變成 5 個 0。
        var totals = new GlTotals(
            PbcRowCount: 10,
            GlRowCount: 2,
            ExcludedByPeriodCount: 5,
            ExcludedByPostingStatusCount: 3,
            GlDebitScaled: 4_782_562L * 10_000,
            GlCreditScaled: 4_782_562L * 10_000);
        Assert.Equal(6, TrailingZeroThreshold.FromGlTotals(totals, 10_000));
    }

    [Fact]
    public void FromCommittedGl_ReadsTheTotalsCarriedByTheCommittedGlMapping()
    {
        var mapping = new CommittedMapping(DatasetKind.Gl, new Dictionary<string, string>(), "flag", "batch", DateTimeOffset.UnixEpoch);
        Assert.Null(TrailingZeroThreshold.FromCommittedGl(null, 10_000));
        Assert.Null(TrailingZeroThreshold.FromCommittedGl(mapping, 10_000));
        var withTotals = mapping with { GlTotals = new GlTotals(4, 4, 0, 0, 400_000L * 10_000, 400_000L * 10_000) };
        // 平均 100,000：6 位數，5 個 0。
        Assert.Equal(5, TrailingZeroThreshold.FromCommittedGl(withTotals, 10_000));
    }

    // 一條規則只有一個定義，尾零規則改由尾數述詞比對全 0 樣態，UnitModulus 隨之移除，原本兩則模數測試改鎖住樣態與位數範圍。
    // 第一次失敗：Build 收據 20261024-195205380-7ff4a86d8e454b30b80f473242fceabd（找不到 UnitModulus）。
    // 固定 6 個 0 的常數拿掉後，第二個樣態直接寫 6（裁定 2026-11-05-25）；第一次失敗是建置失敗：收據 20261105-082826731-60a7ec59299d423aa561526e307e604b。
    [Fact]
    public void ZeroPattern_HasOneZeroPerDigit()
    {
        Assert.Equal("000", TrailingZeroThreshold.ZeroPattern(3));
        Assert.Equal("000000", TrailingZeroThreshold.ZeroPattern(6));
    }

    [Fact]
    public void ZeroPattern_RejectsDigitsOutsideTheAllowedRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TrailingZeroThreshold.ZeroPattern(TrailingZeroThreshold.MinCustomDigits - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrailingZeroThreshold.ZeroPattern(TrailingZeroThreshold.MaxCustomDigits + 1));
    }
}
