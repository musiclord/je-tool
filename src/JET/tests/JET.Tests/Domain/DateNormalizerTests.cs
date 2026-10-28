using System.Text.Json;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// DateNormalizer 規格測試（guide §3.1.3）。
/// oracle：規格手算——民國年 = 西元年 − 1911（114 + 1911 = 2025）。純數字不當日期（使用者 2026-10-22 裁定），
/// 45292 以前是 OADate 2024-01-01。前後端共用的固定答案在 tools/tests/fixtures/date-normalization.json。
/// 設計技術：決策表（格式 × RocYearEnabled）＋ BVA（ROC 年、sanity guard 年份邊界、以前的序列值邊界）。
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

    // 預篩選收斂計畫階段七（使用者 2026-10-22 裁定）：純數字不再當成 Excel 序列值換成日期，兩種開關都拒絕。
    // 「45292」原本在上面的通用格式裡期待 2024-01-01；改成拒絕的第一次失敗：收據 20261024-044716893-6a6900c538364667949441681dcd3b6d。
    [Theory]
    [InlineData("2025")]      // 以前換成 1905-07-17
    [InlineData("2025.12")]   // 以前也是 1905 年
    [InlineData("45292")]     // 以前是 2024-01-01
    [InlineData("2")]         // 以前是 1900-01-01
    [InlineData("73415")]     // 以前是 2100-12-31
    [InlineData("73415.5")]
    [InlineData("050315")]    // 六位數日期，2026-10-19 曾選維持現狀，當時換成 2037-10-05；現在同屬純數字
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
        // 2026-10-04 第 9 批高4：序列值限 1900–2100，關閉民國年後不能接受原先的 5022 年日期。
        // 首次失敗：Public 20261004-100911120-57efb95a0cae44beb892ec3c2d058592；原始輸入不變。
        // 預篩選收斂計畫階段七（2026-10-22）後純數字一律拒絕，這一則的結果不變。
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
    // 預篩選收斂計畫階段七（使用者 2026-10-22 裁定）：序列值邊界「2」與「73415」不再換成 1900-01-01 與 2100-12-31，
    // 和其他純數字一樣拒絕；原始輸入不變。第一次失敗：收據 20261024-044716893-6a6900c538364667949441681dcd3b6d。
    [Theory]
    [InlineData("2")]
    [InlineData("73415")]
    public void TryNormalize_FormerOaDateSerialBoundaries_Rejected(string raw)
    {
        Assert.False(DateNormalizer.TryNormalize(raw, RocOn, out var isoDate));
        Assert.Null(isoDate);
    }

    // 以前是序列值範圍外而拒絕；階段七（2026-10-22）後所有純數字都拒絕，這些值的結果不變。
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

    // 使用者 2026-10-19 裁定：帶千分位的數字視為無法辨識的日期，不當 Excel 序列值換算。
    [Theory]
    [InlineData("1,234")]    // 以前換成 1903-05-18，再以期間外排除。
    [InlineData("45,292")]   // 去掉千分位是 2024-01-01 的序列值，同樣不接受。
    [InlineData("73,415.5")]
    // 逗號不在三位一組的位置，以前同樣當成序列值（「12,31」是 1231）；改動後一度被寬鬆解析讀成今年的 12 月 31 日。
    // 下面四列是獨立複審後新增的，修正前的失敗收據 20261019-133635513-8bc41e6e2ef140c48f9b0bb1e55f32f3。
    [InlineData("12,31")]
    [InlineData("3,15")]
    [InlineData("1,2024")]
    [InlineData("2024,12,31")] // 以前是超出範圍的序列值而拒絕，不能改讀成 2024-12-31。
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

    // 使用者 2026-10-19 裁定「視為無法辨識的日期」：寬鬆解析只接受寫出四位數年份與日的寫法，
    // 不自行補上執行當年、不補成 1 日，也不把一兩位數讀成年份。修正前這些值都被接受
    // （失敗收據 20261019-151920082-d008d7d7066b489ab4ea0b4c1907bcf1）。
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

    // 同一裁定：年份與日都寫出來的寫法照常接受；時間裡的數字不算成日，月與日相同時要各有一個數字。
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

    // 讀取器對超出 1900 到 2100 年的日期型儲存格，交給投影帶小數點的序列文字；拿掉序列值關卡後它仍是純數字而拒絕，
    // 投影照舊報「無法辨識為日期」並寫出列號（工作流程見 Batch9ExcelDateWorkflowTests）。改程式前後都通過，沒有可保存的第一次失敗。
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

    // 預篩選收斂計畫階段七（使用者 2026-10-22 裁定「共用固定答案」）：前後端讀同一份日期固定答案
    // tools/tests/fixtures/date-normalization.json，前端 frontend-shared-controls 的日期清單測試讀同一份；一邊改了規則另一邊就失敗。
    // 第一次失敗：收據 20261024-044716893-6a6900c538364667949441681dcd3b6d（純數字那一組當時仍換成日期）。
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
        var path = Path.Combine(TestRepositoryPaths.RepositoryRoot, "tools", "tests", "fixtures", "date-normalization.json");
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
