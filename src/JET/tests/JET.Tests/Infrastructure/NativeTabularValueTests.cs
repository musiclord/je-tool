using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// .xls 與 Access 原生值的文字表示要和 .xlsx 讀取器一致（2026-10-04 第二遍回饋審閱第 1 批，L09）。
/// .xlsx 的對照：布林寫 true 或 false；時間用 TimeSpan 的 "c" 格式；日期寫 yyyy-MM-dd。
/// </summary>
public sealed class NativeTabularValueTests
{
    [Fact]
    public void Boolean_MatchesXlsxTrueFalse()
    {
        Assert.Equal("true", NativeTabularValue.Read(true).Text);
        Assert.Equal("false", NativeTabularValue.Read(false).Text);
        Assert.Equal(LegacyFieldKind.Number, NativeTabularValue.Read(true).Kind);
    }

    [Fact]
    public void TimeOnlyDateTime_FromAccessZeroDate_BecomesTimeText()
    {
        // Access 只存時間的 Date/Time 欄位，經 OLE DB 讀出來的日期部分固定是 1899-12-30（OLE 日期零點）。
        var value = NativeTabularValue.Read(new DateTime(1899, 12, 30, 10, 30, 15));
        Assert.Equal("10:30:15", value.Text);
        Assert.Equal(LegacyFieldKind.Time, value.Kind);
    }

    [Fact]
    public void ZeroDateWithoutTime_StaysADate()
    {
        var value = NativeTabularValue.Read(new DateTime(1899, 12, 30));
        Assert.Equal("1899-12-30", value.Text);
        Assert.Equal(LegacyFieldKind.Date, value.Kind);
    }

    [Fact]
    public void ExplicitTimeOnly_UsesTheSameFormatAsXlsx()
    {
        var value = NativeTabularValue.Read(new DateTime(2025, 1, 1, 8, 5, 0), timeOnly: true);
        Assert.Equal(TimeSpan.FromHours(8).Add(TimeSpan.FromMinutes(5)).ToString("c"), value.Text);
        Assert.Equal("08:05:00", value.Text);
    }

    [Fact]
    public void DateWithTime_KeepsDateOnly()
    {
        Assert.Equal("2025-03-01", NativeTabularValue.Read(new DateTime(2025, 3, 1, 9, 0, 0)).Text);
    }
}
