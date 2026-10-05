using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 2026-10-02 主線實測的畫面缺口守衛。W6：第三步欄位配對的下拉、借方代碼、逐值單選鈕組與兩個
/// 「加入」按鈕都要有可分辨的無障礙名稱，名稱取自欄位目錄或來源值，不寫死。W8：科目配對檔正在準備
/// 或準備失敗時，「在 JET 配對」分頁也要看得到一行狀態；成功時只在「用 Excel 配對」分頁呈現。
/// 實際渲染結果另由 tools/tests 的 Node 行為檢查核對。
/// </summary>
public sealed class MappingNamesAndTemplateStatusFrontendTests
{
    [Fact]
    public void MappingControls_TakeAccessibleNamesFromTheFieldCatalogAndSourceValues()
    {
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");
        var uiCore = ReadFrontend("js", "ui-core.js");

        // 借方代碼的名稱來自欄位目錄的標籤。
        Assert.Contains("{ key: 'dcDebitCode', label: '借方代碼'", uiCore, StringComparison.Ordinal);

        // 簡易清單：來源欄下拉以「<欄位標籤>的來源欄」命名，借方代碼輸入框以欄位標籤命名。
        Assert.Contains("'\" aria-label=\"' + Ui.esc(field.label) + '的來源欄\"'", mapping, StringComparison.Ordinal);
        Assert.Contains("'\" aria-label=\"' + Ui.esc(field.label) + '\"' +", mapping, StringComparison.Ordinal);
        // 對照表格：借方代碼輸入框以欄位標籤命名，提示另用 aria-describedby；來源欄下拉以來源欄命名。
        Assert.Contains("'\" aria-label=\"' + Ui.esc(litField.label) + '\" aria-describedby=\"' + litId + '-hint\"'", mapping, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"來源欄「' + Ui.esc(col) + '」對應的分錄測試欄位\"", mapping, StringComparison.Ordinal);

        // 逐值的人工或自動選項：每組以來源值命名；單選鈕的 name、value 與資料屬性不變。
        Assert.Contains("role=\"' + (mode === 'reject' ? 'radiogroup' : 'group') +", mapping, StringComparison.Ordinal);
        Assert.Contains("'\" aria-label=\"來源值「' + Ui.esc(item.value) + '」\">'", mapping, StringComparison.Ordinal);
        Assert.Contains("name=\"manual-code-' + index + '\" value=\"' + value + '\"", mapping, StringComparison.Ordinal);
        Assert.Contains("' data-manual-assign=\"' + Ui.esc(item.value) + '\"'", mapping, StringComparison.Ordinal);

        // 兩個「加入」按鈕可見文字不變，名稱可分辨。
        Assert.Contains("data-code-group=\"manual\" aria-label=\"加入人工代碼\"", mapping, StringComparison.Ordinal);
        Assert.Contains("data-code-group=\"automatic\" aria-label=\"加入自動代碼\"", mapping, StringComparison.Ordinal);
        Assert.Contains("data-action=\"add-posting-value\" aria-label=\"加入已過帳的值\"", mapping, StringComparison.Ordinal);
    }

    [Fact]
    public void MappingTexts_UsePlainWordsRequestedByTheAuditor()
    {
        var mapping = ReadFrontend("js", "steps", "mapping-step.js");

        // 使用者 2026-09-21：按鈕命名成「預覽」就好；2026-09-22：不需要繞口難讀的文言文。
        Assert.DoesNotContain("預覽來源資料", mapping, StringComparison.Ordinal);
        Assert.Contains("'\" title=\"開啟資料預覽，對照欄位名稱與實際內容\">預覽</button>'", mapping, StringComparison.Ordinal);
        Assert.DoesNotContain("字面值（不是欄位名稱）", mapping, StringComparison.Ordinal);
        // 第二遍第9批共用迴圈渲染借、貸兩碼，借方可見原句不變，不能再要求整句是單一source literal。
        // 首次失敗：20261004-100752118-a195c091201e444d858ec7d7bee3d291；Node原完整借方句斷言仍保留。
        var literals = ExtractBetween(mapping, "var literalHtml = literalFieldFor(fields, mode).map(", "var eligibility = mappingCommitEligibility");
        Assert.Contains("var example = litField.key === 'dcCreditCode' ? 'C 或 2' : 'D 或 1'", literals, StringComparison.Ordinal);
        Assert.Contains("-hint\">輸入代表' +", literals, StringComparison.Ordinal);
        Assert.Contains("(litField.key === 'dcCreditCode' ? '貸方' : '借方') + '的代碼，例如 ' + example + '</span></div>'", literals, StringComparison.Ordinal);
    }

    [Fact]
    public void AccountTemplateStatus_ShowsOnTheJetTabOnlyWhilePreparingOrAfterFailure()
    {
        var validate = ReadFrontend("js", "steps", "validate-step.js");

        // 「在 JET 配對」分頁先放狀態列，再放科目清單；「用 Excel 配對」分頁沿用原本的狀態呈現。
        Assert.Contains(": accountTemplateJetStatusHtml() + Ui.accountMappingEditor.html(state, ready)", validate, StringComparison.Ordinal);
        Assert.Contains("validationOutputStatusHtml('template') +", validate, StringComparison.Ordinal);

        var jet = ExtractBetween(validate, "function accountTemplateJetStatusHtml(", "function validationCardHtml(");
        Assert.Contains("output.status === 'pending'", jet, StringComparison.Ordinal);
        Assert.Contains("if (output.status !== 'failed') { return ''; }", jet, StringComparison.Ordinal);
        Assert.Contains("科目配對檔尚未準備好：", jet, StringComparison.Ordinal);
        Assert.Contains("Ui.esc(output.reason", jet, StringComparison.Ordinal);
        Assert.Contains("data-action=\"retry-account-template-in-excel\"", jet, StringComparison.Ordinal);
        Assert.Contains("到「用 Excel 配對」重試", jet, StringComparison.Ordinal);
        // Excel 分頁的桌面情境以 data-validation-output="template" 讀狀態，JET 分頁不得重用。
        Assert.DoesNotContain("data-validation-output=", jet, StringComparison.Ordinal);

        // 失敗原因另存一份，JET 分頁才不會沿用「可使用此區按鈕重試」這句只適用 Excel 分頁的話。
        Assert.Contains("reason: error && error.message ? error.message : '',", validate, StringComparison.Ordinal);

        var retry = ExtractBetween(validate, "var retryTemplateInExcel", "Ui.bindReportArtifacts(container);");
        Assert.Contains("accountExcelOpen = true;", retry, StringComparison.Ordinal);
        Assert.Contains("[data-action=\"ensure-account-mapping-template\"]", retry, StringComparison.Ordinal);
    }

    private static string ExtractBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到起點標記：{startMarker}");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"找不到終點標記：{endMarker}");
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
