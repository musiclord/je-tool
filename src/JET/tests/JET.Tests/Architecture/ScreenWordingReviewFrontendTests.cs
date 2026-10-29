using Xunit;

namespace JET.Tests.Architecture;

// 預篩選收斂計畫階段十（2026-10-25）：filter-step.js 依職責拆成五個檔案。讀第五步前端的斷言改用 ProductSources.FilterStepFrontend()，
// 把五個檔案合起來讀，範圍和拆檔前讀單一檔案相同；斷言本身不變。第一次失敗：Focused 收據 20261025-053438454-333c59b548b548008f977941c5502503。

/// <summary>
/// 2026-10-02 整體複審的畫面文字修正：只改顯示文字與格式，不改 action、識別字或底稿內容。
/// 每項都鎖住新字樣，並確認舊字樣不再出現在同一個位置。
/// </summary>
public sealed class ScreenWordingReviewFrontendTests
{
    [Fact]
    public void FieldMappingAndVoucherDetail_UseShortenedNames()
    {
        // W5：使用者 2026-10-02 裁定「借方代碼」與「傳票文件項次」。
        var core = ReadFrontend("js", "ui-core.js");
        Assert.Contains("{ key: 'dcDebitCode', label: '借方代碼',", core, StringComparison.Ordinal);
        Assert.DoesNotContain("借方標識代碼", core, StringComparison.Ordinal);
        Assert.DoesNotContain("借方標識代碼", ReadSource("Domain", "Rules", "JetFieldCatalog.cs"), StringComparison.Ordinal);

        var vouchers = ReadFrontend("js", "filter-vouchers.js");
        Assert.Contains("['狀態', '傳票文件項次', '總帳日期'", vouchers, StringComparison.Ordinal);
        Assert.DoesNotContain("'傳票項次'", vouchers, StringComparison.Ordinal);
    }

    [Fact]
    public void CommonSourceTab_UsesThePlainCommonTitle()
    {
        // 2026-10-03 主線在瀏覽器主機重測 S9 時發現：第五步工作區把卡片區塊的標頭整個藏起來，審計員實際看到的是左側分頁鈕。
        // 使用者 2026-10-08 裁定分頁鈕只寫小組名稱加「條件」；2026-10-29 改裁定：這個分頁和「從範例開始」合併成「常用篩選條件」，
        // 畫面與程式都不再出現小組名稱，因為 JET 給所有審計員使用（原話見 docs/history/specs/2026-10-22-prescreen-tech-debt-deferred-items-plan.md）。
        // 原本斷言舊的分頁鍵與名稱，第一次失敗：Focused 收據 20261029-062123410-6be7ae8a47f140af98c112c94ca35612。
        var filter = ProductSources.FilterStepFrontend();

        Assert.Contains("sourceButton('common', '常用篩選條件')", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("從範例開始", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("KCT", filter, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("KCT", ReadFrontend("js", "ui-core.js"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CriteriaReportAndMatrix_UsePlainActionNames()
    {
        // W13、W21：按鈕與操作名稱不再重複「完成」；矩陣分頁改稱「符合條件摘要」。
        var filter = ProductSources.FilterStepFrontend();

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
        // 使用者 2026-10-13 勾選頁確認 35：完成訊息不再列各項筆數（第一次失敗：Focused 收據 20261013-141351990-49476cfa2fd741ed9b2b96ce5334e3f0）。
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
