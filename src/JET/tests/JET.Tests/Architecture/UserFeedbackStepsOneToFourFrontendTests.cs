using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

public sealed class UserFeedbackStepsOneToFourFrontendTests
{
    [Fact]
    public void CreateForm_OnlyCollectsOptionalCaseMetadataAndDoesNotCollectOperator()
    {
        var source = ReadFrontend("js", "steps", "create-step.js");

        Assert.Matches(
            new Regex(@"formRow\('projectCode',[^\r\n]+false\)", RegexOptions.CultureInvariant),
            source);
        Assert.Matches(
            new Regex(@"formRow\('entityName',[^\r\n]+false\)", RegexOptions.CultureInvariant),
            source);
        Assert.DoesNotContain("formRow('operatorId'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("operatorId: form.operatorId", source, StringComparison.Ordinal);
        Assert.Contains("state.caseId = project ? project.projectId : null", ReadFrontend("js", "state.js"), StringComparison.Ordinal);
        Assert.Contains("<span class=\"case-summary__label\">案件名稱</span>", ReadFrontend("index.html"), StringComparison.Ordinal);
        var app = ReadFrontend("js", "app.js");
        Assert.Contains("var displayName = p.projectId", app, StringComparison.Ordinal);
        Assert.Contains("project-row__entity", app, StringComparison.Ordinal);
        Assert.DoesNotContain("state.caseClient || state.project.projectId", app, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(app, "state\\.project\\.projectId \\+ ' — 分錄測試'").Count);
        Assert.Contains("data.project.projectId", ReadFrontend("js", "ui-core.js"), StringComparison.Ordinal);
    }

    [Fact]
    public void ImportGuidance_ExplainsBoundedPreviewAndUsesTheWiderHintStyle()
    {
        var source = ReadFrontend("js", "steps", "import-step.js");
        var css = ReadFrontend("css", "app.css");

        Assert.Contains("panel__hint panel__hint--wide", source, StringComparison.Ordinal);
        Assert.Contains("右側預覽只顯示少量資料", source, StringComparison.Ordinal);
        Assert.Contains("完整資料直接匯入案件資料庫", source, StringComparison.Ordinal);
        Assert.Contains(".xlsx、.xlsm、.csv", source, StringComparison.Ordinal);
        Assert.Contains("extensions: ['.xlsx', '.xlsm', '.csv', '.txt']", source, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"\.panel__hint--wide\s*\{[^}]*max-width:\s*84ch", RegexOptions.Singleline),
            css);
    }

    [Fact]
    public void MappingState_ResetsManualAutoPolicyWhenItsSourceMappingChanges()
    {
        var source = ReadFrontend("js", "state.js");
        var reset = ExtractFunction(source, "resetManualAutoPolicyIfSourceChanged");
        var setDraft = ExtractFunction(source, "setMappingDraft", objectMember: true);
        var assign = ExtractFunction(source, "assignColumnToField", objectMember: true);
        var replace = ExtractFunction(source, "replaceMappingDraft", objectMember: true);

        Assert.Contains("freshManualAutoPolicy", reset, StringComparison.Ordinal);
        Assert.Contains("resetManualAutoPolicyIfSourceChanged", setDraft, StringComparison.Ordinal);
        Assert.Contains("resetManualAutoPolicyIfSourceChanged", assign, StringComparison.Ordinal);
        Assert.Contains("resetManualAutoPolicyIfSourceChanged", replace, StringComparison.Ordinal);
    }

    [Fact]
    public void MappingUi_ShowsCommitFailureInContextAndCanToggleAllRdeFields()
    {
        var source = ReadFrontend("js", "steps", "mapping-step.js");

        Assert.Contains("mappingCommitErrorHtml", source, StringComparison.Ordinal);
        Assert.Contains("data-bind=\"mapping-commit-error-", source, StringComparison.Ordinal);
        Assert.Contains("onError: function", source, StringComparison.Ordinal);
        Assert.Contains("data-action=\"select-all-rde\"", source, StringComparison.Ordinal);
        Assert.Contains("data-action=\"clear-all-rde\"", source, StringComparison.Ordinal);
        Assert.Contains("toggleAllRdeFields", source, StringComparison.Ordinal);
        Assert.Contains("clearMappingCommitError", source, StringComparison.Ordinal);
        Assert.Contains("section.addEventListener('input'", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MappingUi_DistinguishesPostingDateAndVoucherDateAndDoesNotSummarizeDetachedManualPolicy()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");
        var summary = ExtractFunction(mapping, "committedOptionsHtml");

        Assert.Contains("{ key: 'postDate', label: '過帳日期'", core, StringComparison.Ordinal);
        Assert.Contains("{ key: 'voucherDate', label: '傳票日期'", core, StringComparison.Ordinal);
        Assert.Contains("傳票日期是選填，僅供回溯過帳判斷", mapping, StringComparison.Ordinal);
        Assert.Contains("committed.mapping.manual", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidationUi_UsesRequestedPlainDescriptionsAndNamesTheActualSourceQualityCheck()
    {
        var source = ReadFrontend("js", "steps", "validate-step.js");

        // 完整性測試有兩個檢查：匯入前後控制總數，以及逐科目 GL 對 TB。說明與狀態文字都要能對回同一件事。
        Assert.Contains(
            "先核對匯入前後的筆數與借貸總額是否一致，再逐科目計算 GL 發生額與 TB 期間變動額是否相等；金額不符代表總帳母體可能缺漏。",
            source,
            StringComparison.Ordinal);
        Assert.Contains("function controlTotalsMismatch", source, StringComparison.Ordinal);
        Assert.Contains("'匯入前後總數不一致'", source, StringComparison.Ordinal);
        Assert.Contains(
            "逐張傳票檢查借方與貸方金額是否相等；借貸不平代表傳票編號或金額欄位可能有誤。",
            source,
            StringComparison.Ordinal);
        Assert.Contains("title: '資料可靠性測試'", source, StringComparison.Ordinal);
        Assert.Contains("所選攸關資料元素（RDE）是否可靠", source, StringComparison.Ordinal);
        Assert.Contains("title: '過帳日空白'", source, StringComparison.Ordinal);
        Assert.Contains("日期在查核期間外不列在這裡", source, StringComparison.Ordinal);

        // 報表沿用 legacy 名稱「INF 抽樣測試」，命名是否統一待使用者裁定；裁定前畫面要說明對照關係。
        Assert.Contains("ValidationReport 與 INF 報表中稱為「INF 抽樣測試」", source, StringComparison.Ordinal);
        Assert.DoesNotContain("INF Report 只會發布", source, StringComparison.Ordinal);
        Assert.Contains("INF 報表就是資料可靠性測試的樣本", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateForm_OperatorNoticeRerendersWhenIdentityArrivesAfterFirstPaint()
    {
        var app = ReadFrontend("js", "app.js");
        var create = ReadFrontend("js", "steps", "create-step.js");
        var renderContent = ExtractFunction(app, "renderContent");

        // setCurrentUser 只 notify 不 bump；renderContent 的重繪鍵必須像 renderPicker 一樣把身分併進去，
        // 否則身分晚於首次繪製抵達時，建立案件表單會停在「目前 Windows 帳號」。
        Assert.Contains("state.currentUser", renderContent, StringComparison.Ordinal);
        Assert.Contains("identityKey", renderContent, StringComparison.Ordinal);
        Assert.Contains("user.shortName : '目前 Windows 帳號'", create, StringComparison.Ordinal);
    }

    [Fact]
    public void MappingUi_KeepsCommitErrorLifecycleOutOfRenderAndNamesTheWorkingPaperColumn()
    {
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");
        var errorHtml = ExtractFunction(mapping, "mappingCommitErrorHtml");
        var render = ExtractFunction(mapping, "render");

        // 畫面函式只讀不寫；作廢判斷集中在 render 前的單一入口。
        Assert.DoesNotContain("mappingCommitErrors[kind] = null", errorHtml, StringComparison.Ordinal);
        Assert.Contains("function discardStaleMappingCommitErrors", mapping, StringComparison.Ordinal);
        Assert.Contains("discardStaleMappingCommitErrors(state)", render, StringComparison.Ordinal);

        // 畫面名稱「過帳日期」與 Working Paper 正準欄名不同，統一與否待裁定；裁定前先說明對照。
        Assert.Contains("Working Paper 的欄名仍是「總帳日期_JE」", mapping, StringComparison.Ordinal);
    }

    private static string ExtractFunction(string source, string name, bool objectMember = false)
    {
        var prefix = objectMember ? name + ": function (" : "function " + name + "(";
        var start = source.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到 {name} 函式。");
        var depth = 0;
        var opened = false;
        for (var index = start; index < source.Length; index++)
        {
            if (source[index] == '{') { depth++; opened = true; }
            else if (source[index] == '}' && opened && --depth == 0) { return source[start..(index + 1)]; }
        }
        throw new InvalidOperationException($"找不到 {name} 函式結尾。");
    }

    private static string ReadFrontend(params string[] segments)
        => File.ReadAllText(Path.Combine(new[] { FrontendRoot() }.Concat(segments).ToArray()));

    private static string FrontendRoot() => Path.Combine(RepoRoot(), "JET", "wwwroot");

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
