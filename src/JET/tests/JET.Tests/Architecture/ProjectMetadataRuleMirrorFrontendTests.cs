using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 建案與修改案件畫面輸入時就近顯示的準備日提醒，是後端 <see cref="ProjectMetadataRules"/> 的副本；這裡比對句子。
/// 判斷邊界兩邊各有固定答案：後端在 <c>Batch4ProjectCreationTests</c>，前端在 <c>tools/tests/frontend-workflow.test.cjs</c>。
/// 低嚴重度事項 F7（使用者 2026-10-19 要求處理）。
/// </summary>
public sealed class ProjectMetadataRuleMirrorFrontendTests
{
    [Fact]
    public void PreparationDateWarning_FrontendCopyMatchesBackendWordForWord()
    {
        var create = ReadFrontend("js", "steps", "create-step.js");

        Assert.Contains(
            "warning.textContent = '" + ProjectMetadataRules.PreparationDateWarning + "';",
            create,
            StringComparison.Ordinal);
    }

    private static string ReadFrontend(params string[] segments)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot(), "JET", "wwwroot" }.Concat(segments).ToArray()));

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
