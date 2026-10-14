using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 第三步「還原上次配對」畫面在 2026-09-22 撤下，直到報告還原流程談定前都不得回到畫面。
/// </summary>
public sealed class MappingMetadataMirrorTests
{
    [Fact]
    public void RestoreUi_IsWithdrawnUntilTheReportRestoreWorkflowIsAgreed()
    {
        var source = ReadFrontend("steps", "mapping-step.js");
        // 2026-09-22 明示撤下功能；既有 restore state 由 tools/tests/frontend-journey-transport.test.cjs 的
        // restoreMappingDrafts 行為測試保留（2026-10-14 取代原本的字串比對方法），後端相容性由 Application 測試保留。
        Assert.DoesNotContain("restore-mapping-draft", source, StringComparison.Ordinal);
        Assert.DoesNotContain("bindRestoreMappingDraft", source, StringComparison.Ordinal);
        Assert.DoesNotContain("mappingRestoreDraft", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Store.restoreMappingDrafts", source, StringComparison.Ordinal);
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
