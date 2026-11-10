using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 第三步「還原上次配對」畫面在 2026-09-22 撤下。2026-11-05 使用者決定恢復入口（裁定 2026-11-05-03），
/// 2026-11-09 改名為「由底稿匯入欄位」（裁定 2026-11-09-27）：選檔案後填成草稿，審計員按確認配對才儲存。
/// </summary>
public sealed class MappingMetadataMirrorTests
{
    // 原本的 RestoreUi_IsWithdrawnUntilTheReportRestoreWorkflowIsAgreed 斷言畫面不呼叫 mappingRestoreDraft；
    // 裁定 2026-11-05-03 恢復入口後改成核對新入口在、舊入口沒有回來。
    // 第一次失敗：Focused 收據 20261105-022240747-fdb2032fda9d4353a0d7222b15349128。
    // 入口的行為由 tools/tests/frontend-journey-mapping.test.cjs 的「bringing a mapping from a report」核對。
    [Fact]
    public void RestoreUi_IsTheReportEntryOnTheMappingStep_AndTheWithdrawnEntryStaysAway()
    {
        var source = ReadFrontend("steps", "mapping-step.js");
        Assert.Contains("data-action=\"restore-mapping-from-report\"", source, StringComparison.Ordinal);
        Assert.Contains("global.JetApi.mappingRestoreDraft(", source, StringComparison.Ordinal);
        Assert.Contains("Store.restoreMappingDrafts(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("restore-mapping-draft", source, StringComparison.Ordinal);
        Assert.DoesNotContain("bindRestoreMappingDraft", source, StringComparison.Ordinal);
    }

    private static string ReadFrontend(params string[] relativeSegments)
    {
        var segments = new[] { RepoRoot(), "JET", "wwwroot", "js" }
            .Concat(relativeSegments)
            .ToArray();
        return File.ReadAllText(Path.Combine(segments));
    }

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
