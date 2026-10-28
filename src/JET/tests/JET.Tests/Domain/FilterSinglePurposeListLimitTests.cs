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
        // 原本靠四個欄位旗標預設是 false 表示「傳票建立人員」與「傳票摘要」沒有配對。預篩選收斂計畫階段四（2026-10-23）
        // 拿掉旗標、改讀可用欄位清單，沒帶清單就不檢查欄位，所以改成明確給一份不含這兩欄的清單，斷言不變。
        // 第一次失敗：Focused 收據 20261023-033450367-efcc5f97968c4691a01ae8ae6018b91a。
        var context = new FilterValidationContext(true, true, true)
        {
            AvailableGlFields = JetFieldCatalog.GlFields
                .Select(static candidate => candidate.SemanticIdentity)
                .Where(static candidate => candidate is not (JetFieldCatalog.GlCreateBy or JetFieldCatalog.GlDescription))
                .ToArray()
        };
        var errors = FilterScenarioValidator.Validate(scenario, context, forSave: true);
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
