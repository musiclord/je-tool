using System.Text.RegularExpressions;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 欄位配對步驟的標準化政策接線守衛（前端工作流整合）。後端 <c>mapping.commit.gl</c> 仍是
/// 權威驗證；本組只鎖 closed 選項鏡像、互斥引導、來源值分布的取得方式與送出形狀。
/// </summary>
public sealed class MappingProjectionPolicyFrontendTests
{
    [Fact]
    public void PostingStatus_IsAVisibleMappingSlotMirroringTheCatalog()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var slot = JetFieldCatalog.GlMappingUiSlots
            .Single(item => item.Key == GlMappingKeys.PostingStatus);

        Assert.True(slot.IncludeInMappingUi);
        Assert.Contains(
            "{ key: 'postingStatus', label: '" + slot.Label + "', req: 'optional' }",
            core,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovalDateModes_MirrorTheClosedWireValues()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var modes = ExtractValueLabelMap(core, "GL_APPROVAL_DATE_MODES");

        Assert.Equal(
            new[] { "unmapped", "mapped", "sameAsPostDate" },
            modes.Keys.ToArray());
        Assert.All(modes.Values, label => Assert.False(string.IsNullOrWhiteSpace(label)));
    }

    [Fact]
    public void ApprovalDateModes_AreMutuallyExclusiveWithTheApprovalSourceColumn()
    {
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");
        var state = ReadFrontend("js", "state.js");

        // 2026-09-05：指定來源欄即切換模式，互斥由一次 store 更新維持。
        Assert.Contains("function excludedFieldKeys(kind)", mapping, StringComparison.Ordinal);
        Assert.Contains("data-approval-source", mapping, StringComparison.Ordinal);
        Assert.Contains("Store.setMappingDraft('gl', 'docDate', approvalSource.value)", mapping, StringComparison.Ordinal);
        Assert.Contains("patch.approvalDateMode !== 'mapped'", state, StringComparison.Ordinal);
        Assert.Contains("delete state.mapping.gl.draft.docDate;", state, StringComparison.Ordinal);
        Assert.Contains("gl.options.approvalDateMode = 'mapped';", state, StringComparison.Ordinal);
        Assert.Contains("gl.options.approvalDateMode = 'unmapped';", state, StringComparison.Ordinal);

        // mapped 模式必須有來源欄，否則不得提交。
        Assert.Contains("options.approvalDateMode === 'mapped' && !draft.docDate", mapping, StringComparison.Ordinal);
        Assert.Contains(
            "options.approvalDateMode === 'sameAsPostDate' && draft.docDate",
            mapping,
            StringComparison.Ordinal);
        Assert.Contains("options.approvalDateMode === 'unmapped' && draft.docDate", mapping, StringComparison.Ordinal);
        Assert.Contains("Store.restoreCommittedMapping(kind)", mapping, StringComparison.Ordinal);
        Assert.Contains("JSON.parse(JSON.stringify(committed.options))", state, StringComparison.Ordinal);
    }

    [Fact]
    public void PostingStatusAndManualCodes_ComeFromTheBackendValueProfile()
    {
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");

        // 值、次數、空白數與截斷旗標全部取自後端聚合，畫面不掃描來源列。
        Assert.Contains("global.JetApi.mappingValueProfile({", mapping, StringComparison.Ordinal);
        Assert.Contains("dataset: 'gl'", mapping, StringComparison.Ordinal);
        Assert.Contains("limit: Ui.VALUE_PROFILE_LIMIT", mapping, StringComparison.Ordinal);
        Assert.Contains("data-action=\"load-posting-profile\"", mapping, StringComparison.Ordinal);
        Assert.Contains("data-action=\"load-manual-profile\"", mapping, StringComparison.Ordinal);
        Assert.Contains("data-posting-value=", mapping, StringComparison.Ordinal);
        Assert.Contains("data-manual-assign=", mapping, StringComparison.Ordinal);

        // 載入中／失敗都有可讀文字與可重試入口，不只靠顏色或空白表達。
        Assert.Contains("讀取來源值中…", mapping, StringComparison.Ordinal);
        Assert.Contains("讀取來源值失敗。", mapping, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualAutoCodes_AreValidatedForEmptinessAndOverlapBeforeCommit()
    {
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");
        var problems = ExtractFunction(mapping, "glOptionProblems", "normalizedCode");

        Assert.Contains("人工與自動代碼各需至少一個值", problems, StringComparison.Ordinal);
        Assert.Contains("人工與自動代碼不得重複", problems, StringComparison.Ordinal);
        Assert.Contains("normalizedCode(value) === normalizedCode(other)", problems, StringComparison.Ordinal);

        // trim + 不分大小寫，與後端同一口徑。
        var normalize = ExtractFunction(mapping, "normalizedCode", "glOptionsHtml");
        Assert.Contains(".trim().toUpperCase()", normalize, StringComparison.Ordinal);
    }

    [Fact]
    public void PostingStatusPolicy_RequiresAnAcceptedValueOrBlankAndIsOnlySentWhenMapped()
    {
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");
        var problems = ExtractFunction(mapping, "glOptionProblems", "normalizedCode");
        var payload = ExtractFunction(mapping, "glCommitPayload", "canonicalGlOptions");

        Assert.Contains("accepted.length === 0 && !(policy && policy.includeBlank)", problems, StringComparison.Ordinal);
        Assert.Contains("if (mapping.postingStatus) {", payload, StringComparison.Ordinal);
        Assert.Contains("payload.postingStatusPolicy = {", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void RelevantDataElementFields_AreOptInOnlyAndNeverMintFieldIdsInTheFrontend()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");
        var types = ExtractValueLabelMap(core, "RDE_VALUE_TYPES");

        Assert.Equal(new[] { "text", "date", "money" }, types.Keys.ToArray());

        // 只有勾選的來源欄會成為 RDE；核心配對已佔用的欄不列入候選。
        // 「哪些來源欄已被核心配對佔用」只在 state.js 定義一次，畫面只呼叫，不另寫一份。
        Assert.Contains("data-rde-column=", mapping, StringComparison.Ordinal);
        Assert.DoesNotContain("function rdeAvailableColumns", mapping, StringComparison.Ordinal);
        Assert.DoesNotContain("key !== 'dcDebitCode'", mapping, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(mapping, Regex.Escape("Store.availableGlRdeColumns()")).Count);

        var state = ReadFrontend("js", "state.js");
        var used = ExtractFunction(state, "usedGlSourceColumns", "removeCoreMappedRdeFields");
        Assert.Contains("Object.create(null)", used, StringComparison.Ordinal);
        Assert.Contains("key !== 'dcDebitCode' && value", used, StringComparison.Ordinal);

        // 核心配對變動時，state 會清掉已被核心欄位佔用的 RDE，不能只靠畫面把衝突項目藏起來。
        var cleanup = ExtractFunction(state, "removeCoreMappedRdeFields", "syncApprovalSource");
        Assert.Contains("usedGlSourceColumns()", cleanup, StringComparison.Ordinal);
        Assert.Contains("!used[field.sourceColumn]", cleanup, StringComparison.Ordinal);

        // 新欄一律省略 fieldId；既有欄沿用後端 stable ID。
        Assert.Contains(
            "fields.push({ sourceColumn: column, label: column, valueType: 'text' });",
            mapping,
            StringComparison.Ordinal);
        Assert.Contains("if (field.fieldId) { wire.fieldId = field.fieldId; }", mapping, StringComparison.Ordinal);
        Assert.DoesNotContain("'rde.'", mapping, StringComparison.Ordinal);

        // commit response 的 canonical options 覆寫草稿，下一次 recommit 才會沿用同一身分。
        Assert.Contains("Store.replaceGlMappingOptions(canonicalGlOptions(data))", mapping, StringComparison.Ordinal);
    }

    [Fact]
    public void MappingReviewRequired_MirrorsTheBackendFlagAndBlocksDownstreamSteps()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");
        var state = ReadFrontend("js", "state.js");

        Assert.Contains("data-bind=\"mapping-review-required\"", core, StringComparison.Ordinal);
        Assert.Contains("Store.setMappingReviewRequired(data.mappingReviewRequired)", core, StringComparison.Ordinal);
        Assert.Contains("if (state.mappingReviewRequired) { missing.push(MAPPING_REVIEW_MISSING); }", core, StringComparison.Ordinal);
        Assert.Contains("Ui.mappingReviewBannerHtml(state)", mapping, StringComparison.Ordinal);

        // 成功 recommit 後依 formatVersion 重新推導，修復路徑不必重開案件。
        Assert.Contains("function refreshMappingReviewRequired()", state, StringComparison.Ordinal);
        Assert.Contains("state.mapping[kind].formatVersion === 1", state, StringComparison.Ordinal);
        Assert.Contains("formatVersion: 2", mapping, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> ExtractValueLabelMap(string source, string arrayName)
    {
        var arrayMatch = Regex.Match(
            source,
            arrayName + @"\s*=\s*\[(?<body>.*?)\];",
            RegexOptions.Singleline);
        Assert.True(arrayMatch.Success, $"ui-core.js 內找不到 {arrayName} 陣列。");

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(
            arrayMatch.Groups["body"].Value,
            @"\{\s*value:\s*'(?<value>[^']+)'\s*,\s*label:\s*'(?<label>[^']+)'"))
        {
            result[match.Groups["value"].Value] = match.Groups["label"].Value;
        }

        Assert.NotEmpty(result);
        return result;
    }

    private static string ExtractFunction(string source, string functionName, string nextFunctionName)
    {
        var start = source.IndexOf("function " + functionName, StringComparison.Ordinal);
        var end = source.IndexOf("function " + nextFunctionName, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"找不到 mapping-step.js 的 {functionName} 邊界。");
        return source[start..end];
    }

    private static string ReadFrontend(params string[] segments)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot(), "JET", "wwwroot" }.Concat(segments).ToArray()));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("向上尋不到 JET.slnx。");
    }
}
