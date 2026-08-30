using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>報告清理 UI 只回放後端預覽，並維持兩次明示動作與 single-flight 邊界。</summary>
public sealed class ReportArtifactCleanupFrontendTests
{
    [Fact]
    public void CleanupActions_AreExposedAndOnlyConfirmIsCancellable()
    {
        var api = ReadFrontend("js", "jet-api.js");
        var app = ReadFrontend("js", "app.js");

        Assert.Contains("'report.cleanupPreview'", api, StringComparison.Ordinal);
        Assert.Contains("'report.cleanupConfirm'", api, StringComparison.Ordinal);

        var cancellable = Regex.Match(
            app,
            @"CANCELLABLE_ACTIONS\s*=\s*\{(?<body>[^}]*)\}",
            RegexOptions.Singleline);
        Assert.True(cancellable.Success, "app.js 內找不到 CANCELLABLE_ACTIONS。");
        Assert.Contains("'report.cleanupConfirm': true", cancellable.Groups["body"].Value, StringComparison.Ordinal);
        Assert.DoesNotContain("'report.cleanupPreview'", cancellable.Groups["body"].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanupPanel_IsInlineAndRendersBackendCandidateMetadataWithoutRetentionLogic()
    {
        var export = ReadFrontend("js", "steps", "export-step.js");

        Assert.Contains("data-action=\"preview-report-cleanup\"", export, StringComparison.Ordinal);
        Assert.Contains("data-action=\"confirm-report-cleanup\"", export, StringComparison.Ordinal);
        Assert.Contains("cleanupPreview.candidateCount", export, StringComparison.Ordinal);
        Assert.Contains("cleanupPreview.candidateBytes", export, StringComparison.Ordinal);
        Assert.Contains("candidate.artifactId", export, StringComparison.Ordinal);
        Assert.Contains("candidate.kind", export, StringComparison.Ordinal);
        Assert.Contains("candidate.fileName", export, StringComparison.Ordinal);
        Assert.Contains("candidate.generatedUtc", export, StringComparison.Ordinal);
        Assert.Contains("candidate.bytes", export, StringComparison.Ordinal);
        Assert.Contains("candidate.reason", export, StringComparison.Ordinal);
        Assert.Contains("global.JetApi.reportCleanupPreview({})", export, StringComparison.Ordinal);

        var payload = Regex.Match(
            export,
            @"reportCleanupConfirm\s*\(\s*\{(?<body>[^}]*)\}\s*\)",
            RegexOptions.Singleline);
        Assert.True(payload.Success, "找不到 report.cleanupConfirm payload。");
        var fields = Regex.Matches(payload.Groups["body"].Value, @"(?<name>[A-Za-z][A-Za-z0-9]*)\s*:")
            .Select(match => match.Groups["name"].Value)
            .ToArray();
        Assert.Equal(new[] { "catalogRevision" }, fields);

        Assert.DoesNotMatch(new Regex(@"\b(?:window\.)?(?:confirm|alert)\s*\("), export);
        Assert.DoesNotContain("cleanup-modal", export, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(
            new Regex(@"(?:cleanupPreview\.candidates|\bcandidates)\s*\.\s*(?:filter|sort|reduce)\s*\("),
            export);
    }

    [Fact]
    public void Confirm_ReplacesStoreThenDeclaresSettledRefresh()
    {
        var export = ReadFrontend("js", "steps", "export-step.js");

        Assert.Matches(
            new Regex(
                @"reportCleanupConfirm\(\{\s*catalogRevision:\s*catalogRevision\s*\}\)" +
                @"(?s:.*?)\.then\(function \(data\) \{\s*Store\.setReportArtifacts\(data\.reportArtifacts \|\| \[\]\);"),
            export);
        Assert.Contains("error.code === 'operation_cancelled'", export, StringComparison.Ordinal);
        Assert.Contains("error.code === 'artifact_catalog_changed'", export, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(
                @"return Ui\.run\('刪除舊報告'(?s:.*?)\}\s*,\s*\{\s*refresh:\s*function \(\) \{\s*" +
                @"return requestCleanupPreview\(true\);\s*\},\s*refreshWhen:\s*'settled'\s*\}\);"),
            export);
        Assert.DoesNotMatch(
            new Regex(@"Ui\.run\('刪除舊報告'(?s:.*?)\}\)\.then\(function \(\)"),
            export);
        Assert.Matches(
            new Regex(
                @"Ui\.registerWorkflowReset\(function \(\) \{(?s:.*?)cleanupPreview = null;(?s:.*?)cleanupFeedback = null;"),
            export);
    }

    [Fact]
    public void ConfirmSuccess_WhenFollowupPreviewFails_PreservesCommittedCleanupFact()
    {
        var export = ReadFrontend("js", "steps", "export-step.js");

        Assert.Contains(
            "var retainedFeedback = preserveFeedback ? cleanupFeedback : null;",
            export,
            StringComparison.Ordinal);
        Assert.Contains(
            "text: retainedFeedback.text + ' 最新候選重新檢查失敗",
            export,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "無法取得最新清理預覽；尚未刪除任何報告",
            export,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CleanupStyles_AreFlatHairlineAndUseMutedSemanticColors()
    {
        var css = ReadFrontend("css", "app.css");
        var start = css.IndexOf("/* Step 5 報告清理", StringComparison.Ordinal);
        var end = css.IndexOf("/* ---- Buttons:", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "找不到報告清理 CSS 區段。");
        var cleanupCss = css[start..end];

        Assert.Contains("background: var(--pale-yellow);", cleanupCss, StringComparison.Ordinal);
        Assert.Contains("background: var(--pale-green);", cleanupCss, StringComparison.Ordinal);
        Assert.Contains("background: var(--pale-red);", cleanupCss, StringComparison.Ordinal);
        Assert.Contains("border: 1px solid", cleanupCss, StringComparison.Ordinal);
        Assert.DoesNotContain("box-shadow", cleanupCss, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gradient", cleanupCss, StringComparison.OrdinalIgnoreCase);

        var radii = Regex.Matches(cleanupCss, @"border-radius:\s*(?<radius>\d+)px")
            .Select(match => int.Parse(match.Groups["radius"].Value))
            .ToArray();
        Assert.NotEmpty(radii);
        Assert.All(radii, radius => Assert.InRange(radius, 0, 8));
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
