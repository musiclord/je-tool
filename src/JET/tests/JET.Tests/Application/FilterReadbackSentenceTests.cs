using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;
using JET.Tests.Infrastructure;

namespace JET.Tests.Application;

public sealed class FilterReadbackSentenceTests
{
    public static TheoryData<string, string, string, string> LiteralSentences()
    {
        using var file = JsonDocument.Parse(File.ReadAllText(TestRepositoryPaths.Fixture("filter-readback-sentences.json")));
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
        // 夾具裡非營業日兩句寫「非營業日」「在非營業日」。
        // 夾具裡兩句科目分類不寫「（包含下層分類）」：選的分類都沒有下層時讀回不寫選取依據，
        // 夾具沒有分類設上層。
        Assert.Equal(expected, FilterConditionRenderer.Render(scenario.RootElement, null, labels));
    }

}
