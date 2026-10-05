using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// DateNormalizer 規格測試（guide §3.1.3）。
/// oracle：規格手算——民國年 = 西元年 − 1911（114 + 1911 = 2025）；OADate 45292 = 2024-01-01。
/// 設計技術：決策表（格式 × RocYearEnabled）＋ BVA（OADate、ROC 年、sanity guard 年份邊界）。
/// </summary>
public sealed class DateNormalizerTests
{
    private static readonly DateParseOptions RocOn = new(RocYearEnabled: true);
    private static readonly DateParseOptions RocOff = new(RocYearEnabled: false);

    // 決策表：通用格式（與 RocYearEnabled 無關，兩種開關結果一致）
    [Theory]
    [InlineData("2024-01-01", "2024-01-01")]   // ISO 直通
    [InlineData("2025/6/11", "2025-06-11")]    // yyyy/M/d
    [InlineData("2025.06.11", "2025-06-11")]   // yyyy.M.d
    [InlineData("20250611", "2025-06-11")]     // yyyyMMdd
    [InlineData("45292", "2024-01-01")]        // Excel 序列值
    [InlineData("2024/06/30", "2024-06-30")]   // 顯式西元（原寬鬆 fallback 案例，結果不變）
    public void TryNormalize_UniversalFormats_SameForBothRocSettings(string raw, string expected)
    {
        Assert.True(DateNormalizer.TryNormalize(raw, RocOn, out var onResult));
        Assert.Equal(expected, onResult);

        Assert.True(DateNormalizer.TryNormalize(raw, RocOff, out var offResult));
        Assert.Equal(expected, offResult);
    }

    // 決策表：民國年格式 × 開關
    [Theory]
    [InlineData("114/6/11", "2025-06-11")]   // 斜線分隔
    [InlineData("114.6.11", "2025-06-11")]   // 點分隔
    [InlineData("1140611", "2025-06-11")]    // 7 位數（與 Excel 序列值範圍重疊，民國年優先）
    [InlineData("100/1/1", "2011-01-01")]    // BVA：ROC 年下界 100（100 + 1911 = 2011）
    [InlineData("199/12/31", "2110-12-31")]  // BVA：ROC 年上界 199（199 + 1911 = 2110）
    public void TryNormalize_RocFormats_ConvertWhenEnabled(string raw, string expected)
    {
        Assert.True(DateNormalizer.TryNormalize(raw, RocOn, out var isoDate));
        Assert.Equal(expected, isoDate);
    }

    [Fact]
    public void TryNormalize_RocDisabled_SevenDigitOutsideSupportedSerialYears_IsRejected()
    {
        // 2026-10-04 第 9 批高4：序列值限 1900–2100，關閉民國年後不能接受原先的 5022 年日期。
        // 首次失敗：Public 20261004-100911120-57efb95a0cae44beb892ec3c2d058592；原始輸入不變。
        Assert.False(DateNormalizer.TryNormalize("1140611", RocOff, out var isoDate));
        Assert.Null(isoDate);
    }

    [Fact]
    public void TryNormalize_RocDisabled_SlashRocIsRejectedNotYear0114()
    {
        // 現行缺陷修復：關閉民國年時 114/6/11 必須被拒絕，不得被寬鬆 fallback 吞成西元 0114
        Assert.False(DateNormalizer.TryNormalize("114/6/11", RocOff, out _));
    }

    // 2026-10-04 第 9 批高4：改驗 1900–2100 的固定邊界，未呼叫被測程式反算答案。
    [Theory]
    [InlineData("2", "1900-01-01")]
    [InlineData("73415", "2100-12-31")]
    public void TryNormalize_OaDateSerialBoundaries_Accepted(string raw, string expected)
    {
        Assert.True(DateNormalizer.TryNormalize(raw, RocOn, out var isoDate));
        Assert.Equal(expected, isoDate);
    }

    [Theory]
    [InlineData("0")]        // BVA：序列值下鄰
    [InlineData("2958466")]  // BVA：序列值上鄰
    [InlineData("1")]        // 原先接受的 1899-12-31 改為拒絕。
    [InlineData("73416")]    // 2101-01-01。
    [InlineData("2958465")]  // 原先接受的 9999-12-31 改為拒絕。
    public void TryNormalize_OaDateSerialOutOfRange_Rejected(string raw)
    {
        Assert.False(DateNormalizer.TryNormalize(raw, RocOn, out _));
    }

    // 負向：歧義與非法格式（兩種開關都必須拒絕）
    [Theory]
    [InlineData("11/05/06")]   // 兩位數年：無法分辨民國/西元/日月序
    [InlineData("1/2/3")]      // 短年份三段式同屬歧義類
    [InlineData("20251301")]   // yyyyMMdd 形狀但月份 13 非法
    [InlineData("114/13/1")]   // ROC 月份非法（RocOff 時落入歧義三段式，同樣拒絕）
    [InlineData("not-a-date")]
    public void TryNormalize_AmbiguousOrInvalid_Rejected(string raw)
    {
        Assert.False(DateNormalizer.TryNormalize(raw, RocOn, out _));
        Assert.False(DateNormalizer.TryNormalize(raw, RocOff, out _));
    }

    [Fact]
    public void TryNormalize_RocEnabled_SevenDigitWithInvalidMonth_RejectedNotSerial()
    {
        // 1141315 命中民國年 7 位數形狀但月份 13 非法：民國年判定優先，不得回退成序列值。
        Assert.False(DateNormalizer.TryNormalize("1141315", RocOn, out _));

        // 同一高4裁定：民國年關閉時也不能退回 5024 年的超限序列日期。
        Assert.False(DateNormalizer.TryNormalize("1141315", RocOff, out _));
    }

    // BVA：寬鬆 fallback 的年份 sanity guard（1900–2100）
    [Theory]
    [InlineData("June 11, 2025", "2025-06-11")]   // 寬鬆 fallback 仍可用
    [InlineData("Jan 1, 1900", "1900-01-01")]     // guard 下界
    [InlineData("Dec 31, 2100", "2100-12-31")]    // guard 上界
    public void TryNormalize_LooseFallbackWithinGuard_Accepted(string raw, string expected)
    {
        Assert.True(DateNormalizer.TryNormalize(raw, RocOn, out var isoDate));
        Assert.Equal(expected, isoDate);
    }

    [Theory]
    [InlineData("Jan 1, 1899")]    // BVA：guard 下鄰
    [InlineData("Jan 1, 2101")]    // BVA：guard 上鄰
    public void TryNormalize_LooseFallbackOutsideGuard_Rejected(string raw)
    {
        Assert.False(DateNormalizer.TryNormalize(raw, RocOn, out _));
    }

    [Fact]
    public void TryNormalize_BlankIsLegalNoDate()
    {
        Assert.True(DateNormalizer.TryNormalize("   ", RocOn, out var isoDate));
        Assert.Null(isoDate);
    }
}
