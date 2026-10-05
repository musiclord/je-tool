using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 2026-10-02 整體複審的畫面行為修正：建立案件的 SQL Server 選項（W2）、金額顯示格式（W11）、
/// 完成判斷一致（W15）、沒有情境時查看底稿版本紀錄（W17）、讀取中按「查看」（W18）與非預期錯誤訊息（W19）。
/// 前端沒有 JavaScript 執行環境，這裡和其他前端守衛一樣核對原始碼的判斷式與字樣。
/// </summary>
public sealed class ScreenBehaviorReviewFrontendTests
{
    [Fact]
    public void CreateCase_DisablesSqlServerOnlyWhenDatabaseInfoSaysNotConfigured()
    {
        var create = ReadFrontend("js", "steps", "create-step.js");
        var app = ReadFrontend("js", "app.js");
        var state = ReadFrontend("js", "state.js");

        // 只有 system.databaseInfo 明確回報 configured === false 才停用；還沒查到或已設定但連不上都維持可選。
        Assert.Contains("return state.sqlServerConfigured === false;", ExtractFunction(create, "sqlServerUnavailable"), StringComparison.Ordinal);
        Assert.Contains("{ value: 'sqlServer', text: 'SQL Server（共用實例）', disabled: sqlServerUnavailable(state) }", create, StringComparison.Ordinal);
        Assert.Contains("sqlServerUnavailable(state) ? SQL_SERVER_NOT_CONFIGURED_HINT : ''", create, StringComparison.Ordinal);
        Assert.Contains("var SQL_SERVER_NOT_CONFIGURED_HINT = '這台電腦沒有設定線上資料庫，請改選 SQLite 或 DuckDB。';", create, StringComparison.Ordinal);
        Assert.Contains("(option.disabled ? ' disabled' : '')", ExtractFunction(create, "formSelect"), StringComparison.Ordinal);
        Assert.DoesNotContain("sqlServerConfigured !== true", create, StringComparison.Ordinal);

        // 沿用啟動時已在背景呼叫的 system.databaseInfo，不新增 action。
        Assert.Contains("if (s && typeof s.configured === 'boolean') { Store.setSqlServerConfigured(s.configured); }", app, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(app, @"systemDatabaseInfo\(").Cast<System.Text.RegularExpressions.Match>());
        Assert.Contains("sqlServerConfigured: null,", state, StringComparison.Ordinal);

        // 完整驗證發現設定晚到會清掉建案草稿；false→true 也未恢復原選項。
        // Node 首次失敗：20261004-112938685-4017fc72eda141fbbd874b4783205315。
        // 舊原碼斷言首次失敗：20261004-113104401-69dd35e1bd7f4726a311ba5182d1e09b。
        // 保留上面的停用判斷與下一步提示；改鎖定就地更新，不能再要求重建整張表單。
        var configuration = ExtractFunction(state, "setSqlServerConfigured: function");
        Assert.Contains("state.sqlServerConfigured = next;", configuration, StringComparison.Ordinal);
        Assert.Contains("notify();", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("bump(", configuration, StringComparison.Ordinal);
        var refresh = ExtractFunction(create, "Ui.refreshCreateDatabaseAvailability = function");
        Assert.Contains("var unavailable = sqlServerUnavailable(state);", refresh, StringComparison.Ordinal);
        Assert.Contains("container.querySelector('[data-bind=\"create-form\"] option[value=\"sqlServer\"]')", refresh, StringComparison.Ordinal);
        Assert.Contains("container.querySelector('[data-bind=\"create-form\"] [data-bind=\"databaseProvider-hint\"]')", refresh, StringComparison.Ordinal);
        Assert.Contains("option.disabled = unavailable;", refresh, StringComparison.Ordinal);
        Assert.Contains("hint.textContent = unavailable ? SQL_SERVER_NOT_CONFIGURED_HINT : '';", refresh, StringComparison.Ordinal);
        Assert.Contains("hint.hidden = !unavailable;", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain(".innerHTML", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain(".outerHTML", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain(".value =", refresh, StringComparison.Ordinal);
        var content = ExtractFunction(app, "renderContent");
        var update = content.IndexOf("Ui.refreshCreateDatabaseAvailability(container, state);", StringComparison.Ordinal);
        var cached = content.IndexOf("if (key === lastContentKey) { return; }", StringComparison.Ordinal);
        Assert.True(update >= 0 && cached > update, "即使表單本體不需重繪，資料庫選項與提示也必須更新。");
    }

    [Fact]
    public void Money_ShowsTwoDecimalsWithThousandsSeparatorThroughOneSharedFormatter()
    {
        // 使用者 2026-10-02 裁定 P3-4：畫面統一兩位小數加千分位；報表與底稿不變。
        var core = ReadFrontend("js", "ui-core.js");
        var money = ExtractFunction(core, "money");
        Assert.Contains("toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })", money, StringComparison.Ordinal);
        Assert.Contains("return '—';", money, StringComparison.Ordinal);

        // 每個顯示金額的地方都走同一個函式。
        Assert.Contains("return money(raw);", ExtractFunction(core, "dynamicCellText"), StringComparison.Ordinal);
        Assert.Contains("money(r.amount)", core, StringComparison.Ordinal);
        var vouchers = ReadFrontend("js", "filter-vouchers.js");
        Assert.Contains("Ui.money(row.voucherTotal)", vouchers, StringComparison.Ordinal);
        Assert.Contains("cellHtml(Ui.money(row.amount), 'preview-table__amount')", vouchers, StringComparison.Ordinal);
        Assert.Contains("var extraCells = Ui.dynamicColumnCells(extra);", vouchers, StringComparison.Ordinal);
        Assert.DoesNotContain("String(row.amount)", vouchers, StringComparison.Ordinal);
        var validate = ReadFrontend("js", "steps", "validate-step.js");
        Assert.Contains("function num(v) { return Ui.money(v); }", validate, StringComparison.Ordinal);
        Assert.Contains("Ui.money(row.amount)", validate, StringComparison.Ordinal);
        // L46 distinguishes a real tiny difference from zero without changing ordinary amount precision.
        // First failure: 20261004-091831760-a762b9ef96db4d61a4bb2bff7cdb0022.
        Assert.Contains("Ui.moneyDifference(stats.net)", validate, StringComparison.Ordinal);
        Assert.Contains("Ui.moneyDifference(d.diff)", validate, StringComparison.Ordinal);
        Assert.Contains("Ui.moneyDifference(r.diff)", validate, StringComparison.Ordinal);
        var difference = ExtractFunction(core, "moneyDifference");
        Assert.Contains("var formatted = money(value)", difference, StringComparison.Ordinal);
        Assert.Contains("number !== 0", difference, StringComparison.Ordinal);
        Assert.Contains("formatted === '0.00' || formatted === '-0.00'", difference, StringComparison.Ordinal);
        Assert.Contains("number < 0 ? '負值，不足 0.01' : '不足 0.01'", difference, StringComparison.Ordinal);
        Assert.Contains("return formatted;", difference, StringComparison.Ordinal);
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        Assert.Contains("return Ui.money(r.voucherTotal);", filter, StringComparison.Ordinal);
        Assert.Contains("return Ui.money(r.amount);", filter, StringComparison.Ordinal);
        Assert.Contains("Ui.money(stats.totalDebit)", ReadFrontend("js", "app.js"), StringComparison.Ordinal);
        Assert.Contains("return Ui.money(cell);", ReadFrontend("js", "data-preview.js"), StringComparison.Ordinal);

        // 不再有四位小數的顯示；輸入框與篩選條件值不經過金額格式化。
        foreach (var file in Directory.GetFiles(Path.Combine(FrontendRoot(), "js"), "*.js", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("minimumFractionDigits: 4", File.ReadAllText(file), StringComparison.Ordinal);
        }
        Assert.DoesNotContain("Ui.money(", ReadFrontend("js", "filter-values.js"), StringComparison.Ordinal);
        Assert.Contains("'≥ ' + rule.from", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void StepCompletion_LeftProgressOverviewAndExportShareOneDefinition()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var complete = ExtractFunction(core, "stepComplete");
        var done = ExtractFunction(core, "doneStepCount");

        // 左側進度數到第六步，第六步以目前版本的工作底稿為準。
        Assert.Contains("for (var i = 0; i < steps.length; i++)", done, StringComparison.Ordinal);
        Assert.Contains("stepComplete(state, i)", done, StringComparison.Ordinal);
        Assert.DoesNotContain("steps.length - 1", done, StringComparison.Ordinal);
        Assert.Contains("if (index === last) { return !!currentWorkpaperArtifact(state); }", complete, StringComparison.Ordinal);
        Assert.Contains("stepComplete(state, index)", ExtractFunction(core, "stepPresentation"), StringComparison.Ordinal);

        var workpaper = ExtractFunction(core, "currentWorkpaperArtifact");
        Assert.Contains("findCurrentReportArtifact(state, 'workingPaper', {", workpaper, StringComparison.Ordinal);
        Assert.Contains("resultRef.populationScope === 'auditPeriod'", workpaper, StringComparison.Ordinal);
        Assert.DoesNotContain("reportArtifactHistory", workpaper, StringComparison.Ordinal);

        // 總覽與第六步的「案件流程已完成」都呼叫同一個判斷，不各算一份。
        var app = ReadFrontend("js", "app.js");
        Assert.Contains("return Ui.stepComplete(state, index);", ExtractFunction(app, "overviewStageComplete"), StringComparison.Ordinal);
        Assert.Contains("return Ui.currentWorkpaperArtifact(state);", ExtractFunction(app, "overviewCurrentWorkpaperArtifact"), StringComparison.Ordinal);
        Assert.Contains("var done = Ui.doneStepCount(state);", app, StringComparison.Ordinal);
        var export = ReadFrontend("js", "steps", "export-step.js");
        Assert.Contains("var currentWorkpaper = Ui.currentWorkpaperArtifact(state);", export, StringComparison.Ordinal);
        Assert.Contains("if (!currentWorkpaper) { return ''; }", ExtractFunction(export, "completionSummaryHtml"), StringComparison.Ordinal);
        Assert.DoesNotContain("Ui.currentReportArtifacts(state, ['workingPaper']", export, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportStep_OpensForWorkpaperHistoryWithoutScenario_ButExportStaysDisabled()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var gate = ExtractFunction(core, "stepGate");

        // 進入條件：有可用情境，或已經有任何工作底稿版本紀錄；兩者都沒有時照舊鎖住並寫出情境的原因。
        Assert.Contains("var scenarioMissing = filterScenarioMissing(state);", gate, StringComparison.Ordinal);
        Assert.Contains("if (scenarioMissing && !hasWorkpaperHistory(state)) { missing.push(scenarioMissing); }", gate, StringComparison.Ordinal);
        Assert.Contains("return reportArtifactHistory(state, 'workingPaper').length > 0;", ExtractFunction(core, "hasWorkpaperHistory"), StringComparison.Ordinal);
        var scenario = ExtractFunction(core, "filterScenarioMissing");
        // 2026-10-03 用語統一 W10：使用者裁定以「已儲存」為準（第一次失敗：收據 20261003-023349721-0ccefea0a80c412aa8460624eaae563a）。
        Assert.Contains("if (state.filter.savedScenarios.length === 0) { return '儲存至少一個篩選情境'; }", scenario, StringComparison.Ordinal);
        Assert.Contains("if (!state.filterResultRef || state.filterResultRef.populationScope !== 'auditPeriod') {", scenario, StringComparison.Ordinal);
        Assert.Contains("5: '需先儲存篩選情境'", core, StringComparison.Ordinal);

        // 第五步算完成仍要情境齊備，不因第六步能看版本紀錄而打勾。
        Assert.Contains("if (index === last - 1) { return !filterScenarioMissing(state); }", ExtractFunction(core, "stepComplete"), StringComparison.Ordinal);

        // 匯出本身的條件不放寬：仍要求驗證結果、可用的情境版本與至少一個所選情境。
        var export = ReadFrontend("js", "steps", "export-step.js");
        Assert.Contains("var canExport = !!validationRunId && !!scenarioRevision &&", export, StringComparison.Ordinal);
        Assert.Contains("Ui.completenessEligibility(state.lastRuns.validate).isEligible && selectedScenarioPositions.length > 0;", export, StringComparison.Ordinal);
        Assert.Contains("if (!payload.validationRunId || !payload.scenarioRevision ||", export, StringComparison.Ordinal);
        Assert.Contains("var noScenario = state.filter.savedScenarios.length === 0;", export, StringComparison.Ordinal);
        Assert.Contains("var NO_SCENARIO_NOTICE = '目前沒有已儲存的篩選情境，無法匯出工作底稿。請到「進階條件篩選」儲存至少一個篩選情境，再匯出工作底稿。';", export, StringComparison.Ordinal);
        Assert.Contains("(noScenario ? '<p class=\"form-notice\" data-bind=\"export-no-scenario\">' + Ui.esc(NO_SCENARIO_NOTICE) + '</p>' : '')", export, StringComparison.Ordinal);
        Assert.Contains("(noScenario ? '' : scenarioSelectionHtml(state))", export, StringComparison.Ordinal);
        // 開案時列出無法套用目前規則的情境，照舊逐筆顯示。
        Assert.Contains("(needsScenarioResave ? Ui.filterScenarioProblemsHtml(state) : '')", export, StringComparison.Ordinal);
        // 版本紀錄照常列出，不受情境影響。
        Assert.Contains("var workpapers = Ui.reportArtifactHistory(state, 'workingPaper');", export, StringComparison.Ordinal);
    }

    [Fact]
    public void PagedTable_SearchDuringLoadRunsAfterTheCurrentLoadKeepingOnlyTheLast()
    {
        var core = ReadFrontend("js", "ui-core.js").Replace("\r\n", "\n", StringComparison.Ordinal);
        var bind = core[core.IndexOf("function bindPagedTable(", StringComparison.Ordinal)..];
        bind = bind[..bind.IndexOf("\n  }\n", StringComparison.Ordinal)];

        // 讀取中按「查看」或「清除」不再被忽略：只記住最後一次的文字，並在畫面上說明。
        Assert.DoesNotContain("if (state.busy) { return; }", bind, StringComparison.Ordinal);
        var search = ExtractFunction(bind, "search");
        Assert.Contains("if (state.busy) {", search, StringComparison.Ordinal);
        Assert.Contains("queuedSearch = text;", search, StringComparison.Ordinal);
        Assert.Contains("status.textContent = queuedHint;", search, StringComparison.Ordinal);
        Assert.DoesNotContain(".push(", search, StringComparison.Ordinal);
        Assert.Contains("'正在讀取資料，讀完後接著查看這個號碼。'", bind, StringComparison.Ordinal);
        Assert.Contains("'正在讀取資料，讀完後接著清除查看條件。'", bind, StringComparison.Ordinal);

        // 目前的讀取不論成功或失敗都在 finally 解除忙碌後接著執行，失敗後也能再按。
        var finallyBlock = bind[bind.IndexOf(".finally(function () {", StringComparison.Ordinal)..];
        var busyReleased = finallyBlock.IndexOf("state.busy = false;", StringComparison.Ordinal);
        var queuedRun = finallyBlock.IndexOf("runQueuedSearch();", StringComparison.Ordinal);
        Assert.True(busyReleased >= 0 && queuedRun > busyReleased, "讀取結束要先解除忙碌，再執行記住的查看要求。");
        var run = ExtractFunction(bind, "runQueuedSearch");
        Assert.Contains("queuedSearch = null;", run, StringComparison.Ordinal);
        Assert.Contains("if (!current()) { return; }", run, StringComparison.Ordinal);
        Assert.Contains("fetch(true, '依號碼查看明細', previous);", search, StringComparison.Ordinal);
    }

    [Fact]
    public void FrontendTransportErrors_UseChineseInsteadOfEnglishOriginals()
    {
        // W19：後端沒有附訊息或連線中斷時，畫面同樣不顯示英文原文。
        var api = ReadFrontend("js", "jet-api.js");
        Assert.Contains("'發生非預期的錯誤，這個動作沒有完成。請按畫面上方的「輸出支援日誌」，把檔案交給支援人員。'", api, StringComparison.Ordinal);
        foreach (var retired in new[] { "JET bridge request failed.", "JET host bridge is not available.", "JET host bridge was unloaded" })
        {
            Assert.DoesNotContain(retired, api, StringComparison.Ordinal);
        }
    }

    private static string ExtractFunction(string source, string name)
    {
        var marker = name.EndsWith(": function", StringComparison.Ordinal) || name.EndsWith("= function", StringComparison.Ordinal)
            ? name : $"function {name}(";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到 {name} 函式。");

        var depth = 0;
        var opened = false;
        for (var index = start; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
                opened = true;
            }
            else if (source[index] == '}' && opened && --depth == 0)
            {
                return source[start..(index + 1)];
            }
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

        return directory?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET.slnx，無法定位 repo 根。");
    }
}
