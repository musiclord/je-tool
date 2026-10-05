namespace JET.Domain;

internal sealed record DataPreviewDatasetLabel(string Dataset, string Label);

/// <summary>右側常駐資料預覽五個直接頁籤的顯示文字權威；wire dataset key 不變。</summary>
internal static class DataPreviewDatasetLabels
{
    internal static readonly IReadOnlyList<DataPreviewDatasetLabel> MainTabs =
    [
        new("glStaging", "GL 原始資料"),
        new("glEntries", "納入測試的分錄"),
        new("tbStaging", "TB 原始資料"),
        new("tbBalances", "已確認配對的試算表"),
        new("accountMappings", "科目配對")
    ];
}
