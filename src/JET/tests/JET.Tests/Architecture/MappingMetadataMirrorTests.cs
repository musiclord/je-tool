using Xunit;

namespace JET.Tests.Architecture;

public sealed class MappingMetadataMirrorTests
{
    [Fact]
    public void RestoreUi_IsWithdrawnUntilTheReportRestoreWorkflowIsAgreed()
    {
        var source = ReadFrontend("steps", "mapping-step.js");
        // 2026-09-22 明示撤下功能；既有 restore state 與後端相容性由下方及 Application 測試保留。
        Assert.DoesNotContain("restore-mapping-draft", source, StringComparison.Ordinal);
        Assert.DoesNotContain("bindRestoreMappingDraft", source, StringComparison.Ordinal);
        Assert.DoesNotContain("mappingRestoreDraft", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Store.restoreMappingDrafts", source, StringComparison.Ordinal);
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
