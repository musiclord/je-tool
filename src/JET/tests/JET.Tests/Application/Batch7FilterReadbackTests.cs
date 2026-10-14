using System.Text.Json;
using JET.Application;
using JET.Domain;
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

    /// <summary>
    /// 使用者 2026-10-14 第三輪勾選頁確認 R3-03：前端讀回已移除的 GL 欄位時，名稱來自 project.load 的
    /// retiredGlFieldLabels；夾具寫出同一份名稱給前端用，這裡確認它和後端唯一的名稱表一致。
    /// </summary>
    [Fact]
    public void FixtureRetiredFieldNames_EqualTheBackendCatalog()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tools", "tests", "fixtures", "batch7-filter-readback.json")))
            directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("Repository fixture directory was not found.");
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "tools", "tests", "fixtures", "batch7-filter-readback.json")));
        var withNames = file.RootElement.EnumerateArray()
            .Where(item => item.TryGetProperty("retiredGlFieldLabels", out _))
            .ToArray();

        Assert.NotEmpty(withNames);
        foreach (var item in withNames)
        {
            var names = item.GetProperty("retiredGlFieldLabels").EnumerateObject()
                .ToDictionary(entry => entry.Name, entry => entry.Value.GetString()!);
            Assert.Equal(new Dictionary<string, string> { ["voucherDate"] = "傳票日期" }, names);
            Assert.Equal(JetFieldCatalog.RetiredGlFieldLabels.OrderBy(entry => entry.Key), names.OrderBy(entry => entry.Key));
        }
    }
}
