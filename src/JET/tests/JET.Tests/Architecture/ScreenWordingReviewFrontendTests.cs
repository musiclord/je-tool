using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 2026-10-02 整體複審的畫面文字修正：只改顯示文字與格式，不改 action、識別字或底稿內容。
/// 每項都鎖住新字樣，並確認舊字樣不再出現在同一個位置。
/// </summary>
public sealed class ScreenWordingReviewFrontendTests
{
    [Fact]
    public void K3_CashCounterpartExplanation_UsesExactApprovedText()
    {
        Assert.Contains("現金不算一般對方科目；要排除現金銷貨，改用預篩選條件『未預期借貸組合』。",
            ReadFrontend("js", "ui-core.js"), StringComparison.Ordinal);
    }

    [Fact]
    public void FieldMappingAndVoucherDetail_UseShortenedNames()
    {
        // W5：使用者 2026-10-02 裁定「借方代碼」與「傳票文件項次」。
        var core = ReadFrontend("js", "ui-core.js");
        Assert.Contains("{ key: 'dcDebitCode', label: '借方代碼',", core, StringComparison.Ordinal);
        Assert.DoesNotContain("借方標識代碼", core, StringComparison.Ordinal);
        Assert.DoesNotContain("借方標識代碼", ReadSource("Domain", "Rules", "JetFieldCatalog.cs"), StringComparison.Ordinal);

        var vouchers = ReadFrontend("js", "filter-vouchers.js");
        Assert.Contains("['狀態', '傳票文件項次', '總帳入帳日'", vouchers, StringComparison.Ordinal);
        Assert.DoesNotContain("'傳票項次'", vouchers, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletenessReason_DistinguishesNeverRunFromOutdatedSummary()
    {
        // W7：從未執行資料驗證時請審計員「先執行」；舊摘要缺判定欄位時才「重新執行」。
        var core = ReadFrontend("js", "ui-core.js");
        var eligibility = ExtractFunction(core, "completenessEligibility");

        Assert.Contains("var COMPLETENESS_FIRST_RUN_REASON = '請先執行資料驗證，以取得目前資料的結果';", core, StringComparison.Ordinal);
        Assert.Contains("var COMPLETENESS_RERUN_REASON = '請重新執行資料驗證，以取得目前資料的結果';", core, StringComparison.Ordinal);
        Assert.Contains("if (!validation) {", eligibility, StringComparison.Ordinal);
        Assert.Contains("reason: COMPLETENESS_FIRST_RUN_REASON", eligibility, StringComparison.Ordinal);
        Assert.Contains(": COMPLETENESS_RERUN_REASON", eligibility, StringComparison.Ordinal);
        Assert.True(
            eligibility.IndexOf("if (!validation) {", StringComparison.Ordinal)
                < eligibility.IndexOf("COMPLETENESS_RERUN_REASON", StringComparison.Ordinal),
            "沒有驗證結果的判斷必須在舊摘要判斷之前。");
    }

    [Fact]
    public void KctAndLegacyLetters_AreLabelledBySource_WithoutChangingKctAutoNaming()
    {
        // W9：KCT A 到 J 與舊表 A 到 U 字母相同、意思不同，畫面要標明來源。
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        var kct = ExtractFunction(filter, "kctPickerHtml");
        var legacy = ExtractFunction(filter, "legacyPickerHtml");

        Assert.Contains("KCT 條件 A 到 J", kct, StringComparison.Ordinal);
        Assert.Contains("<summary>舊表條件 A 到 U 與範例</summary>", legacy, StringComparison.Ordinal);
        Assert.Contains("<span class=\"visually-hidden\">舊表條件 A 到 U</span>", legacy, StringComparison.Ordinal);
        Assert.Contains("Ui.esc('舊表 ' + entry.letter + '：' + entry.label)", legacy, StringComparison.Ordinal);
        Assert.Contains(">加入舊表 ' + item.letter + ' 條件</button>", legacy, StringComparison.Ordinal);
        Assert.Contains("Ui.esc('舊表 ' + example.combination)", legacy, StringComparison.Ordinal);
        Assert.DoesNotContain("A–U 條件", legacy, StringComparison.Ordinal);

        // K10 改用清單內的評估說明；自動命名不變。
        // 使用者 2026-10-07 裁定同一組混有自訂條件時名稱接「+自訂」；只有 KCT 卡片時仍是字母相接（第一次失敗收據 20261007-035823939-41943206b5f747f1b7fb8dfe79f987d7）。
        Assert.Contains("tokens.push(letters.join('+') + (hasCustom ? '+自訂' : ''));", ExtractFunction(filter, "kctScenarioName"), StringComparison.Ordinal);
        Assert.Contains(".map(function (item) { return item.letter + '：' + (item.defaultRationale || item.label); })", ExtractFunction(filter, "kctScenarioRationale"), StringComparison.Ordinal);
    }

    [Fact]
    public void KctSourceTab_ShowsTheLetterRangeOnScreen()
    {
        // 2026-10-03 主線在瀏覽器主機重測 S9 時發現：第五步工作區把 KCT 區塊標頭整個藏起來
        // （app.css 的 .filter-workspace .filter-entry .condition-picker__head），審計員實際看到的是左側分頁鈕。
        // W9 要標明 KCT 的字母範圍，所以分頁鈕本身寫出 A 到 J。
        var filter = ReadFrontend("js", "steps", "filter-step.js");

        Assert.Contains("sourceButton('kct', 'KCT 條件 A 到 J')", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("'KCT條件'", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void CriteriaReportAndMatrix_UsePlainActionNames()
    {
        // W13、W21：按鈕與操作名稱不再重複「完成」；矩陣分頁改稱「符合條件摘要」。
        var filter = ReadFrontend("js", "steps", "filter-step.js");

        Assert.Contains("(regenerate ? '重新產生條件篩選報告' : '產生條件篩選報告')", filter, StringComparison.Ordinal);
        Assert.Contains("Ui.run('產生條件篩選報告', function () {", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("完成條件篩選並產生報告", filter, StringComparison.Ordinal);
        Assert.Contains("matrixViewButton('scenarios', '符合條件摘要')", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("命中摘要", filter, StringComparison.Ordinal);

        // T7：空狀態說明每個情境成為矩陣的一欄，不再出現 C1..CN 這類內部記號。
        Assert.Contains("尚未儲存任何篩選情境；先在上方儲存情境，每個情境會成為矩陣的一欄。", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("C1..CN", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void ScenarioSaveTime_UsesSharedLocalDateTimeFormat()
    {
        // W14：第六步與總覽共用同一個時間格式函式，不直接顯示後端 ISO 字串。
        var core = ReadFrontend("js", "ui-core.js");
        var format = ExtractFunction(core, "formatDateTime");
        Assert.Contains("toLocaleString('zh-Hant'", format, StringComparison.Ordinal);
        Assert.Contains("year: 'numeric', month: '2-digit', day: '2-digit'", format, StringComparison.Ordinal);
        Assert.Contains("hour: '2-digit', minute: '2-digit', hour12: false", format, StringComparison.Ordinal);
        Assert.Contains("formatDateTime: formatDateTime,", core, StringComparison.Ordinal);

        var app = ReadFrontend("js", "app.js");
        var overviewWhen = ExtractFunction(app, "overviewWhen");
        Assert.Contains("return Ui.formatDateTime(iso);", overviewWhen, StringComparison.Ordinal);
        Assert.DoesNotContain("toLocaleString", overviewWhen, StringComparison.Ordinal);

        var export = ReadFrontend("js", "steps", "export-step.js");
        Assert.Contains("條件儲存時間：", export, StringComparison.Ordinal);
        Assert.Contains("Ui.formatDateTime(filterRevision(state))", export, StringComparison.Ordinal);
        Assert.DoesNotContain("條件保存時間", export, StringComparison.Ordinal);
        Assert.DoesNotContain("Ui.esc(filterRevision(state) ||", export, StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_UsesCompleteSentencesAndChineseNames()
    {
        var app = ReadFrontend("js", "app.js");

        // W16：不適用規則的說明改成完整句子。
        Assert.Contains("列在「不適用規則」的條件因缺少所需資料或設定而沒有執行，不代表沒有符合的分錄。", ExtractFunction(app, "overviewDisclaimerHtml"), StringComparison.Ordinal);
        Assert.DoesNotContain("不適用不等於零", app, StringComparison.Ordinal);

        // T3：總覽與重複匯出的提示用「工作底稿」。
        Assert.Contains("overviewFactHtml('export.working-paper', '工作底稿',", app, StringComparison.Ordinal);
        Assert.Contains("['產生目前版本的工作底稿']", app, StringComparison.Ordinal);
        Assert.DoesNotContain("'Working Paper'", app, StringComparison.Ordinal);
        Assert.DoesNotContain("的 Working Paper'", app, StringComparison.Ordinal);
        var store = ReadSource("Infrastructure", "FileIO", "ProjectReportArtifactStore.cs");
        Assert.Contains("\"同名工作底稿已存在，原檔已保留。", store, StringComparison.Ordinal);
        Assert.DoesNotContain("\"同名 Working Paper 已存在", store, StringComparison.Ordinal);

        // Q8 supersedes the old T4 wording with confirmation status, not submission terminology.
        // First failure: 20261004-091831760-a762b9ef96db4d61a4bb2bff7cdb0022.
        Assert.DoesNotContain("'尚未提交'", app, StringComparison.Ordinal);
        Assert.Contains("\"尚未確認 TB 欄位配對，無法執行完整性測試。\"", ReadSource("AuditCore", "CompletenessPartBProcedure.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void PersonValues_AreNamedByPurpose()
    {
        // T8：依建立人員彙總的表頭用「編製人員」；計數與說明用「人員代號或姓名」。
        var validate = ReadFrontend("js", "steps", "validate-step.js");
        Assert.Contains("columns: ['編製人員', '分錄筆數', '借方', '貸方', '人工分錄筆數']", validate, StringComparison.Ordinal);

        Assert.Contains("isPerson ? '每行一個人員代號或姓名'", ReadFrontend("js", "filter-values.js"), StringComparison.Ordinal);
        var import = ReadFrontend("js", "steps", "import-step.js");
        Assert.Contains("有效授權清單：' + info.rowCount + ' 個人員代號或姓名' +", import, StringComparison.Ordinal);
        Assert.Contains("' 個有效人員代號或姓名。'", import, StringComparison.Ordinal);
        Assert.Contains("preparerName: '人員代號或姓名',", ReadFrontend("js", "data-preview.js"), StringComparison.Ordinal);
        Assert.Contains("依匯入後的科目或人員分組；空白欄位不列入。", ReadFrontend("js", "steps", "filter-step.js"), StringComparison.Ordinal);

        foreach (var source in new[]
        {
            validate,
            import,
            ReadFrontend("js", "filter-values.js"),
            ReadFrontend("js", "data-preview.js"),
            ReadFrontend("js", "steps", "filter-step.js")
        })
        {
            Assert.DoesNotContain("人員識別值", source, StringComparison.Ordinal);
            Assert.DoesNotContain("人員代號或姓名或", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AccountMappingAndNullRecords_UseReviewedWording()
    {
        var validate = ReadFrontend("js", "steps", "validate-step.js");

        // T10：科目配對狀態與「儲存」按鈕一致。
        Assert.Contains("目前已儲存 ' + info.rowCount + ' 個科目的配對。", validate, StringComparison.Ordinal);
        Assert.Contains("尚未儲存科目配對。", validate, StringComparison.Ordinal);
        Assert.DoesNotContain("目前已保存 ' + info.rowCount", validate, StringComparison.Ordinal);
        Assert.DoesNotContain("尚未保存科目配對", validate, StringComparison.Ordinal);
        var preview = ReadFrontend("js", "data-preview.js");
        Assert.Contains("case 'accountMappings': return '尚未儲存科目配對；", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("尚未保存科目配對", preview, StringComparison.Ordinal);

        // T11：空值計數的名稱直接說明可能重複計入。
        Assert.Contains("' 筆、空值項目合計（同一分錄可能重複計入）' + (Number(", validate, StringComparison.Ordinal);
        Assert.DoesNotContain("異常項次合計", validate, StringComparison.Ordinal);
    }

    private static string ExtractFunction(string source, string name)
    {
        var start = source.IndexOf($"function {name}(", StringComparison.Ordinal);
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
        => ReadSource(new[] { "wwwroot" }.Concat(segments).ToArray());

    private static string ReadSource(params string[] segments)
        => File.ReadAllText(
            Path.Combine(new[] { RepoRoot(), "JET" }.Concat(segments).ToArray()));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET.slnx。");
    }
}
