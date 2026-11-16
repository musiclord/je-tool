using System.Text;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 同一個動作的按鈕在各步驟用同一個名稱；按下後的作業名稱、提示句與報讀名稱也照畫面上的字寫。
/// 每項逐字鎖住現行字樣，並確認換掉的字樣不再出現在畫面程式。程式註解不算畫面文字，檢查前先移除。
/// </summary>
public sealed class ButtonNameUnificationFrontendTests
{
    [Fact]
    public void FolderButtons_AllSayOpenFolder_AndTheirOperationsUseTheSameName()
    {
        // 頁首、第四步科目配對與報告區的按鈕都在檔案總管開啟案件資料夾，報告區那顆另外選取報告檔。
        Assert.Contains("title=\"在檔案總管開啟目前案件的資料夾\" hidden>開啟資料夾</button>", Html(), StringComparison.Ordinal);
        Assert.Contains("Ui.run('開啟資料夾', function () {", Script("app.js"), StringComparison.Ordinal);
        var validate = Script("steps", "validate-step.js");
        Assert.Contains("data-action=\"open-account-mapping-folder\">開啟資料夾</button>", validate, StringComparison.Ordinal);
        Assert.Contains("Ui.run('開啟資料夾', function () {", validate, StringComparison.Ordinal);
        // 報告區的按鈕字原本就是「開啟資料夾」，按下後的作業名稱也寫同一個名稱。
        var core = Script("ui-core.js");
        Assert.Contains("'>開啟資料夾</button>' : '') +", core, StringComparison.Ordinal);
        Assert.Contains("esc(report.reportId) + '\">開啟資料夾</button>' : '';", core, StringComparison.Ordinal);
        Assert.Contains("run('開啟資料夾', function () {", core, StringComparison.Ordinal);

        AssertRetiredEverywhere("開啟案件資料夾", "顯示報告檔案");
    }

    [Fact]
    public void RetryButtonsAfterAReadFailure_AllSayReload_AndPromptsNameThatButton()
    {
        // 讀取失敗後就地重讀的按鈕都寫「重新載入」，和共用讀取失敗提示的按鈕同一個字。
        Assert.Contains("controlAttributes(retryAttributes) + '>重新載入</button>'", Script("ui-core.js"), StringComparison.Ordinal);
        Assert.Contains("retry.textContent = '重新載入';", Script("ui-core.js"), StringComparison.Ordinal);
        Assert.Contains("data-detail-retry>重新載入</button>", Script("steps", "validate-step.js"), StringComparison.Ordinal);
        Assert.Equal(2, Occurrences(Script("data-preview.js"), "Ui.esc(dataset) + '\">重新載入</button>'"));
        Assert.Contains("data-voucher-retry>重新載入</button>", Script("filter-vouchers.js"), StringComparison.Ordinal);
        var mapping = Script("steps", "mapping-step.js");
        Assert.Contains("data-source-retry>重新載入</button>", mapping, StringComparison.Ordinal);
        var saved = Script("steps", "filter-step-saved.js");
        Assert.Contains("data-scenario-retry>重新載入</button>", saved, StringComparison.Ordinal);
        Assert.Contains("data-action=\"delete-preview-retry\" data-collapsible hidden>重新載入</button>", Script("app.js"), StringComparison.Ordinal);

        // 提示句用「」指名按鈕時，寫的是畫面上那顆按鈕的字。
        Assert.Contains("'讀取來源值失敗，可按「重新載入」。'", mapping, StringComparison.Ordinal);
        Assert.Contains("'<p>可按「重新載入」重試。</p>'", saved, StringComparison.Ordinal);

        AssertRetiredEverywhere(">重試</button>", "= '重試'", "重試讀取結果", "重新確認數量", "「再試一次」");
    }

    [Fact]
    public void SavedScenarioMenu_UsesTheVerbsOfTheUnsavedScenarioDialog()
    {
        // 「操作」選單的「編輯」「複製」和「篩選情境尚未儲存」對話框用同一組動詞；
        // 對話框的內文與兩顆按鈕都由這個動詞組成。
        var saved = Script("steps", "filter-step-saved.js");
        Assert.Contains("data-action=\"edit-scenario\" data-index=\"' + i + '\">編輯</button>", saved, StringComparison.Ordinal);
        Assert.Contains("data-action=\"copy-scenario\" data-index=\"' + i + '\">複製</button>", saved, StringComparison.Ordinal);
        Assert.Contains("var verb = kind === 'copy' ? '複製' : '編輯';", saved, StringComparison.Ordinal);
        Assert.Contains("continueLabel: '不儲存，直接' + wording.verb,", saved, StringComparison.Ordinal);
        Assert.Contains("saveLabel: '儲存並' + wording.verb,", saved, StringComparison.Ordinal);

        // 編輯區按下就儲存的那顆叫「另存為新情境」，選單的「複製」不會儲存。
        Assert.Contains(">另存為新情境</button>", Script("steps", "filter-step-rules.js"), StringComparison.Ordinal);
        AssertRetiredEverywhere("另存副本");
    }

    [Fact]
    public void UnsavedAccountCategoryDialog_NamesTheActionTheAuditorPressed()
    {
        // 改動會被丟掉時，第二顆按鈕寫「不儲存，直接」接審計員按下的按鈕名稱；改動留在畫面上時寫「先不儲存」。
        var core = Script("ui-core.js");
        Assert.Contains("confirmPendingAccountCategories('退出後，這些改動不會保留。', '不儲存，直接退出')", core, StringComparison.Ordinal);
        Assert.Contains("confirmPendingAccountCategories('回首頁後，這些改動不會保留。', '不儲存，直接回首頁')", core, StringComparison.Ordinal);
        const string beforePrescreen = "Ui.confirmPendingAccountCategories('預篩選只會使用已儲存的分類。', '先不儲存')";
        Assert.Contains(beforePrescreen, Script("steps", "validate-step.js"), StringComparison.Ordinal);
        Assert.Contains(beforePrescreen, Script("steps", "export-step.js"), StringComparison.Ordinal);

        AssertRetiredEverywhere("不儲存，直接離開", "離開案件後，這些改動不會保留。", "先不儲存，直接執行");
    }

    [Fact]
    public void CaseListRefreshButton_IsNamedAfterTheCaseList()
    {
        // 後端在案件資料夾不見時請審計員「重新整理案件清單」，按鈕用同一個名稱。
        Assert.Contains("data-action=\"picker-refresh-local\">重新整理案件清單</button>", Script("app.js"), StringComparison.Ordinal);
        AssertRetiredEverywhere("重新整理本機");
    }

    [Fact]
    public void ExportThatRunsPrescreenFirst_NamesItsOperationAfterTheButton()
    {
        // 第六步要先執行預篩選時，按鈕與按下後的作業名稱同一個字；已有預篩選結果時照舊。
        var export = Script("steps", "export-step.js");
        Assert.Contains("if (plan.needsRun) { return '先執行預篩選並產生底稿'; }", export, StringComparison.Ordinal);
        Assert.Contains("Ui.run('先執行預篩選並產生底稿'", export, StringComparison.Ordinal);
        Assert.Contains("Ui.run('產生工作底稿與預篩選報告'", export, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualAutoPrompts_NameTheCurrentSelectorAndItsOptions()
    {
        // 第三步人工與自動分錄的下拉選單叫「如何區分人工與自動」，選項是「只指定人工代碼」等；
        // 提示句與確認配對失敗的原因都照這個標籤與選項寫。
        var mapping = Script("steps", "mapping-step.js");
        Assert.Contains("<label>如何區分人工與自動<select", mapping, StringComparison.Ordinal);
        Assert.Contains("[['reject', '分別指定人工、自動'], ['automatic', '只指定人工代碼'],", mapping, StringComparison.Ordinal);
        Assert.Contains("'人工與自動代碼各需至少一個值；只想指定一側時，請在「如何區分人工與自動」選「只指定人工代碼」或「只指定自動代碼」'",
            mapping, StringComparison.Ordinal);
        Assert.Contains("'值太多，只列出最常出現的幾種；可以改成只指定一側的代碼，其餘非空白值依「如何區分人工與自動」的選擇歸類。'",
            mapping, StringComparison.Ordinal);
        Assert.Contains("'值太多，只列出最常出現的幾種；沒有列出的值依「如何區分人工與自動」的選擇歸類。'", mapping, StringComparison.Ordinal);
        Assert.Contains("'」未歸類為人工或自動，請歸類，或改成只指定一側的代碼。'", mapping, StringComparison.Ordinal);
        foreach (var retired in new[] { "判定方式", "只列一側", "只列人工", "只列自動" })
        {
            Assert.DoesNotContain(retired, mapping, StringComparison.Ordinal);
        }

        var reasons = TerminologyUnificationFrontendTests.StripComments(
            ProductSources.Product("Domain", "Rules", "ProjectionErrorReasons.cs"), js: false);
        Assert.Contains("\"未歸類為人工或自動。請將這些值歸類，或在「如何區分人工與自動」改成只指定一側的代碼\"", reasons, StringComparison.Ordinal);
        Assert.DoesNotContain("判定方式", reasons, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthorizedPreparerCard_CallsItsFileTheSourceFile()
    {
        // 授權編製人員清單卡片上方寫「來源檔：檔名」，同一張卡片其他地方也叫「來源檔」。
        var import = Script("steps", "import-step.js");
        Assert.Contains("'<p>來源檔：' + Ui.esc(draft.fileName) + '</p>'", import, StringComparison.Ordinal);
        Assert.Contains("'<p>下方是來源檔前 10 列。完成匯入後，授權編製人員清單只保留選定欄位去除空白及重複後的識別值。</p>'",
            import, StringComparison.Ordinal);
        Assert.Contains("'<p role=\"alert\">來源檔預覽失敗：'", import, StringComparison.Ordinal);
        Assert.Contains("data-action=\"retry-authorized-preparer-preview\">重讀來源檔預覽</button>", import, StringComparison.Ordinal);
        Assert.Equal(2, Occurrences(import, "Ui.run('讀取授權編製人員清單來源檔預覽'"));
        Assert.DoesNotContain("原檔", import, StringComparison.Ordinal);
    }

    [Fact]
    public void OtherButtonsAndOperations_UseTheNameTheSameActionHasElsewhere()
    {
        var validate = Script("steps", "validate-step.js");
        // 收起確認、不移除的按鈕和其他確認一樣寫「取消」。
        Assert.Contains("data-action=\"cancel-category-remove\">取消</button>", validate, StringComparison.Ordinal);
        // 重新產生時是「重新」接第一次的字。
        Assert.Contains("(complete ? '重新產生報告與抽樣清單' : '產生報告與抽樣清單')", validate, StringComparison.Ordinal);
        // 展開區標題右側的提示字和流程總覽一樣寫「展開」。
        Assert.Contains("<span class=\"prescreen-signals__hint\">展開</span>", validate, StringComparison.Ordinal);
        Assert.Contains("<span class=\"overview__analysis-hint\">展開</span>", Script("overview-bi.js"), StringComparison.Ordinal);
        // 「查看符合的傳票」按下後的作業名稱照按鈕寫。
        Assert.Contains("Ui.run('查看符合的傳票'", ProductSources.FilterStepFrontend(), StringComparison.Ordinal);
        // 兩個入口輸出的是同一份診斷日誌。
        Assert.Contains("data-action=\"picker-dev-log-export\">輸出診斷日誌</button>", Script("app.js"), StringComparison.Ordinal);
        Assert.Contains("data-action=\"dev-log-export-file\">輸出診斷日誌</button>", Script("dev-log-panel.js"), StringComparison.Ordinal);
        // 步驟鎖定時列出的前置工作用畫面上的作業名稱「開啟案件」。
        Assert.Contains("missing.push('建立或開啟案件');", Script("ui-core.js"), StringComparison.Ordinal);
        // 兩份清單的完成句和按鈕「上傳…清單」同一個動詞；選檔視窗的標題都以「選擇」開頭。
        var import = Script("steps", "import-step.js");
        Assert.Contains("'國定假日清單上傳完成：' + Ui.countText(data.count) + ' 天。'", import, StringComparison.Ordinal);
        Assert.Contains("'補班日清單上傳完成：' + Ui.countText(data.count) + ' 天。'", import, StringComparison.Ordinal);
        Assert.Contains("hostSelectFile({ title: '選擇授權編製人員清單', extensions: ['.xlsx'] })", import, StringComparison.Ordinal);

        AssertRetiredEverywhere("先不移除", "重新產生兩份報告", "展開查看", "預覽篩選情境", "輸出診斷日誌（DEV）", ">輸出日誌</button>",
            "建立或載入案件", "國定假日匯入完成", "補班日匯入完成");
    }

    [Fact]
    public void AccessibleNames_StartWithTheVisibleTextAndNameTheObject()
    {
        // 子條件組與整張傳票條件的「移除」和一般條件同一個報讀名稱與滑鼠提示。
        var rules = Script("steps", "filter-step-rules.js");
        Assert.Contains("<button type=\"button\" class=\"rule-row__remove\" data-compound-remove aria-label=\"移除這條條件\" title=\"移除這條條件\">移除</button>",
            rules, StringComparison.Ordinal);
        // 「加入」念出要加入什麼，取自旁邊的標籤與輸入框。
        Assert.Contains("data-child-add aria-label=\"加入子條件\">加入</button>", rules, StringComparison.Ordinal);
        Assert.Contains("data-date-add aria-label=\"加入日期\">加入</button>", Script("filter-values.js"), StringComparison.Ordinal);
        // 選單按鈕和它打開的選單同一個報讀名稱；資料預覽念出要看哪一份資料。
        var validate = Script("steps", "validate-step.js");
        Assert.Contains("aria-controls=\"account-mapping-more\" aria-label=\"更多科目配對範本操作\">更多</button>", validate, StringComparison.Ordinal);
        Assert.Contains("data-action=\"preview-account-mapping\" aria-label=\"資料預覽：科目配對\">資料預覽</button>", validate, StringComparison.Ordinal);
        // 案件清單同一列的兩顆按鈕都念出案件名稱。
        var app = Script("app.js");
        Assert.Contains("'aria-label=\"開啟案件 ' + Ui.esc(displayName) + '\">'", app, StringComparison.Ordinal);
        Assert.Contains("'title=\"刪除案件\" aria-label=\"刪除案件 ' + Ui.esc(displayName) + '\">' + ICON_TRASH + '</button>'", app, StringComparison.Ordinal);

        // 訊息面板的按鈕報讀名稱以按鈕上的字開頭；面板裡的是「訊息」，不是重新匯入時寫下的操作紀錄。
        var html = Html();
        Assert.Contains("title=\"複製最近 100 則訊息\" aria-label=\"複製紀錄（最近 100 則訊息）\">複製紀錄</button>", html, StringComparison.Ordinal);
        Assert.Contains("title=\"輸出去識別的支援日誌\">輸出支援日誌</button>", html, StringComparison.Ordinal);
        Assert.Contains("button.setAttribute('aria-label', '複製紀錄（最近 100 則訊息）');", app, StringComparison.Ordinal);
        Assert.Contains("button.setAttribute('aria-label', '正在整理最近 100 則訊息');", app, StringComparison.Ordinal);
        Assert.DoesNotContain("操作紀錄", html, StringComparison.Ordinal);
        Assert.DoesNotContain("操作紀錄", app, StringComparison.Ordinal);
    }

    private static void AssertRetiredEverywhere(params string[] retired)
    {
        var root = Path.Combine(ProductSources.ProductRoot, "wwwroot");
        var files = Directory.GetFiles(Path.Combine(root, "js"), "*.js", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "vendor" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(files);

        var hits = new List<string>();
        foreach (var file in files)
        {
            var code = TerminologyUnificationFrontendTests.StripComments(File.ReadAllText(file), js: true);
            hits.AddRange(retired.Where(term => code.Contains(term, StringComparison.Ordinal))
                .Select(term => Path.GetFileName(file) + "：" + term));
        }

        var html = Html();
        hits.AddRange(retired.Where(term => html.Contains(term, StringComparison.Ordinal)).Select(term => "index.html：" + term));
        Assert.Empty(hits);
    }

    private static string Script(params string[] parts)
        => TerminologyUnificationFrontendTests.StripComments(ProductSources.Frontend(parts), js: true);

    private static string Html() => StripHtmlComments(ProductSources.Product("wwwroot", "index.html"));

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string StripHtmlComments(string html)
    {
        var output = new StringBuilder(html.Length);
        var index = 0;
        while (index < html.Length)
        {
            var start = html.IndexOf("<!--", index, StringComparison.Ordinal);
            if (start < 0) { output.Append(html, index, html.Length - index); break; }
            output.Append(html, index, start - index);
            var end = html.IndexOf("-->", start + 4, StringComparison.Ordinal);
            index = end < 0 ? html.Length : end + 3;
        }

        return output.ToString();
    }
}
