using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 畫面文字的守衛：只管顯示文字與格式，不涉及 action、識別字或底稿內容。
/// 每項都鎖住現行字樣，並確認舊字樣不出現在同一個位置。
/// </summary>
public sealed class ScreenWordingReviewFrontendTests
{
    [Fact]
    public void FieldMappingAndVoucherDetail_UseShortenedNames()
    {
        // 畫面寫「借方代碼」與「傳票文件項次」。
        var core = ReadFrontend("js", "ui-core.js");
        Assert.Contains("{ key: 'drCrDebitCode', label: '借方代碼',", core, StringComparison.Ordinal);
        Assert.DoesNotContain("借方標識代碼", core, StringComparison.Ordinal);
        Assert.DoesNotContain("借方標識代碼", ReadSource("Domain", "Rules", "JetFieldCatalog.cs"), StringComparison.Ordinal);

        var vouchers = ReadFrontend("js", "filter-vouchers.js");
        // 分錄併在傳票表裡，沒有自己的表頭。
        // 傳票文件項次自成一欄，表頭寫短表頭「項次」，分錄列只寫數字。
        // 表頭替只有分錄才有內容的欄留好寬度，所以「項次」表頭帶欄寬用的 class。
        Assert.Contains("{ key: null, label: '項次', className: 'voucher-table__line' }", vouchers, StringComparison.Ordinal);
        Assert.DoesNotContain("'傳票文件項次 '", vouchers, StringComparison.Ordinal);
        Assert.DoesNotContain("'傳票項次'", vouchers, StringComparison.Ordinal);
        Assert.DoesNotContain("'項次 '", vouchers, StringComparison.Ordinal);
    }

    [Fact]
    public void CommonSourceTab_UsesThePlainCommonTitle()
    {
        // 第五步工作區把卡片區塊的標頭整個藏起來，審計員實際看到的是左側分頁鈕。
        // 分頁鈕寫「常用篩選條件」；畫面與程式都不出現小組名稱，因為 JET 給所有審計員使用。
        var filter = ProductSources.FilterStepFrontend();

        Assert.Contains("sourceButton('common', '常用篩選條件')", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("從範例開始", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("KCT", filter, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("KCT", ReadFrontend("js", "ui-core.js"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CriteriaReportAndHighRiskSummary_UsePlainActionNames()
    {
        // 按鈕與操作名稱不重複「完成」；矩陣分頁稱為「符合條件摘要」。
        var filter = ProductSources.FilterStepFrontend();

        Assert.Contains("(regenerate ? '重新產生條件篩選報告' : '產生條件篩選報告')", filter, StringComparison.Ordinal);
        Assert.Contains("Ui.run('產生條件篩選報告', function () {", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("完成條件篩選並產生報告", filter, StringComparison.Ordinal);
        Assert.Contains("highRiskSummaryViewButton('scenarios', '符合條件摘要')", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("命中摘要", filter, StringComparison.Ordinal);

        // 空狀態說明每個情境在高風險條件彙總裡佔一欄，不出現 C1..CN 這類內部記號，也不寫「矩陣」。
        Assert.Contains("尚未儲存任何篩選情境；先在上方儲存情境，每個情境在彙總裡佔一欄。", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("矩陣", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("C1..CN", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_UsesCompleteSentencesAndChineseNames()
    {
        var app = ReadFrontend("js", "app.js");

        // 無法執行的規則的說明寫成完整句子。
        // 畫面用字是「無法執行的規則」，和第四步同名。
        Assert.Contains("列在「無法執行的規則」的條件因缺少所需資料或設定而沒有執行，不代表沒有符合的分錄。", ExtractFunction(app, "overviewDisclaimerHtml"), StringComparison.Ordinal);
        Assert.DoesNotContain("不適用不等於零", app, StringComparison.Ordinal);

        // 總覽與重複匯出的提示用「工作底稿」。
        Assert.Contains("overviewFactHtml('export.working-paper', '工作底稿',", app, StringComparison.Ordinal);
        Assert.Contains("['產生目前版本的工作底稿']", app, StringComparison.Ordinal);
        Assert.DoesNotContain("'Working Paper'", app, StringComparison.Ordinal);
        Assert.DoesNotContain("的 Working Paper'", app, StringComparison.Ordinal);
        var store = ReadSource("Infrastructure", "FileIO", "ProjectReportStore.cs");
        Assert.Contains("\"同名工作底稿已存在，原檔已保留。", store, StringComparison.Ordinal);
        Assert.DoesNotContain("\"同名 Working Paper 已存在", store, StringComparison.Ordinal);

        // The wording uses confirmation status, not submission terminology.
        Assert.DoesNotContain("'尚未提交'", app, StringComparison.Ordinal);
        // 沒有 TB 時的原因句；用字是「尚未確認」，不是「還沒確認」。
        Assert.Contains("\"TB 欄位配對尚未確認，無法執行 GL 與 TB 逐科目比對。\"", ReadSource("AuditCore", "AccountComparisonProcedure.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void PersonValues_AreNamedByPurpose()
    {
        // 依建立人員彙總的表頭用「編製人員」；計數與說明用「人員代號或姓名」。
        var validate = ReadFrontend("js", "steps", "validate-step.js");
        // 表頭用欄位名「傳票建立人員」。
        Assert.Contains("columns: ['傳票建立人員', '分錄筆數', '借方', '貸方', '人工分錄筆數']", validate, StringComparison.Ordinal);

        Assert.Contains("isPerson ? '每行一個人員代號或姓名'", ReadFrontend("js", "filter-values.js"), StringComparison.Ordinal);
        var import = ReadFrontend("js", "steps", "import-step.js");
        // 人數經 Ui.countText 加千分位。
        // 一律寫「授權編製人員清單」。
        Assert.Contains("授權編製人員清單：' + Ui.countText(info.rowCount) + ' 個人員代號或姓名' +", import, StringComparison.Ordinal);
        Assert.Contains("' 個有效人員代號或姓名。'", import, StringComparison.Ordinal);
        Assert.Contains("preparerName: '人員代號或姓名',", ReadFrontend("js", "data-preview.js"), StringComparison.Ordinal);
        Assert.Contains("依匯入後的科目或人員分組；空白欄位不列入。", ProductSources.FilterStepFrontend(), StringComparison.Ordinal);

        foreach (var source in new[]
        {
            validate,
            import,
            ReadFrontend("js", "filter-values.js"),
            ReadFrontend("js", "data-preview.js"),
            ProductSources.FilterStepFrontend()
        })
        {
            Assert.DoesNotContain("人員識別值", source, StringComparison.Ordinal);
            Assert.DoesNotContain("人員代號或姓名或", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AccountMappingAndBlankRecords_UseReviewedWording()
    {
        var validate = ReadFrontend("js", "steps", "validate-step.js");

        // 科目配對狀態與「儲存」按鈕一致。
        // 科目數經 Ui.countText 加千分位。
        // 「用 Excel 配對」下方的結果清單一列寫「已儲存的科目配對」與科目數，沒匯入時寫「尚未匯入」。
        Assert.Contains("<span class=\"excel-mapping__label\">已儲存的科目配對</span>", validate, StringComparison.Ordinal);
        Assert.Contains("Ui.countText(info.rowCount) + ' 個科目</span>'", validate, StringComparison.Ordinal);
        Assert.Contains("excel-mapping__value--muted\">尚未匯入</span>", validate, StringComparison.Ordinal);
        Assert.DoesNotContain("目前已保存 ' + info.rowCount", validate, StringComparison.Ordinal);
        Assert.DoesNotContain("尚未保存科目配對", validate, StringComparison.Ordinal);
        var preview = ReadFrontend("js", "data-preview.js");
        Assert.Contains("case 'accountMapping': return '尚未儲存科目配對；", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("尚未保存科目配對", preview, StringComparison.Ordinal);

        // 空值計數的名稱直接說明可能重複計入。
        // 完成訊息不列各項筆數。
        Assert.Contains("return data ? '資料驗證完成' : '';", validate, StringComparison.Ordinal);
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
