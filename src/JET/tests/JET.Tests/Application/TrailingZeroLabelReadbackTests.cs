using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;
using JET.Tests.Infrastructure;

namespace JET.Tests.Application;

public sealed class TrailingZeroLabelReadbackTests
{
    // 0 的個數照 IDEA 版由 GL 算出，共用夾具的尾零兩筆帶 zerosThreshold 欄：有寫時讀回「金額尾數連續 N 個 0」，
    // 沒寫時讀回規則名稱「分錄金額中有連續 0 的尾數」。
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
        // 共用夾具不含舊式連續零尾數的寫法（已儲存情境的舊寫法不讀回），所以每一筆都沒有型別標題。
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
        return JsonDocument.Parse(File.ReadAllText(TestRepositoryPaths.Fixture("filter-readback-prep-start-date-and-trailing-zeros.json")));
    }

    private static IEnumerable<JsonElement> TrailingZeroItems(JsonDocument json) =>
        json.RootElement.EnumerateArray().Where(item => item.GetProperty("id").GetString()!.StartsWith("trailing-zeros-", StringComparison.Ordinal));
}
