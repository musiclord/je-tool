using System.Text.Json;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// DateNormalizer 規格測試。
/// oracle：規格手算——民國年 = 西元年 − 1911（114 + 1911 = 2025）。純數字不當日期，
/// 45292 雖然是 OADate 的 2024-01-01 也不接受。前後端共用的固定答案在 tests/JET.Tests/Fixtures/date-normalization.json。
/// 設計技術：決策表（格式 × RocYearEnabled）＋ BVA（ROC 年、sanity guard 年份邊界、序列值邊界）。
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
    [InlineData("2024/06/30", "2024-06-30")]   // 顯式西元（原寬鬆 fallback 案例，結果不變）
    public void TryNormalize_UniversalFormats_SameForBothRocSettings(string raw, string expected)
    {
        Assert.True(DateNormalizer.TryNormalize(raw, RocOn, out var onResult));
        Assert.Equal(expected, onResult);

        Assert.True(DateNormalizer.TryNormalize(raw, RocOff, out var offResult));
        Assert.Equal(expected, offResult);
    }

    // 純數字不當成 Excel 序列值換成日期，兩種開關都拒絕。
    [Theory]
    [InlineData("2025")]      // 當成序列值會是 1905-07-17
    [InlineData("2025.12")]   // 當成序列值也是 1905 年
    [InlineData("45292")]     // 當成序列值會是 2024-01-01
    [InlineData("2")]         // 當成序列值會是 1900-01-01
    [InlineData("73415")]     // 當成序列值會是 2100-12-31
    [InlineData("73415.5")]
    [InlineData("050315")]    // 六位數日期，同屬純數字
    public void TryNormalize_PlainNumbers_Rejected(string raw)
    {
        Assert.False(DateNormalizer.TryNormalize(raw, RocOn, out var onResult));
        Assert.Null(onResult);
        Assert.False(DateNormalizer.TryNormalize(raw, RocOff, out var offResult));
        Assert.Null(offResult);
    }

    // 決策表：民國年格式 × 開關
    [Theory]
    [InlineData("114/6/11", "2025-06-11")]   // 斜線分隔
    [InlineData("114.6.11", "2025-06-11")]   // 點分隔
    [InlineData("1140611", "2025-06-11")]    // 7 位數（開啟時是民國年；關閉時是純數字而拒絕）
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
        // 純數字不當成序列值，一律拒絕；關閉民國年後，這個值不能讀成 5022 年的日期。
        Assert.False(DateNormalizer.TryNormalize("1140611", RocOff, out var isoDate));
        Assert.Null(isoDate);
    }

    [Fact]
    public void TryNormalize_RocDisabled_SlashRocIsRejectedNotYear0114()
    {
        // 現行缺陷修復：關閉民國年時 114/6/11 必須被拒絕，不得被寬鬆 fallback 吞成西元 0114
        Assert.False(DateNormalizer.TryNormalize("114/6/11", RocOff, out _));
    }

    // 驗 1900–2100 的固定邊界，未呼叫被測程式反算答案。
    // 序列值邊界「2」與「73415」不換成 1900-01-01 與 2100-12-31，
    // 和其他純數字一樣拒絕。
    [Theory]
    [InlineData("2")]
    [InlineData("73415")]
    public void TryNormalize_FormerOaDateSerialBoundaries_Rejected(string raw)
    {
        Assert.False(DateNormalizer.TryNormalize(raw, RocOn, out var isoDate));
        Assert.Null(isoDate);
    }

    // 純數字不當成序列值，所有純數字都拒絕。
    [Theory]
    [InlineData("0")]        // BVA：序列值下鄰
    [InlineData("2958466")]  // BVA：序列值上鄰
    [InlineData("1")]        // 當成序列值會是 1899-12-31，同樣拒絕。
    [InlineData("73416")]    // 2101-01-01。
    [InlineData("2958465")]  // 當成序列值會是 9999-12-31，同樣拒絕。
    public void TryNormalize_OaDateSerialOutOfRange_Rejected(string raw)
    {
        Assert.False(DateNormalizer.TryNormalize(raw, RocOn, out _));
    }

    // 帶千分位的數字視為無法辨識的日期，不當 Excel 序列值換算。
    [Theory]
    [InlineData("1,234")]    // 當成序列值會是 1903-05-18。
    [InlineData("45,292")]   // 去掉千分位是 2024-01-01 的序列值，同樣不接受。
    [InlineData("73,415.5")]
    // 逗號不在三位一組的位置，同樣不當成序列值（「12,31」是 1231），也不讓寬鬆解析讀成今年的 12 月 31 日。
    [InlineData("12,31")]
    [InlineData("3,15")]
    [InlineData("1,2024")]
    [InlineData("2024,12,31")] // 不能讀成 2024-12-31。
    public void TryNormalize_SerialWithThousandsSeparator_Rejected(string raw)
    {
        Assert.False(DateNormalizer.TryNormalize(raw, RocOn, out _));
        Assert.False(DateNormalizer.TryNormalize(raw, RocOff, out _));
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

        // 同樣受 1900 到 2100 的限制：民國年關閉時也不能退回 5024 年的超限序列日期。
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

    // 視為無法辨識的日期：寬鬆解析只接受寫出四位數年份與日的寫法，
    // 不自行補上執行當年、不補成 1 日，也不把一兩位數讀成年份。
    [Theory]
    [InlineData("12/31")]        // 補成執行當年
    [InlineData("12-31")]
    [InlineData("Dec 31")]
    [InlineData("December 31")]
    [InlineData("2025-12")]      // 補成 12 月 1 日
    [InlineData("2025-01")]      // 月份的 01 不能當成日
    [InlineData("Dec 2025")]
    [InlineData("31 Dec")]       // 讀成 2031-12-01
    [InlineData("1 Dec")]        // 讀成 2001-12-01
    [InlineData("Dec 31, 25")]   // 兩位數 25 讀成 2025 年
    [InlineData("11-Jun-25")]
    public void TryNormalize_IncompleteDate_Rejected(string raw)
    {
        Assert.False(DateNormalizer.TryNormalize(raw, RocOn, out _));
        Assert.False(DateNormalizer.TryNormalize(raw, RocOff, out _));
    }

    // 年份與日都寫出來的寫法照常接受；時間裡的數字不算成日，月與日相同時要各有一個數字。
    [Theory]
    [InlineData("June 11, 2025", "2025-06-11")]
    [InlineData("11 June 2025", "2025-06-11")]
    [InlineData("11-Jun-2025", "2025-06-11")]
    [InlineData("2025年6月11日", "2025-06-11")]
    [InlineData("2025 Dec 1", "2025-12-01")]
    [InlineData("2025/06/11 00:00:00", "2025-06-11")]
    [InlineData("2025/06/01 01:01:01", "2025-06-01")]
    [InlineData("2025-06-11T08:30:00", "2025-06-11")]
    [InlineData("2025-1-1", "2025-01-01")]
    public void TryNormalize_DateWithYearAndDayWritten_Accepted(string raw, string expected)
    {
        Assert.True(DateNormalizer.TryNormalize(raw, RocOn, out var onResult));
        Assert.Equal(expected, onResult);
        Assert.True(DateNormalizer.TryNormalize(raw, RocOff, out var offResult));
        Assert.Equal(expected, offResult);
    }

    [Fact]
    public void TryNormalize_BlankIsLegalNoDate()
    {
        Assert.True(DateNormalizer.TryNormalize("   ", RocOn, out var isoDate));
        Assert.Null(isoDate);
    }

    // 讀取器對超出 1900 到 2100 年的日期型儲存格，交給投影帶小數點的序列文字；它是純數字，所以拒絕，
    // 投影報「無法辨識為日期」並寫出列號（工作流程見 ExcelNativeDateImportTests）。
    [Theory]
    [InlineData(73416d, "73416.0")]       // 2101-01-01
    [InlineData(1140611d, "1140611.0")]   // 小數點讓它不會被讀成民國 114 年 6 月 11 日
    [InlineData(-1d, "-1.0")]
    public void TryNormalize_ReaderSerialText_IsRejectedUnderBothRocSettings(double serial, string expectedText)
    {
        var text = DateNormalizer.ExcelSerialText(serial);
        Assert.Equal(expectedText, text);
        Assert.False(DateNormalizer.TryNormalize(text, RocOn, out _));
        Assert.False(DateNormalizer.TryNormalize(text, RocOff, out _));
    }

    // 前後端讀同一份日期固定答案
    // tests/JET.Tests/Fixtures/date-normalization.json，前端的日期清單測試讀同一份；一邊改了規則另一邊就失敗。
    public static TheoryData<string> SharedFixtureRaws()
    {
        var data = new TheoryData<string>();
        foreach (var raw in SharedDateFixture.Cases.Keys) data.Add(raw);
        return data;
    }

    [Theory]
    [MemberData(nameof(SharedFixtureRaws))]
    public void TryNormalize_SharedDateFixture_MatchesBothRocSettings(string raw)
    {
        var expected = SharedDateFixture.Cases[raw];
        Assert.Equal(expected.RocEnabled, DateNormalizer.TryNormalize(raw, RocOn, out var onResult) ? onResult : null);
        Assert.Equal(expected.RocDisabled, DateNormalizer.TryNormalize(raw, RocOff, out var offResult) ? offResult : null);
    }
}

/// <summary>前後端共用的日期固定答案；每個原文對應民國年開啟與關閉兩種設定的結果，null 是無法辨識。</summary>
internal static class SharedDateFixture
{
    internal static IReadOnlyDictionary<string, (string? RocEnabled, string? RocDisabled)> Cases { get; } = Load();

    private static Dictionary<string, (string? RocEnabled, string? RocDisabled)> Load()
    {
        var path = TestRepositoryPaths.Fixture("date-normalization.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var cases = new Dictionary<string, (string? RocEnabled, string? RocDisabled)>(StringComparer.Ordinal);
        foreach (var item in json.RootElement.GetProperty("cases").EnumerateArray())
        {
            cases.Add(item.GetProperty("raw").GetString()!, (Text(item, "rocEnabled"), Text(item, "rocDisabled")));
        }

        return cases;
    }

    private static string? Text(JsonElement item, string name) =>
        item.GetProperty(name).ValueKind == JsonValueKind.Null ? null : item.GetProperty(name).GetString();
}
