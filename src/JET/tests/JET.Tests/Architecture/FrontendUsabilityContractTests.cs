using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 第三步配對版面、單一資料夾入口、不顯示多餘的成功提示，以及總覽把流程位置和階段狀態分開的呈現層守衛。
/// 審計事實仍只由既有 backend DTO 提供；本組只鎖 DOM、互動與可讀性。
/// 資料預覽的半屏收合與「其他資料」選單、摘要優先的總覽與階段檢視、總覽的完成判斷，2026-10-14 起改由
/// <c>tools/tests/frontend-journey-tables.test.cjs</c>、<c>frontend-journey-overview.test.cjs</c> 與 <c>frontend-journey-completion.test.cjs</c>
/// 的行為測試核對；收合斷點和 app.css 一致這一條搬到 <see cref="FrontendSourceOnlyRulesTests"/>。
/// </summary>
public sealed class FrontendUsabilityContractTests
{
    [Fact]
    public void Mapping_UsesContainerWidthKeepsLabelsWholeAndOwnsHorizontalOverflow()
    {
        var css = ReadFrontend("css", "app.css");

        Assert.Matches(new Regex(@"\.map-layout\s*\{[^}]*display:\s*flex;[^}]*flex-wrap:\s*wrap;", RegexOptions.Singleline), css);
        Assert.Matches(new Regex(@"\.mapping-table-wrap\s*\{[^}]*overflow-x:\s*auto;", RegexOptions.Singleline), css);
        Assert.Contains("word-break: keep-all", css, StringComparison.Ordinal);
        Assert.Contains("white-space: nowrap", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_HasHeaderAndMappingFolderEntriesAndSharedReportRevealControls()
    {
        var root = FrontendRoot();
        var index = File.ReadAllText(Path.Combine(root, "index.html"));
        var javascript = string.Join('\n', Directory.EnumerateFiles(Path.Combine(root, "js"), "*.js", SearchOption.AllDirectories).Select(File.ReadAllText));

        Assert.Single(Regex.Matches(index, "data-action=\"open-project-folder\"", RegexOptions.CultureInvariant).Cast<Match>());
        // 9/22 科目配對區新增就近開啟資料夾，報告定位由各步共用一個事件綁定。
        // 改動原因：使用者 2026-10-08 畫面註解要求移除第二步假日卡片的「開啟案件資料夾」，呼叫點由 4 處降為 3 處
        // （頁首、科目配對各一，加上報告定位）；第一次失敗：收據 20261008-035557135-54b626343f7c4071bb97b02ddde22408。
        Assert.Equal(3, Regex.Matches(javascript, @"JetApi\.hostOpenFolder\(", RegexOptions.CultureInvariant).Count);
        Assert.Contains("hostOpenFolder({ target: 'projectFolder' })", javascript, StringComparison.Ordinal);
        Assert.Contains("data-open-artifact", javascript, StringComparison.Ordinal);
        Assert.Contains("hostOpenFolder({ artifactId: artifactId })", javascript, StringComparison.Ordinal);
        Assert.DoesNotContain("hostOpenFolder({ path:", javascript, StringComparison.Ordinal);
        Assert.DoesNotContain("artifactId: artifact.artifactId", javascript, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_RemovesRedundantSuccessPromptsButRetainsBlockersAndAuditInterpretation()
    {
        var javascript = string.Join('\n', Directory.EnumerateFiles(Path.Combine(FrontendRoot(), "js"), "*.js", SearchOption.AllDirectories).Select(File.ReadAllText));

        Assert.DoesNotContain("現在可以開始", javascript, StringComparison.Ordinal);
        Assert.DoesNotContain("進入條件已備齊", javascript, StringComparison.Ordinal);
        Assert.DoesNotContain("本步驟條件已備齊", javascript, StringComparison.Ordinal);
        // 使用者 2026-10-13 勾選頁確認 18、36：「已阻擋」改「尚無法執行」（第一次失敗：Focused 收據 20261013-141351990-49476cfa2fd741ed9b2b96ce5334e3f0）。
        Assert.Contains("尚無法執行：", javascript, StringComparison.Ordinal);
        // 「符合條件不等於錯誤」與「判斷由審計員做」在 2026-09-22 改成白話後合併為同一句，整句檢查。
        // 2026-10-03 用語統一 T1：畫面不再使用「母體」（第一次失敗：收據 20261003-023349721-0ccefea0a80c412aa8460624eaae563a）。
        Assert.Contains("預篩選呈現查核期間分錄的分布與符合條件的分錄，是否需進一步查核由審計員判斷。", javascript, StringComparison.Ordinal);
        Assert.DoesNotContain("母體分布", javascript, StringComparison.Ordinal);
        // 2026-10-02 整體複審 W16：「不適用不等於零。」改成完整句，語意仍是「不適用不代表沒有符合的分錄」；舊短句不得再出現。
        Assert.Contains("列在「不適用規則」的條件因缺少所需資料或設定而沒有執行，不代表沒有符合的分錄。", javascript, StringComparison.Ordinal);
        Assert.DoesNotContain("不適用不等於零", javascript, StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_SeparatesWorkflowLocationFromLifecycleStatusAndNeverReliesOnColorAlone()
    {
        var app = ReadFrontend("js", "app.js");
        var css = ReadFrontend("css", "app.css");
        var progress = ExtractFunction(app, "overviewProgressHtml");

        Assert.DoesNotContain("current: '● 進行中'", app, StringComparison.Ordinal);
        Assert.Contains("available: '可處理'", app, StringComparison.Ordinal);
        Assert.Contains("locked: '等待前置步驟'", app, StringComparison.Ordinal);
        Assert.DoesNotContain("if (index === state.currentStepIndex) { return 'current'; }", app, StringComparison.Ordinal);
        Assert.Contains("status === 'available'", progress, StringComparison.Ordinal);
        Assert.Contains("status === 'locked'", progress, StringComparison.Ordinal);
        Assert.Contains("個可處理", progress, StringComparison.Ordinal);
        Assert.Contains("個等待前置", progress, StringComparison.Ordinal);
        Assert.DoesNotContain("個進行中", progress, StringComparison.Ordinal);
        // 2026-10-08 新主題把藍色選取色改名為唯一重點色 --color-accent（值仍是 #1f6c9f），紅色只留給錯誤。
        Assert.Matches(
            new Regex(@"\.overview-stage__location\s*\{[^}]*color:\s*var\(--color-accent\);", RegexOptions.Singleline),
            css);
        Assert.Matches(
            new Regex(@"\.overview__progress-fill\s*\{[^}]*background:\s*var\(--color-accent\);", RegexOptions.Singleline),
            css);
        Assert.DoesNotMatch(
            new Regex(@"\.overview__progress-fill\s*\{[^}]*background:\s*#d64422;", RegexOptions.Singleline),
            css);
    }

    private static string ExtractFunction(string source, string name)
    {
        var start = source.IndexOf($"function {name}(", StringComparison.Ordinal);
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
        return directory?.FullName ?? throw new InvalidOperationException("向上尋不到 JET.slnx，無法定位 repo 根。");
    }
}
