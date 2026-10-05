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
        Assert.Contains("{ value: 'glStaging', label: 'GL 原始資料' }", body, StringComparison.Ordinal);
        Assert.Contains("{ value: 'glEntries', label: '納入測試的分錄' }", body, StringComparison.Ordinal);
        Assert.Contains("{ value: 'tbStaging', label: 'TB 原始資料' }", body, StringComparison.Ordinal);
        // Q8 unifies the confirmed label; retain both the fixed label and the full Domain mirror below.
        // First fixed-label failure: 20261004-092023464-13b0a6928d5e400492fbe8a6a24eb69d.
        Assert.Contains("{ value: 'tbBalances', label: '已確認配對的試算表' }", body, StringComparison.Ordinal);
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
    public void AccountMappingPreviewButton_OpensDirectAccountMappingDataset()
    {
        var validate = ReadFrontend("js", "steps", "validate-step.js");

        Assert.Contains("data-action=\"preview-account-mapping\"", validate, StringComparison.Ordinal);
        Assert.Contains("Ui.openDataPreview('accountMappings')", validate, StringComparison.Ordinal);
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
