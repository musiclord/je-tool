using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// NonWorkingDays:讀取解析(Resolve)與寫入驗證(Validate)。
/// canonical 編碼 .NET DayOfWeek 週日=0…週六=6;預設週六(6)、週日(0)。
/// </summary>
public sealed class NonWorkingDaysTests
{
    // 等價類:未設定(null)→預設週六、週日。
    [Fact]
    public void Resolve_Null_ReturnsDefaultSatSun()
    {
        Assert.Equal(new[] { 0, 6 }, NonWorkingDays.Resolve(null));
    }

    // 顯式空集合 ≠ null:代表整週工作日,Resolve 保留空。
    [Fact]
    public void Resolve_Empty_ReturnsEmpty()
    {
        Assert.Empty(NonWorkingDays.Resolve(System.Array.Empty<int>()));
    }

    // 去重 + 排序 + 防禦性過濾越界值(寫入端已驗證,此處為縱深):{5,1,3,1,9,-2} → {1,3,5}。
    [Fact]
    public void Resolve_UnsortedDuplicatesOutOfRange_NormalizesAndDropsInvalid()
    {
        Assert.Equal(new[] { 1, 3, 5 }, NonWorkingDays.Resolve(new[] { 5, 1, 3, 1, 9, -2 }));
    }

    // 合法 0–6 全保留(去重排序)。
    [Fact]
    public void Validate_FullWeekUnsorted_KeepsAllSortedDistinct()
    {
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, NonWorkingDays.Validate(new[] { 6, 5, 4, 3, 2, 1, 0 }));
    }

    // BVA 下界鄰:-1 拒絕。
    [Fact]
    public void Validate_BelowMin_ThrowsInvalidPayload()
    {
        var ex = Assert.Throws<JetActionException>(() => NonWorkingDays.Validate(new[] { -1 }));
        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
    }

    // BVA 上界鄰:7 拒絕。
    [Fact]
    public void Validate_AboveMax_ThrowsInvalidPayload()
    {
        var ex = Assert.Throws<JetActionException>(() => NonWorkingDays.Validate(new[] { 0, 7 }));
        Assert.Equal(JetErrorCodes.InvalidPayload, ex.Code);
    }

    // 空集合是合法寫入(整週工作日)。
    [Fact]
    public void Validate_Empty_ReturnsEmpty()
    {
        Assert.Empty(NonWorkingDays.Validate(System.Array.Empty<int>()));
    }
}
