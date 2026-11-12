using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 長作業的完成訊息守衛：會寫完成句的工作流程長作業，都要明示選擇加入一則完成摘要。
/// 完成句與失敗句的寫法、耗時、最後進度、複製最近 100 則與取消時記下的進度，
/// 由前端測試核對。
/// </summary>
public sealed class OperationLogFrontendTests
{
    [Fact]
    public void WorkflowLongOperations_ExplicitlyOptIntoOneCompletionSummary()
    {
        var validation = ReadFrontend("js", "steps", "validate-step.js");
        var import = ReadFrontend("js", "steps", "import-step.js");
        var filter = ProductSources.FilterStepFrontend();
        var export = ReadFrontend("js", "steps", "export-step.js");
        var combined = validation + import + filter + export;

        foreach (var label in new[]
        {
            "匯入授權編製人員清單",
            // 兩份清單寫全名。
            "上傳國定假日清單",
            "上傳補班日清單",
            // 這兩個操作名稱已定案。
            "匯入科目配對範本",
            "重建科目配對範本",
            "產生驗證階段報告",
            "產生預篩選報告",
            "執行驗證並產生報告",
            "執行預篩選",
            "預覽篩選情境",
            // 用語以「已儲存」為準，操作名稱寫「儲存」。
            "儲存篩選情境",
            // 按鈕與操作名稱是「產生條件篩選報告」，不重複「完成」。
            "產生條件篩選報告",
            "產生工作底稿",
            "產生工作底稿與預篩選報告"
        })
        {
            Assert.Contains($"Ui.run('{label}'", combined, StringComparison.Ordinal);
        }

        // 成功時只留一則：有自己完成句的操作用 completionMessage，由 Ui.run 接上耗時記一則；
        // 沒有選檔這類 resolved no-op 由 completionMessage 回傳空字串排除。
        Assert.True(
            Regex.Matches(combined, @"logCompletion\s*:\s*true|completionMessage\s*:").Count >= 13,
            "關鍵長操作必須明示 opt-in；不得讓所有 Ui.run 無差別增加成功訊息。");
        Assert.True(
            Regex.Matches(combined, @"logCompletionWhen\s*:|completionMessage\s*:\s*function\s*\(data\)\s*\{\s*return data \?").Count >= 4,
            "含檔案選擇器的操作必須排除 resolved no-op，不能把取消選檔記成成功。");
    }

    private static string ReadFrontend(params string[] segments)
        => File.ReadAllText(
            Path.Combine(new[] { RepoRoot(), "JET", "wwwroot" }.Concat(segments).ToArray()));

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
