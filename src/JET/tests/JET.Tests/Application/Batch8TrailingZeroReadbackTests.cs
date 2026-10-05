using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch8TrailingZeroReadbackTests
{
    public static TheoryData<string, string, string, string?> SharedSentences()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tools", "tests", "fixtures", "batch8-filter-readback.json")))
            directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("Repository fixture directory was not found.");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "tools", "tests", "fixtures", "batch8-filter-readback.json")));
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
        if (typeLabel is not null) Assert.Equal(typeLabel, FilterConditionLabels.RuleTypes["customTrailingZeros"]);
        Assert.Equal(6, TrailingZeroThreshold.DefaultZerosThreshold);
    }
}
