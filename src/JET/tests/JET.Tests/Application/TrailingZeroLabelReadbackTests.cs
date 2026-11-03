using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class TrailingZeroLabelReadbackTests
{
    public static TheoryData<string, string, string, string?> SharedSentences()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tools", "tests", "fixtures", "filter-readback-prep-start-date-and-trailing-zeros.json")))
            directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("Repository fixture directory was not found.");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "tools", "tests", "fixtures", "filter-readback-prep-start-date-and-trailing-zeros.json")));
        var data = new TheoryData<string, string, string, string?>();
        foreach (var item in json.RootElement.EnumerateArray().Where(item => item.GetProperty("id").GetString()!.StartsWith("trailing-zeros-", StringComparison.Ordinal)))
            data.Add(item.GetProperty("id").GetString()!, item.GetProperty("scenario").GetRawText(),
                item.GetProperty("expected").GetString()!, item.TryGetProperty("ruleTypeLabel", out var label) ? label.GetString() : null);
        return data;
    }

    [Theory]
    [MemberData(nameof(SharedSentences))]
    public void TrailingZeroLabelsAndReadback_UseTheSameLiteralNames(string id, string json, string expected, string? typeLabel)
    {
        Assert.NotEmpty(id);
        using var scenario = JsonDocument.Parse(json);
        Assert.Equal(expected, FilterConditionRenderer.Render(scenario.RootElement));
        // 舊式連續零尾數的兩筆連同型別一起從共用夾具拿掉（裁定 2026-10-30-25：已儲存情境的舊寫法不再讀回），剩下的都沒有型別標題。
        // 第一次失敗：Focused 收據 20261030-081546753-58fab64421d94d9a9973dfa09cda1b98。
        Assert.Null(typeLabel);
        Assert.Equal(6, TrailingZeroThreshold.DefaultZerosThreshold);
    }
}
