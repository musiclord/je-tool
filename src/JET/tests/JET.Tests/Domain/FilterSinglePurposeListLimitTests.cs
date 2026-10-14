using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class FilterSinglePurposeListLimitTests
{
    [Theory]
    [InlineData("createBy", "in", "傳票建立人員")]
    [InlineData("description", "isBlank", "傳票摘要")]
    public void KctFieldPresets_KeepTheirExistingMappedFieldRequirement(string field, string op, string label)
    {
        var json = JsonSerializer.SerializeToElement(new { source = "kct", name = "", rationale = "", groups = new[] {
            new { rules = new[] { new { type = "fieldValue", field, @operator = op, values = new[] { "Synthetic" } } } } } });
        var node = System.Text.Json.Nodes.JsonNode.Parse(json.GetRawText())!;
        if (op == "isBlank") node["groups"]![0]!["rules"]![0]!.AsObject().Remove("values");
        var scenario = FilterScenarioPayloadParser.Parse(JsonElement.Parse(node.ToJsonString()), 100);
        var errors = FilterScenarioValidator.Validate(scenario, new FilterValidationContext(true, true, true), forSave: true);
        Assert.Contains(errors, message => message.Contains($"「{label}」尚未選擇來源欄", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("createBy", "in", "任一符合")]
    [InlineData("description", "contains", "任一符合")]
    [InlineData("amount", "endsWithDigits", "任一符合")]
    [InlineData("createBy", "notIn", "全部符合")]
    [InlineData("description", "notContains", "全部符合")]
    [InlineData("amount", "notEndsWithDigits", "全部符合")]
    public void MoreThanOneHundredValues_ExplainsHowToSplitTheList(string field, string op, string combination)
    {
        var rule = new Dictionary<string, object>
        {
            ["type"] = "fieldValue", ["field"] = field, ["operator"] = op,
            ["values"] = Enumerable.Range(0, 101).Select(i => i.ToString()).ToArray()
        };
        if (field == "amount") rule["amountBasis"] = "absolute";
        var json = JsonSerializer.SerializeToElement(new { name = "合成清單", rationale = "固定上限", groups = new[] { new { rules = new[] { rule } } } });
        var scenario = FilterScenarioPayloadParser.Parse(json, 100);
        var errors = FilterScenarioValidator.Validate(scenario, new FilterValidationContext(true, true, true), forSave: true);
        Assert.Contains(errors, message => message.Contains("100", StringComparison.Ordinal)
            && message.Contains("拆", StringComparison.Ordinal) && message.Contains(combination, StringComparison.Ordinal)
            && message.Contains("子條件組", StringComparison.Ordinal));
    }
}
