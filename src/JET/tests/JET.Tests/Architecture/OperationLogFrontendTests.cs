using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

// 預篩選收斂計畫階段十（2026-10-25）：filter-step.js 依職責拆成五個檔案。讀第五步前端的斷言改用 ProductSources.FilterStepFrontend()，
// 把五個檔案合起來讀，範圍和拆檔前讀單一檔案相同；斷言本身不變。第一次失敗：Focused 收據 20261025-053438454-333c59b548b548008f977941c5502503。

/// <summary>
/// 長作業的完成訊息守衛：會寫完成句的工作流程長作業，都要明示選擇加入一則完成摘要。
/// 完成句與失敗句的寫法、耗時、最後進度、複製最近 100 則與取消時記下的進度，
/// 2026-10-14 起改由 <c>tools/tests/frontend-journey-operations.test.cjs</c> 核對。
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
            "上傳假日檔",
            "上傳補班檔",
            // 使用者 2026-10-13 勾選頁確認 L29、L27（第一次失敗：Focused 收據 20261013-141351990-49476cfa2fd741ed9b2b96ce5334e3f0）。
            "匯入科目配對檔",
            "重建科目配對檔",
            "產生驗證階段報告",
            "產生預篩選報告",
            "執行驗證並產生報告",
            "執行預篩選",
            "預覽篩選情境",
            // 2026-10-03 用語統一 W10：使用者裁定以「已儲存」為準，操作名稱改成「儲存」（第一次失敗：收據 20261003-023349721-0ccefea0a80c412aa8460624eaae563a）。
            "儲存篩選情境",
            // 2026-10-02 整體複審 W13：按鈕與操作名稱改成「產生條件篩選報告」，不再重複「完成」。
            "產生條件篩選報告",
            "產生工作底稿",
            "產生工作底稿與預篩選報告"
        })
        {
            Assert.Contains($"Ui.run('{label}'", combined, StringComparison.Ordinal);
        }

        // 使用者 2026-10-13 裁定成功時只留一則：有自己完成句的操作改用 completionMessage，由 Ui.run 接上耗時記一則；
        // 沒有選檔這類 resolved no-op 由 completionMessage 回傳空字串排除。第一次失敗收據 20261013-095529696-bc66ff9ab56e4bd29575a25a71138c23。
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
