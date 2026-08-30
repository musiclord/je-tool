using Xunit;

namespace JET.Tests.Architecture;

public sealed class MappingMetadataMirrorTests
{
    [Fact]
    public void RestoreUi_AppliesOneBackendResponseAndNeverAutoCommits()
    {
        var source = ReadFrontend("steps", "mapping-step.js");
        var start = source.IndexOf("function bindRestoreMappingDraft", StringComparison.Ordinal);
        var end = source.IndexOf("/* ---- 狀態判定", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "找不到 mapping restore 綁定函式邊界。");

        var body = source[start..end];
        Assert.Contains("mappingRestoreDraft({ filePath: file.filePath })", body, StringComparison.Ordinal);
        Assert.Contains("Store.restoreMappingDrafts(data)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("mappingCommitGl", body, StringComparison.Ordinal);
        Assert.DoesNotContain("mappingCommitTb", body, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreState_AtomicallyReplacesBothDraftsAndPreservesCommittedSnapshots()
    {
        var source = ReadFrontend("state.js");
        var start = source.IndexOf("restoreMappingDrafts: function", StringComparison.Ordinal);
        var end = source.IndexOf("setMappingMode: function", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "找不到 restoreMappingDrafts state 邊界。");

        var body = source[start..end];
        Assert.Contains("state.mapping.gl.draft =", body, StringComparison.Ordinal);
        Assert.Contains("state.mapping.gl.amountMode =", body, StringComparison.Ordinal);
        Assert.Contains("state.mapping.tb.draft =", body, StringComparison.Ordinal);
        Assert.Contains("state.mapping.tb.changeMode =", body, StringComparison.Ordinal);
        Assert.DoesNotContain(".committed =", body, StringComparison.Ordinal);
        Assert.Equal(1, Count(body, "bump();"));
    }

    private static int Count(string source, string value)
        => source.Split(value, StringSplitOptions.None).Length - 1;

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
