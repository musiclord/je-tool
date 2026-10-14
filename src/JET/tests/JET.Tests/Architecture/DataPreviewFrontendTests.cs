using System.Text.RegularExpressions;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>常駐資料預覽的直接頁籤與步驟換位入口鏡射使用者可見契約。</summary>
public sealed class DataPreviewFrontendTests
{
    [Fact]
    public void MainTabs_ExposeSourceAndStandardizedPairsWithApprovedExactLabels()
    {
        var preview = ReadFrontend("js", "data-preview.js");
        var tabs = Regex.Match(
            preview,
            @"var MAIN_TABS\s*=\s*\[(?<body>[\s\S]*?)\];");

        Assert.True(tabs.Success, "找不到 data-preview.js 的 MAIN_TABS。");
        var body = tabs.Groups["body"].Value;
        // 使用者 2026-10-08 裁定資料名稱全系統統一：PBC-GL、GL、PBC-TB、TB；wire key 同步改名。
        Assert.Contains("{ value: 'pbcGl', label: 'PBC-GL' }", body, StringComparison.Ordinal);
        Assert.Contains("{ value: 'gl', label: 'GL' }", body, StringComparison.Ordinal);
        Assert.Contains("{ value: 'pbcTb', label: 'PBC-TB' }", body, StringComparison.Ordinal);
        // Q8 unifies the confirmed label; retain both the fixed label and the full Domain mirror below.
        // First fixed-label failure: 20261004-092023464-13b0a6928d5e400492fbe8a6a24eb69d.
        Assert.Contains("{ value: 'tb', label: 'TB' }", body, StringComparison.Ordinal);
        Assert.Contains("{ value: 'accountMappings', label: '科目配對' }", body, StringComparison.Ordinal);
        Assert.DoesNotContain("GL (PBC)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("TB (PBC)", body, StringComparison.Ordinal);

        var actual = Regex.Matches(
                body,
                @"\{\s*value:\s*'(?<dataset>[^']+)',\s*label:\s*'(?<label>[^']+)'\s*\}")
            .Select(match => new DataPreviewDatasetLabel(
                match.Groups["dataset"].Value,
                match.Groups["label"].Value))
            .ToArray();
        Assert.Equal(DataPreviewDatasetLabels.MainTabs, actual);
    }

    [Fact]
    public void Datasets_MirrorDomainLabelsIncludingGlExcluded()
    {
        // 使用者 2026-10-08 裁定資料名稱全系統統一；「其他資料」選單與載入狀態也逐字鏡射 Domain 權威。
        var preview = ReadFrontend("js", "data-preview.js");
        var datasets = Regex.Match(
            preview,
            @"var DATASETS\s*=\s*\[(?<body>[\s\S]*?)\];");

        Assert.True(datasets.Success, "找不到 data-preview.js 的 DATASETS。");
        var actual = Regex.Matches(
                datasets.Groups["body"].Value,
                @"\{\s*value:\s*'(?<dataset>[^']+)',\s*label:\s*'(?<label>[^']+)'\s*\}")
            .Select(match => new DataPreviewDatasetLabel(
                match.Groups["dataset"].Value,
                match.Groups["label"].Value))
            .ToArray();
        Assert.Equal(DataPreviewDatasetLabels.All, actual);
        Assert.Contains(new DataPreviewDatasetLabel("glExcluded", "GL(未納入測試)"), actual);
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
