using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 第三步「由底稿匯入欄位」入口：選檔案後填成草稿，審計員按確認配對才儲存。
/// </summary>
public sealed class MappingMetadataMirrorTests
{
    // 核對新入口在、撤下的舊入口沒有回來。
    // 入口的行為由前端測試的「bringing a mapping from a report」核對。
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
        var segments = new[] { SolutionRoot(), "JET", "wwwroot", "js" }
            .Concat(relativeSegments)
            .ToArray();
        return File.ReadAllText(Path.Combine(segments));
    }

    private static string SolutionRoot()
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
