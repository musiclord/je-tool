using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class TrailingZeroLabelReadbackTests
{
    // 0 的個數改照 IDEA 版由 GL 算出（裁定 2026-11-05-25），共用夾具的尾零兩筆多一欄 zerosThreshold：有寫時讀回「金額尾數連續 N 個 0」，
    // 沒寫時讀回規則名稱「分錄金額中有連續 0 的尾數」。原本只有一筆固定 6 個 0 的讀回，常數拿掉後這裡也不再核對常數。
    // 第一次失敗是建置失敗：收據 20261105-082826731-60a7ec59299d423aa561526e307e604b。
    public static TheoryData<string, string, string, string?, int?> SharedSentences()
    {
        using var json = Fixture();
        var data = new TheoryData<string, string, string, string?, int?>();
        foreach (var item in TrailingZeroItems(json))
            data.Add(item.GetProperty("id").GetString()!, item.GetProperty("scenario").GetRawText(),
                item.GetProperty("expected").GetString()!, item.TryGetProperty("ruleTypeLabel", out var label) ? label.GetString() : null,
                item.TryGetProperty("zerosThreshold", out var zeros) ? zeros.GetInt32() : null);
        return data;
    }

    [Theory]
    [MemberData(nameof(SharedSentences))]
    public void TrailingZeroLabelsAndReadback_UseTheSameLiteralNames(string id, string json, string expected, string? typeLabel, int? zerosThreshold)
    {
        Assert.NotEmpty(id);
        using var scenario = JsonDocument.Parse(json);
        Assert.Equal(expected, FilterConditionRenderer.Render(scenario.RootElement, null, zerosThreshold: zerosThreshold));
        // 舊式連續零尾數的兩筆連同型別一起從共用夾具拿掉（裁定 2026-10-30-25：已儲存情境的舊寫法不再讀回），剩下的都沒有型別標題。
        // 第一次失敗：Focused 收據 20261030-081546753-58fab64421d94d9a9973dfa09cda1b98。
        Assert.Null(typeLabel);
    }

    [Fact]
    public void SharedSentences_CoverBothAKnownAndAnUnknownZeroCount()
    {
        using var json = Fixture();
        var items = TrailingZeroItems(json).ToList();
        Assert.Contains(items, item => item.TryGetProperty("zerosThreshold", out _));
        Assert.Contains(items, item => !item.TryGetProperty("zerosThreshold", out _));
    }

    private static JsonDocument Fixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tools", "tests", "fixtures", "filter-readback-prep-start-date-and-trailing-zeros.json")))
            directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("Repository fixture directory was not found.");
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "tools", "tests", "fixtures", "filter-readback-prep-start-date-and-trailing-zeros.json")));
    }

    private static IEnumerable<JsonElement> TrailingZeroItems(JsonDocument json) =>
        json.RootElement.EnumerateArray().Where(item => item.GetProperty("id").GetString()!.StartsWith("trailing-zeros-", StringComparison.Ordinal));
}
