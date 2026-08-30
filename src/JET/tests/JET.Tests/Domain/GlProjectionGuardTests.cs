using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// 退化母體守門判定（jet-testing §1 Domain 純函式）。BVA：空母體不守門、
/// 非空且借貸皆 0 才退化、任一側非 0 即正常。對齊借貸欄誤配傳票總額的退化情境（逐列淨額恆 0）。
/// </summary>
public sealed class GlProjectionGuardTests
{
    [Theory]
    [InlineData(0, 0, 0, false)]      // 空母體：不屬守門範圍
    [InlineData(1, 0, 0, true)]       // 邊界：1 列、借貸皆 0 → 退化
    [InlineData(121121, 0, 0, true)]  // 大量列、借貸皆 0 的退化情境
    [InlineData(5, 100, 0, false)]    // 僅借方 → 正常
    [InlineData(5, 0, 100, false)]    // 僅貸方 → 正常
    [InlineData(5, 100, 100, false)]  // 借貸俱有 → 正常
    [InlineData(5, -100, 0, false)]   // 借方為負彙總（scaled 累計）→ 非 0，正常
    public void IsDegenerateAmountPopulation_FlagsOnlyNonEmptyAllZero(
        long projectedRowCount, long totalDebitScaled, long totalCreditScaled, bool expected)
    {
        Assert.Equal(
            expected,
            GlProjectionGuard.IsDegenerateAmountPopulation(projectedRowCount, totalDebitScaled, totalCreditScaled));
    }
}
