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

    // 日期時間帶時差或 Z 時，取文字裡寫的那一天，不換成這台電腦的時間。+14:00 與 -12:00 換成任何時區的時間，
    // 至少有一個會換到前一天或後一天，所以不論電腦設在哪個時區，換算時間的寫法都會讓這組案例失敗。
    [Theory]
    [InlineData("2024-01-05T00:30:00+14:00")]
    [InlineData("2024-01-05T23:30:00-12:00")]
    [InlineData("2024-01-05T00:30:00+09:00")]
    [InlineData("2024-01-05T20:00:00Z")]
    public void TryNormalize_DateTimeWithOffset_TakesTheWrittenDate(string raw)
    {
        Assert.True(DateNormalizer.TryNormalize(raw, RocOn, out var onResult));
        Assert.Equal("2024-01-05", onResult);
        Assert.True(DateNormalizer.TryNormalize(raw, RocOff, out var offResult));
        Assert.Equal("2024-01-05", offResult);
    }

    // 年份在前、四位數年份、用「-」分隔的寫法和用「/」分隔的一樣，照寫出來的年份讀成日期，年份可以是 0001 到 9999；
    // 分隔符號混用也和「/」「.」混用一樣接受。答案都是手寫的年月日。
    [Theory]
    [InlineData("1899-6-11", "1899-06-11")]
    [InlineData("2101-6-11", "2101-06-11")]
    [InlineData("1000-1-1", "1000-01-01")]
    [InlineData("0001-1-1", "0001-01-01")]
    [InlineData("1899-06/11", "1899-06-11")]
    public void TryNormalize_YearFirstHyphenDates_ReadEveryYearLikeSlashDates(string raw, string expected)
    {
        Assert.True(DateNormalizer.TryNormalize(raw, RocOn, out var onResult));
        Assert.Equal(expected, onResult);
        Assert.True(DateNormalizer.TryNormalize(raw, RocOff, out var offResult));
        Assert.Equal(expected, offResult);
    }

    // 民國年開啟時，用「-」分隔的三位數民國年和用「/」分隔的一樣換成西元（114 + 1911 = 2025）；關閉時讀不成日期。
    [Theory]
    [InlineData("114-6-11", "2025-06-11")]
    [InlineData("114-06-11", "2025-06-11")]
    [InlineData("100-1-1", "2011-01-01")]
    [InlineData("199-12-31", "2110-12-31")]
    [InlineData("113-2-29", "2024-02-29")]   // 民國 113 年是西元 2024 年，閏年
    public void TryNormalize_RocYearWithHyphen_ConvertsOnlyWhenRocIsOn(string raw, string expected)
    {
        Assert.True(DateNormalizer.TryNormalize(raw, RocOn, out var onResult));
        Assert.Equal(expected, onResult);
        Assert.False(DateNormalizer.TryNormalize(raw, RocOff, out var offResult));
        Assert.Null(offResult);
    }

    // 帶時間、寫成年月日或英文月份的寫法仍只收 1900 到 2100 年，年份在這之外時兩種設定都讀不成日期。
    [Theory]
    [InlineData("1899/06/11 00:00:00")]
    [InlineData("2101-06-11T08:30:00")]
    [InlineData("1895年2月2日")]
    [InlineData("1899-6-11 08:30")]
    public void TryNormalize_TimeOrWordedDatesOutside1900Through2100_StayUnrecognized(string raw)
    {
        Assert.False(DateNormalizer.TryNormalize(raw, RocOn, out var onResult));
        Assert.Null(onResult);
        Assert.False(DateNormalizer.TryNormalize(raw, RocOff, out var offResult));
        Assert.Null(offResult);
    }

    [Fact]
    public void TryNormalize_BlankIsLegalNoDate()
    {
        Assert.True(DateNormalizer.TryNormalize("   ", RocOn, out var isoDate));
        Assert.Null(isoDate);
    }

    // 無法辨識的日期要標出哪一段有問題：月或日不存在時標月或日，其他情形標整個值。位置都是手算的。
    [Theory]
    [InlineData("2025/13/05", DateProblemKind.NoSuchDate, 5, 2)]   // 沒有 13 月
    [InlineData("2025-02-30", DateProblemKind.NoSuchDate, 8, 2)]   // 2 月沒有 30 日
    [InlineData("2025.4.31", DateProblemKind.NoSuchDate, 7, 2)]    // 4 月沒有 31 日
    [InlineData("20251340", DateProblemKind.NoSuchDate, 4, 2)]     // 八位數寫法的 13 月
    [InlineData("20250199", DateProblemKind.NoSuchDate, 6, 2)]     // 八位數寫法的 99 日
    [InlineData("45292", DateProblemKind.Number, 0, 5)]
    [InlineData("2025.12", DateProblemKind.Number, 0, 7)]
    [InlineData("-5.0", DateProblemKind.Number, 0, 4)]
    [InlineData("05/01/25", DateProblemKind.AmbiguousOrder, 0, 8)]
    [InlineData("31/12/2025", DateProblemKind.AmbiguousOrder, 0, 10)]
    [InlineData("not-a-date", DateProblemKind.NotDate, 0, 10)]
    [InlineData("  2025/13/05  ", DateProblemKind.NoSuchDate, 5, 2)]  // 位置照去掉頭尾空白後的原文算
    public void DescribeUnrecognized_MarksThePartThatIsWrong(string raw, DateProblemKind kind, int start, int length)
    {
        Assert.False(DateNormalizer.TryNormalize(raw, RocOff, out _));

        Assert.Equal(new DateProblem(kind, start, length), DateNormalizer.DescribeUnrecognized(raw, RocOff));
    }

    // 原因照案件的民國年選項與那一年實際的天數判斷，和讀日期用同一套寫法。每個案例先確認這個值在那個設定下讀不成日期。
    // 位置都是手算的：三位數年份標年份；日曆上沒有的那一天標月或日，年份是 0 時標年份；
    // 帶時間、寫成年月日或英文月份的寫法年份不在 1900 到 2100 年時，標出原文裡的四位數年份。
    [Theory]
    [InlineData("114/6/11", false, DateProblemKind.YearTooShort, 0, 3)]
    [InlineData("114-6-11", false, DateProblemKind.YearTooShort, 0, 3)]
    [InlineData("200/6/11", true, DateProblemKind.YearTooShort, 0, 3)]
    [InlineData("112/2/29", true, DateProblemKind.NoSuchDate, 6, 2)]    // 民國 112 年是 2023 年，2 月只有 28 天
    [InlineData("114/2/29", true, DateProblemKind.NoSuchDate, 6, 2)]
    [InlineData("1900/2/29", true, DateProblemKind.NoSuchDate, 7, 2)]   // 1900 年不是閏年
    [InlineData("2025/2/29", true, DateProblemKind.NoSuchDate, 7, 2)]
    [InlineData("1140230", true, DateProblemKind.NoSuchDate, 5, 2)]
    [InlineData("1141315", true, DateProblemKind.NoSuchDate, 3, 2)]
    [InlineData("20250230", true, DateProblemKind.NoSuchDate, 6, 2)]
    [InlineData("01140230", true, DateProblemKind.NoSuchDate, 6, 2)]
    [InlineData("0000/6/11", true, DateProblemKind.NoSuchDate, 0, 4)]   // 沒有 0 年
    [InlineData("2025/02/30 08:00", true, DateProblemKind.NoSuchDate, 8, 2)]
    [InlineData("2025/13/05 08:00", true, DateProblemKind.NoSuchDate, 5, 2)]
    [InlineData("1141315", false, DateProblemKind.Number, 0, 7)]        // 民國年關閉時只是七位數的數字
    [InlineData("1899/6/11 08:30", true, DateProblemKind.LooseYearOutOfRange, 0, 4)]
    [InlineData("1895年2月2日", true, DateProblemKind.LooseYearOutOfRange, 0, 4)]
    [InlineData("Jan 1, 1899", true, DateProblemKind.LooseYearOutOfRange, 7, 4)]
    public void DescribeUnrecognized_FollowsTheRocSettingAndTheDaysOfThatYear(
        string raw, bool rocEnabled, DateProblemKind kind, int start, int length)
    {
        var options = rocEnabled ? RocOn : RocOff;
        Assert.False(DateNormalizer.TryNormalize(raw, options, out _));

        Assert.Equal(new DateProblem(kind, start, length), DateNormalizer.DescribeUnrecognized(raw, options));
    }

    // 日期後面接時間或時差時，原因和不接時的同一種寫法相同：日曆上沒有的那一天標出月或日，年份多打標出年份；
    // 三位數年份接時間時讀不成日期，不論民國年選項開或關都標出年份。每個案例先確認這個值在那個設定下讀不成日期，位置都是手算的。
    [Theory]
    [InlineData("2025-02-30T08:00:00Z", true, DateProblemKind.NoSuchDate, 8, 2)]
    [InlineData("2025-02-30T08:00:00+08:00", true, DateProblemKind.NoSuchDate, 8, 2)]
    [InlineData("2025-13-05T08:00:00Z", false, DateProblemKind.NoSuchDate, 5, 2)]
    [InlineData("99999-02-01T08:30:00Z", true, DateProblemKind.YearTooLong, 0, 5)]
    [InlineData("114/6/11 08:30", true, DateProblemKind.YearTooShort, 0, 3)]
    [InlineData("114/6/11 08:30", false, DateProblemKind.YearTooShort, 0, 3)]
    public void DescribeUnrecognized_DateWithTimeOrOffset_GivesTheSameReasonAsWithout(
        string raw, bool rocEnabled, DateProblemKind kind, int start, int length)
    {
        var options = rocEnabled ? RocOn : RocOff;
        Assert.False(DateNormalizer.TryNormalize(raw, options, out _));

        Assert.Equal(new DateProblem(kind, start, length), DateNormalizer.DescribeUnrecognized(raw, options));
    }

    // 年份多打成五位數以上時，JET 不猜它是哪一年，仍是無法辨識的日期；只標出年份那一段，問題種類是年份超過四位數。
    // 月日寫錯也一樣先標年份。位置都是手算的，照去掉頭尾空白後的原文算。
    [Theory]
    [InlineData("99999/02/01", 0, 5)]
    [InlineData("20255-1-1", 0, 5)]
    [InlineData("202501.02.03", 0, 6)]
    [InlineData("00114/06/11", 0, 5)]
    [InlineData("99999/13/45", 0, 5)]
    [InlineData("  99999/02/01 ", 0, 5)]
    // 四位數年份的日期後面接時間也讀得成，年份多打一位時同樣只標年份。
    [InlineData("99999/02/01 00:00:00", 0, 5)]
    [InlineData("99999-02-01T08:30:00", 0, 5)]
    [InlineData("20255/6/11 08:30", 0, 5)]
    public void DescribeUnrecognized_YearLongerThanFourDigits_MarksTheYear(string raw, int start, int length)
    {
        Assert.False(DateNormalizer.TryNormalize(raw, RocOn, out _));
        Assert.False(DateNormalizer.TryNormalize(raw, RocOff, out _));

        Assert.Equal(new DateProblem(DateProblemKind.YearTooLong, start, length), DateNormalizer.DescribeUnrecognized(raw, RocOn));
    }

    // 全形數字不是常見的日期寫法，半形與全形混用也一樣：兩種民國年設定都讀不成日期，
    // 日期提醒照「不是日期」標出整個值，判斷時不能出錯。
    [Theory]
    [InlineData("２０２５/０６/１１")]
    [InlineData("２０２５-０６-１１")]
    [InlineData("２０２５０６１１")]
    [InlineData("１１４/０６/１１")]
    [InlineData("１１４０６１１")]
    [InlineData("2025/０６/11")]
    public void FullWidthDigitDates_AreUnrecognizedAndMarkedAsNotDate(string raw)
    {
        var readWithRoc = true;
        var readWithoutRoc = true;
        DateProblem problem = default;
        var error = Record.Exception(() =>
        {
            readWithRoc = DateNormalizer.TryNormalize(raw, RocOn, out _);
            readWithoutRoc = DateNormalizer.TryNormalize(raw, RocOff, out _);
            problem = DateNormalizer.DescribeUnrecognized(raw, RocOn);
        });

        Assert.Null(error);
        Assert.False(readWithRoc);
        Assert.False(readWithoutRoc);
        Assert.Equal(new DateProblem(DateProblemKind.NotDate, 0, raw.Length), problem);
    }

    [Theory]
    [InlineData("1899-12-31", true)]
    [InlineData("1900-01-01", false)]
    [InlineData("2100-12-31", false)]
    [InlineData("2101-01-01", true)]
    [InlineData("9999-12-31", true)]
    [InlineData("0114-06-11", true)]
    public void HasUnusualYear_IsTrueOutside1900Through2100(string isoDate, bool expected) =>
        Assert.Equal(expected, DateNormalizer.HasUnusualYear(isoDate));

    // 讀取器對換算不出日期的日期型儲存格，交給投影帶小數點的序列文字；它是純數字，所以讀不成日期，
    // 確認配對時列為無法辨識的日期（工作流程見 ExcelNativeDateImportTests）。
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
