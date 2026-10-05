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
        // 9/21 移除工作區重複抬頭；案件名稱仍顯示於頂部，總覽視窗保留自己的標題，所以這個標題只能出現一次。
        // 2026-10-03 O8：總覽標題不再用破折號接「分錄測試」，改成客戶名稱，沒填時用案件名稱，「分錄測試」移到小標
        // （第一次失敗：收據 20261003-065529947-9b08f413915748fe812bdead0a6d6d89）。
        Assert.Equal(1, Regex.Matches(app, Regex.Escape("(state.caseClient || caseName || '目前案件')")).Count);
        Assert.DoesNotContain("' — 分錄測試'", app, StringComparison.Ordinal);
        Assert.Contains("var eyebrow = '分錄測試流程總覽';", app, StringComparison.Ordinal);
        Assert.DoesNotContain("workpaperHeaderHtml", app, StringComparison.Ordinal);
        Assert.Contains("data.project.projectId", ReadFrontend("js", "ui-core.js"), StringComparison.Ordinal);
    }

    [Fact]
    public void ImportGuidance_UsesBriefCopyAndRetainsSupportedFileTypes()
    {
        var source = ReadFrontend("js", "steps", "import-step.js");
        var css = ReadFrontend("css", "app.css");

        Assert.Contains("panel__hint panel__hint--wide", source, StringComparison.Ordinal);
        // L11 separates GL merging from TB period-file handling, while keeping every supported format.
        // First failure: 20261004-091831760-a762b9ef96db4d61a4bb2bff7cdb0022.
        Assert.Contains("支援 Excel、CSV、文字檔及 Access。總帳明細可合併欄位相同的來源。", source, StringComparison.Ordinal);
        Assert.Contains("試算表請提供同一查核期間的資料，不會自動把期初與期末兩份檔案配成期間變動。", source, StringComparison.Ordinal);
        Assert.DoesNotContain("完整資料直接匯入案件資料庫", source, StringComparison.Ordinal);
        Assert.DoesNotContain("解鎖「非授權編製人員」", source, StringComparison.Ordinal);
        Assert.Contains("extensions: ['.xlsx', '.xlsm', '.xls', '.csv', '.txt', '.mdb', '.accdb']", source, StringComparison.Ordinal);
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
        // 第二遍第 6 批裁定：修改設定後保留上次各組錯誤與導航，改標成需要重新確認，不再整份清掉。
        // 首次失敗收據 20261004-081808251-028419be00a44696b3d2d8e01e72927f；Node 行為測試另實際修改後再確認。
        var markModified = ExtractFunction(source, "markMappingCommitErrorModified");
        var errorHtml = ExtractFunction(source, "mappingCommitErrorHtml");
        var errorTarget = ExtractFunction(source, "mappingErrorTarget");
        Assert.Contains("error.modified = true", markModified, StringComparison.Ordinal);
        Assert.DoesNotContain("mappingCommitErrors[kind] = null", markModified, StringComparison.Ordinal);
        Assert.Contains("設定已修改，請重新確認", errorHtml, StringComparison.Ordinal);
        Assert.Contains("detail.sourceColumn, detail.reasonCode", errorHtml, StringComparison.Ordinal);
        Assert.Contains("前往設定", errorHtml, StringComparison.Ordinal);
        Assert.Contains("reasonCode === 'manual_blank' ? 'manual-blank' : 'manual-mode'", errorTarget, StringComparison.Ordinal);
        Assert.Contains("data-focus-mapping-field", errorTarget, StringComparison.Ordinal);
        Assert.Contains("section.addEventListener('input'", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MappingUi_DistinguishesPostingDateAndVoucherDateAndDoesNotSummarizeDetachedManualPolicy()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");
        var summary = ExtractFunction(mapping, "committedOptionsHtml");

        Assert.Contains("{ key: 'postDate', label: '總帳入帳日'", core, StringComparison.Ordinal);
        Assert.Contains("{ key: 'voucherDate', label: '傳票日期'", core, StringComparison.Ordinal);
        Assert.Contains("{ key: 'voucherDate', label: '傳票日期', req: 'optional' }", core, StringComparison.Ordinal);
        Assert.Contains("committed.mapping.manual", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidationUi_UsesRequestedPlainDescriptionsAndNamesTheActualSourceQualityCheck()
    {
        var source = ReadFrontend("js", "steps", "validate-step.js");

        // 完整性測試有兩個檢查：存下的分錄和確認配對時算出的數字，以及逐科目 GL 對 TB。說明與狀態文字都要能對回同一件事。
        // 2026-10-05 V2 裁定：兩組數字都由 JET 在確認配對時與存下後計算，不比對來源檔本身，所以不再寫「匯入前後」；
        // 最後一句是使用者原句，逐字保留。W1 裁定：借貸不平判讀句照原件用「、」。
        Assert.Contains(
            "先確認 JET 存下的分錄筆數與借貸合計，和確認欄位配對時算出的一樣。這一步不和來源檔自己的合計比對。"
            + "再比對各科目的總帳本期借貸淨額與試算表本期變動金額。金額不符代表總帳母體可能缺漏。",
            source,
            StringComparison.Ordinal);
        Assert.Contains("function controlTotalsMismatch", source, StringComparison.Ordinal);
        Assert.Contains("'存下的分錄數字不一致'", source, StringComparison.Ordinal);
        Assert.Contains(
            "依傳票號碼彙總借方與貸方金額，列出不相等的傳票。借貸不平代表傳票編號、金額欄位可能有誤。",
            source,
            StringComparison.Ordinal);
        Assert.Contains("title: '資料可靠性測試'", source, StringComparison.Ordinal);
        Assert.Contains("抽出分錄樣本，供核對傳票附件，確認摘要、日期等篩選欄位是否正確。", source, StringComparison.Ordinal);
        // U23 restores the two user sentences from HEAD; L42 restores the required explanation order, not a blocker.
        // First failure: 20261004-091831760-a762b9ef96db4d61a4bb2bff7cdb0022.
        Assert.Contains("需先確認攸關資料元素（RDE）的可靠性後，再執行高風險篩選條件。", source, StringComparison.Ordinal);
        Assert.Contains("title: '總帳入帳日空白'", source, StringComparison.Ordinal);
        Assert.Contains("總帳入帳日空白的分錄，無法判斷是否在查核期間內，因此未納入本次測試", source, StringComparison.Ordinal);

        // 9/22 使用者要求移除測試說明中的報表沿革；用途與檔名改在共用報告清單對照，實際報表內容不變。
        Assert.DoesNotContain("ValidationReport 與 INF 報表中稱為", source, StringComparison.Ordinal);
        Assert.DoesNotContain("INF Report 只會發布", source, StringComparison.Ordinal);
        // 用途已由上方可靠性測試說明，不在報告區再重複同一句。
        Assert.DoesNotContain("資料可靠性抽樣清單供核對傳票附件", source, StringComparison.Ordinal);
        var core = ReadFrontend("js", "ui-core.js");
        Assert.Contains("infReport: '資料可靠性抽樣清單'", core, StringComparison.Ordinal);
        Assert.Contains("esc(artifact.fileName", core, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateForm_OperatorNoticeUpdatesWithoutRebuildingTheFormWhenIdentityArrives()
    {
        var app = ReadFrontend("js", "app.js");
        var create = ReadFrontend("js", "steps", "create-step.js");
        var renderContent = ExtractFunction(app, "renderContent");

        // C9 U48/L87：舊身分重繪鍵會清掉未儲存表單，改為只更新操作人員那一行。
        // 舊斷言首敗：20261004-084402361-d3857beabfbf4b5689007c0ac3130c98。
        // frontend-mapping.test.cjs 另外核對相同input節點、文字、選取範圍與焦點都保留。
        Assert.Contains("Ui.refreshCreateIdentity(container, state)", renderContent, StringComparison.Ordinal);
        Assert.DoesNotContain("identityKey", renderContent, StringComparison.Ordinal);
        Assert.Contains("data-bind=\"create-operator\"", create, StringComparison.Ordinal);
        Assert.Contains("state.currentUser", ExtractFunction(create, "operatorNoticeHtml"), StringComparison.Ordinal);
        Assert.Contains("user.shortName : '目前 Windows 帳號'", create, StringComparison.Ordinal);
    }

    [Fact]
    public void MappingUi_KeepsCommitErrorLifecycleOutOfRenderAndUsesBriefGuidance()
    {
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");
        var errorHtml = ExtractFunction(mapping, "mappingCommitErrorHtml");
        var render = ExtractFunction(mapping, "render");

        // 畫面函式只讀不寫；作廢判斷集中在 render 前的單一入口。
        Assert.DoesNotContain("mappingCommitErrors[kind] = null", errorHtml, StringComparison.Ordinal);
        Assert.Contains("function discardStaleMappingCommitErrors", mapping, StringComparison.Ordinal);
        Assert.Contains("discardStaleMappingCommitErrors(state)", render, StringComparison.Ordinal);

        // 9/21 配對畫面只保留操作提示；報表欄名契約仍由原有報表測試驗證。
        Assert.Contains("選擇各欄位的資料來源，標示 * 的欄位為必填。", render, StringComparison.Ordinal);
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
