using System.Text.Json;
using JET.Application;
using Xunit;

namespace JET.Tests.Application;

public sealed class Batch7FilterReadbackTests
{
    public static TheoryData<string, string, string, string> LiteralSentences()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tools", "tests", "fixtures", "batch7-filter-readback.json")))
            directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("Repository fixture directory was not found.");
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "tools", "tests", "fixtures", "batch7-filter-readback.json")));
        var data = new TheoryData<string, string, string, string>();
        foreach (var item in file.RootElement.EnumerateArray())
            data.Add(item.GetProperty("id").GetString()!, item.GetProperty("scenario").GetRawText(),
                item.GetProperty("rdeFields").GetRawText(), item.GetProperty("expected").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(LiteralSentences))]
    public void BackendReadback_EqualsTheSharedLiteralSentence(string id, string json, string fieldsJson, string expected)
    {
        Assert.NotEmpty(id);
        using var scenario = JsonDocument.Parse(json);
        using var fields = JsonDocument.Parse(fieldsJson);
        var labels = fields.RootElement.EnumerateArray().ToDictionary(
            field => field.GetProperty("fieldId").GetString()!, field => field.GetProperty("label").GetString()!);
        Assert.Equal(expected, FilterConditionRenderer.Render(scenario.RootElement, null, labels));
    }
}
