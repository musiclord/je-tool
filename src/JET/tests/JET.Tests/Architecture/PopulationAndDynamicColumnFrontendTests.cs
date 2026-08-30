using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 母體分區、來源品質、資料版本失效與動態結果欄的前端守衛。
/// 這些數字與欄位定義全部由後端同一次執行裁定；本組只鎖「畫面複述、不重算、不猜欄位」。
/// </summary>
public sealed class PopulationAndDynamicColumnFrontendTests
{
    [Fact]
    public void PopulationSummary_MirrorsTheBackendPartitionWithoutRecomputing()
    {
        var validate = ReadFrontend("js", "steps", "validate-step.js");

        Assert.Contains("data-bind=\"population-summary\"", validate, StringComparison.Ordinal);
        Assert.Contains("v.populationSummary", validate, StringComparison.Ordinal);
        Assert.Contains("cell('標準化後全部分錄', raw.rowCount", validate, StringComparison.Ordinal);
        Assert.Contains("cell('進入測試母體', effective.rowCount", validate, StringComparison.Ordinal);
        Assert.Contains("cell('期間排除', excluded.byPeriodCount", validate, StringComparison.Ordinal);
        Assert.Contains("cell('過帳狀態排除', excluded.byPostingStatusCount", validate, StringComparison.Ordinal);

        // 四個數字都直接取自 response，不得以相減或加總在畫面重算。
        Assert.DoesNotContain("raw.rowCount -", validate, StringComparison.Ordinal);
        Assert.DoesNotContain("effective.rowCount +", validate, StringComparison.Ordinal);
    }

    [Fact]
    public void ExcludedEntries_HaveTheirOwnBackendDatasetAndReasonLabels()
    {
        var validate = ReadFrontend("js", "steps", "validate-step.js");
        var preview = ReadFrontend("js", "data-preview.js");

        Assert.Contains("data-action=\"preview-excluded-entries\"", validate, StringComparison.Ordinal);
        Assert.Contains("Ui.openDataPreview('glExcludedEntries')", validate, StringComparison.Ordinal);
        Assert.Contains("{ value: 'glExcludedEntries'", preview, StringComparison.Ordinal);

        // 排除原因是後端 closed wire value，畫面只做顯示層對照。
        Assert.Contains("EXCLUSION_REASON_LABELS = { period: '不在查核期間', postingStatus: '過帳狀態不符' }",
            preview, StringComparison.Ordinal);
        Assert.Contains("excludedByPeriodCount", preview, StringComparison.Ordinal);
        Assert.Contains("excludedByPostingStatusCount", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void StaleState_IsMirroredFromTheBackendFlagsNotInferred()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var state = ReadFrontend("js", "state.js");
        var validate = ReadFrontend("js", "steps", "validate-step.js");
        var filter = ReadFrontend("js", "steps", "filter-step.js");

        Assert.Contains("Store.setStaleState(data.staleState || null)", core, StringComparison.Ordinal);
        Assert.Contains("staleState: { validation: false, prescreen: false, filter: false }", state, StringComparison.Ordinal);
        Assert.Contains("data-bind=\"stale-", core, StringComparison.Ordinal);

        Assert.Contains("Ui.staleNoticeHtml(state, 'validation'", validate, StringComparison.Ordinal);
        Assert.Contains("Ui.staleNoticeHtml(state, 'prescreen'", validate, StringComparison.Ordinal);
        Assert.Contains("Ui.staleNoticeHtml(state, 'filter'", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void DynamicResultColumns_ComeFromTheBackendColumnMetadata()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var validate = ReadFrontend("js", "steps", "validate-step.js");
        var filter = ReadFrontend("js", "steps", "filter-step.js");

        // custom 欄一律讀 row.customValues[key]，key set 精確等於 custom columns。
        Assert.Contains("row.customValues ? row.customValues[col.key] : null", core, StringComparison.Ordinal);
        Assert.Contains("function dynamicColumnHeadHtml(columns)", core, StringComparison.Ordinal);
        Assert.Contains("function dynamicColumnCells(columns)", core, StringComparison.Ordinal);

        // INF 抽樣明細與篩選命中明細都以同一份欄位定義渲染表頭與資料列。
        Assert.Contains("Ui.dynamicColumnHeadHtml(columns)", validate, StringComparison.Ordinal);
        Assert.Contains("Ui.dynamicColumnCells(columns)", validate, StringComparison.Ordinal);
        Assert.Contains("Ui.dynamicColumnHeadHtml(columns)", filter, StringComparison.Ordinal);
        Assert.Contains("Ui.dynamicColumnCells(columns)", filter, StringComparison.Ordinal);

        // 舊的固定 INF 欄序已退場，不得在前端保留第二份 schema。
        Assert.DoesNotContain("'借方', '貸方', '總帳日期', '核准日'", validate, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceQualityPage_IsTheOnlyFullDetailPathForSourceFindings()
    {
        var validate = ReadFrontend("js", "steps", "validate-step.js");

        Assert.Contains("querySourceQualityPage({ cursor: cursor, pageSize: 200 })", validate, StringComparison.Ordinal);
        Assert.Contains("sourceQualityCategoryLabel", validate, StringComparison.Ordinal);
        Assert.Contains("r.sourceRowNumber", validate, StringComparison.Ordinal);
        Assert.Contains("r.sourceLabel", validate, StringComparison.Ordinal);
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
