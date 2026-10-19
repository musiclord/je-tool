using System.Globalization;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

/// <summary>
/// 低嚴重度事項 G（使用者 2026-10-19 要求處理）：Windows 地區格式的日曆設成中華民國曆時，
/// 沒有指定文化的 yyyy 會被當成民國年，閏年 2 月 29 日解析失敗，跨過 2 月底的日數也會算錯。
/// oracle：手算西元年的季末視窗與閏年日期。每個測試在 zh-TW 加中華民國曆下執行，結束後還原。
/// </summary>
public sealed class RocCalendarCultureTests
{
    [Fact]
    public void RocCalendar_ParsingWithoutCulture_ReadsTheYearAsRoc()
    {
        // 前提：這個環境確實會重現問題，下面的測試才不是空跑。民國 2024 年換算成西元是平年，沒有 2 月 29 日。
        Assert.False(UnderRocCalendar(() => DateOnly.TryParseExact("2024-02-29", "yyyy-MM-dd", out _)));
    }

    [Fact]
    public void QuarterEndWindows_LeapDayPeriodEnd_IsParsedAsGregorian()
    {
        Assert.Equal(["2024-02-25..2024-02-29"], UnderRocCalendar(() => WindowStrings("2024-01-01", "2024-02-29", 5)));
    }

    [Fact]
    public void QuarterEndWindows_WindowAcrossLeapDay_CountsTheLeapDay()
    {
        // 2024 是閏年：3/31 往前 35 天（含當日）從 2/26 起；誤當民國年時落在平年，會算成 2/25。
        Assert.Equal(["2024-02-26..2024-03-31"], UnderRocCalendar(() => WindowStrings("2024-01-01", "2024-03-31", 35)));
    }

    [Fact]
    public void FilterDateRange_LeapDayBound_IsAValidDate()
    {
        var rule = new FilterRuleSpec(FilterJoin.And, FilterRuleType.DateRange, null, "postDate", [],
            TextMatchMode.Contains, "2024-02-29", "2024-12-31", null, null, null, null);
        var scenario = new FilterScenarioSpec("閏日起算", "期間從閏日開始",
            [new FilterGroupSpec(FilterJoin.And, [rule])]);
        var context = new FilterValidationContext(HasLastPeriodStart: true, HasAccountMapping: false, HasAuthorizedPreparers: false);

        Assert.Empty(UnderRocCalendar(() => FilterScenarioValidator.Validate(scenario, context)));
    }

    [Fact]
    public void ReportWorkbookMetadata_LeapDayPeriodEnd_PassesTheInvariant()
    {
        var gl = new CommittedMapping(
            DatasetKind.Gl,
            new Dictionary<string, string>(StringComparer.Ordinal) { [GlMappingKeys.DocNum] = "Document" },
            GlAmountModeNames.Signed,
            "gl-batch",
            DateTimeOffset.UnixEpoch);
        var metadata = new ReportWorkbookMetadata("2024-01-01", "2024-02-29", TaxonomyRevision: 1, gl, TbMapping: null);

        var error = Record.Exception(() => UnderRocCalendar(() =>
        {
            ReportWorkbookMetadataInvariant.Validate(metadata);
            return true;
        }));

        Assert.Null(error);
    }

    [Fact]
    public void ProjectLockMessage_ShowsGregorianYear()
    {
        var lockedUtc = new DateTimeOffset(2024, 2, 29, 8, 30, 0, TimeSpan.Zero);

        var message = UnderRocCalendar(() => ProjectLockErrorMessage.Format("審計員", "PC-01", lockedUtc));

        Assert.Contains("2024-02-29 08:30", message, StringComparison.Ordinal);
    }

    // 使用者 2026-10-19 裁定防再犯：程式啟動時把日曆固定為西元，地區的其他設定不動。
    [Fact]
    public void ProcessCulture_RocCalendar_SwitchesOnlyTheCalendarToGregorian()
    {
        var roc = (CultureInfo)CultureInfo.GetCultureInfo("zh-TW").Clone();
        roc.DateTimeFormat.Calendar = new TaiwanCalendar();

        var adjusted = ProcessCulture.WithGregorianCalendar(roc);

        Assert.IsType<GregorianCalendar>(adjusted.DateTimeFormat.Calendar);
        Assert.Equal("zh-TW", adjusted.Name);
        Assert.Equal(roc.NumberFormat.NumberGroupSeparator, adjusted.NumberFormat.NumberGroupSeparator);
        Assert.True(DateOnly.TryParseExact("2024-02-29", "yyyy-MM-dd", adjusted, DateTimeStyles.None, out _));
        Assert.Equal("2024", new DateTime(2024, 2, 29).ToString("yyyy", adjusted));
        Assert.IsType<TaiwanCalendar>(roc.DateTimeFormat.Calendar);
    }

    [Fact]
    public void ProcessCulture_GregorianCalendar_IsReturnedAsIs()
    {
        var gregorian = CultureInfo.GetCultureInfo("zh-TW");

        Assert.Same(gregorian, ProcessCulture.WithGregorianCalendar(gregorian));
    }

    private static IReadOnlyList<string> WindowStrings(string start, string end, int days) =>
        QuarterEndWindows.Compute(start, end, days)
            .Select(w => $"{w.FromIso}..{w.ToIso}")
            .ToList();

    private static T UnderRocCalendar<T>(Func<T> action)
    {
        var original = CultureInfo.CurrentCulture;
        var roc = (CultureInfo)CultureInfo.GetCultureInfo("zh-TW").Clone();
        roc.DateTimeFormat.Calendar = new TaiwanCalendar();
        CultureInfo.CurrentCulture = roc;
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
