using System.Text.Json;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// KCT 條件 A 的季底與查核期末視窗：前端讀回顯示自己計算視窗，必須和後端 <see cref="QuarterEndWindows"/>
/// 對同一張邊界表格給出相同答案；前端那一半在 tools/tests/frontend-workflow.test.cjs 讀同一個檔案。
/// </summary>
public sealed class QuarterEndWindowsFrontendParityTests
{
    [Fact]
    public void QuarterWindowsUseTheSameFixedBoundaryTableAsFrontend()
    {
        using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            TestRepositoryPaths.RepositoryRoot, "tools", "tests", "fixtures", "quarter-end-windows.json")));
        foreach (var row in data.RootElement.EnumerateArray())
        {
            var expected = row.GetProperty("expected").EnumerateArray().Select(v => v.GetString());
            var actual = QuarterEndWindows.Compute(row.GetProperty("start").GetString()!, row.GetProperty("end").GetString()!, row.GetProperty("days").GetInt32());
            Assert.Equal(expected, actual.Select(w => $"{w.FromIso}..{w.ToIso}"));
        }
    }
}
