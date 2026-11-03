using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class FilterSinglePurposeListLimitTests
{
    [Theory]
    [InlineData("createdBy", "in", "傳票建立人員")]
    [InlineData("description", "isBlank", "傳票摘要")]
    public void CommonCardFieldPresets_KeepTheirExistingMappedFieldRequirement(string field, string op, string label)
    {
        var json = JsonSerializer.SerializeToElement(new { source = "common", name = "", rationale = "", groups = new[] {
            new { conditions = new[] { new { type = "fieldValue", field, @operator = op, values = new[] { "Synthetic" } } } } } });
        var node = System.Text.Json.Nodes.JsonNode.Parse(json.GetRawText())!;
        if (op == "isBlank") node["groups"]![0]!["conditions"]![0]!.AsObject().Remove("values");
        var scenario = FilterScenarioPayloadParser.Parse(JsonElement.Parse(node.ToJsonString()), 100);
        // 原本靠四個欄位旗標預設是 false 表示「傳票建立人員」與「傳票摘要」沒有配對。後來
        // 拿掉旗標、改讀可用欄位清單，沒帶清單就不檢查欄位，所以改成明確給一份不含這兩欄的清單，斷言不變。
        // 第一次失敗：Focused 收據 20261023-033450367-efcc5f97968c4691a01ae8ae6018b91a。
        var context = new FilterValidationContext(true, true, true)
        {
            AvailableGlFields = JetFieldCatalog.GlFields
                .Select(static candidate => candidate.SemanticIdentity)
                .Where(static candidate => candidate is not (JetFieldCatalog.GlCreatedBy or JetFieldCatalog.GlDescription))
                .ToArray()
        };
        var errors = FilterScenarioValidator.Validate(scenario, context, forSave: true);
        Assert.Contains(errors, message => message.Contains($"「{label}」尚未選擇來源欄", StringComparison.Ordinal));
    }

    // 子條件組的連接在畫面上只寫 AND 與 OR（裁定 2026-10-30-32），提示改成「放進子條件組，用 OR 連接」或「用 AND 連接」，
    // 不再寫畫面上已沒有的「任一符合」「全部符合」。第一次失敗：Focused 收據 20261031-101615482-c77433faf82d4c4f86b5fcc87daf2e32。
    [Theory]
    [InlineData("createdBy", "in", "用 OR 連接")]
    [InlineData("description", "contains", "用 OR 連接")]
    [InlineData("amount", "endsWithDigits", "用 OR 連接")]
    [InlineData("createdBy", "notIn", "用 AND 連接")]
    [InlineData("description", "notContains", "用 AND 連接")]
    [InlineData("amount", "notEndsWithDigits", "用 AND 連接")]
    public void MoreThanOneHundredValues_ExplainsHowToSplitTheList(string field, string op, string combination)
    {
        var rule = new Dictionary<string, object>
        {
            ["type"] = "fieldValue", ["field"] = field, ["operator"] = op,
            ["values"] = Enumerable.Range(0, 101).Select(i => i.ToString()).ToArray()
        };
        if (field == "amount") rule["amountBasis"] = "absolute";
        var json = JsonSerializer.SerializeToElement(new { name = "合成清單", rationale = "固定上限", groups = new[] { new { conditions = new[] { rule } } } });
        var scenario = FilterScenarioPayloadParser.Parse(json, 100);
        var errors = FilterScenarioValidator.Validate(scenario, new FilterValidationContext(true, true, true), forSave: true);
        Assert.Contains(errors, message => message.Contains("100", StringComparison.Ordinal)
            && message.Contains("拆", StringComparison.Ordinal) && message.Contains(combination, StringComparison.Ordinal)
            && message.Contains("子條件組", StringComparison.Ordinal));
    }
}
