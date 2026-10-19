using System.Text.RegularExpressions;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 跨資料庫一致性計畫第三節 C：投影時的共用文字上限（<see cref="TextStorageLimits"/>）必須等於 SQL Server
/// 建表語法的欄寬。只讀建表語法文字，不需要連線，所以每次 Public 都會執行；兩邊任一邊改了沒有同步就會失敗。
/// </summary>
public sealed class SqlServerTextStorageLimitTests
{
    public static TheoryData<string, int> Columns => new()
    {
        { "document_number", TextStorageLimits.KeyUtf16CodeUnits },
        { "account_code", TextStorageLimits.KeyUtf16CodeUnits },
        { "line_item", TextStorageLimits.TextUtf16CodeUnits },
        { "account_name", TextStorageLimits.TextUtf16CodeUnits },
        { "source_module", TextStorageLimits.TextUtf16CodeUnits },
        { "created_by", TextStorageLimits.TextUtf16CodeUnits },
        { "approved_by", TextStorageLimits.TextUtf16CodeUnits },
        { "posting_status", TextStorageLimits.TextUtf16CodeUnits },
        { "text_value", TextStorageLimits.KeyUtf16CodeUnits },
        { "day_name", TextStorageLimits.CalendarDayNameUtf16CodeUnits }
    };

    [Theory]
    [MemberData(nameof(Columns))]
    public void SchemaColumnWidth_EqualsSharedProjectionLimit(string column, int limit)
    {
        var widths = Regex.Matches(
                SqlServerProjectDatabase.SchemaSql,
                $@"\b{column}\s+NVARCHAR\((\d+)\)",
                RegexOptions.CultureInvariant)
            .Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Distinct()
            .ToArray();

        Assert.Equal(new[] { limit }, widths);
    }

    [Fact]
    public void AuthorizedPreparerNameWidth_EqualsSharedKeyLimit()
    {
        var match = Regex.Match(
            SqlServerProjectDatabase.SchemaSql,
            @"target_authorized_preparer \(\s*name NVARCHAR\((\d+)\)",
            RegexOptions.CultureInvariant);

        Assert.True(match.Success);
        Assert.Equal(TextStorageLimits.KeyUtf16CodeUnits.ToString(System.Globalization.CultureInfo.InvariantCulture), match.Groups[1].Value);
    }

    /// <summary>情境名稱欄寬和三種資料庫共用的情境名稱上限一致（使用者 2026-10-19 選「三種共用 400 字上限」）。</summary>
    [Fact]
    public void ScenarioNameColumnWidth_EqualsSharedScenarioNameLimit()
    {
        var match = Regex.Match(
            SqlServerProjectDatabase.SchemaSql,
            @"config_filter_scenario\s*\(\s*position\s+INT PRIMARY KEY,\s*name\s+NVARCHAR\((\d+)\)",
            RegexOptions.CultureInvariant);

        Assert.True(match.Success);
        Assert.Equal(
            FilterScenarioLimits.MaxNameLength,
            int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
    }
}
