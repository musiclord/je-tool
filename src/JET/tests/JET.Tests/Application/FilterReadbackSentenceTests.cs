using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Application;

public sealed class FilterReadbackSentenceTests
{
    public static TheoryData<string, string, string, string> LiteralSentences()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tools", "tests", "fixtures", "filter-readback-sentences.json")))
            directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("Repository fixture directory was not found.");
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "tools", "tests", "fixtures", "filter-readback-sentences.json")));
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
        // 夾具裡非營業日兩句依裁定 2026-11-03-10 改成「非營業日」「在非營業日」。第一次失敗：Focused 收據 20261103-075506575-f3ac39e02ca44355891184cde07d8d1a。
        // 夾具裡兩句科目分類拿掉「（包含下層分類）」：裁定 2026-11-03-27 起，選的分類都沒有下層時讀回不寫選取依據，
        // 夾具沒有分類設上層。第一次失敗：Focused 收據 20261103-102439408-02bcfde39695433aaf7424d0787b8986。
        Assert.Equal(expected, FilterConditionRenderer.Render(scenario.RootElement, null, labels));
    }

}
