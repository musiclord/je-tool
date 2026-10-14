namespace JET.Domain;

internal sealed record DataPreviewDatasetLabel(string Dataset, string Label);

/// <summary>
/// 資料預覽各資料集的顯示文字權威。使用者 2026-10-08 裁定資料名稱全系統統一：
/// PBC-GL、GL、PBC-TB、TB、GL(未納入測試)；wire dataset key 同步改為 pbcGl、gl、pbcTb、tb、glExcluded。
/// 前端 data-preview.js 的 DATASETS 與 MAIN_TABS 逐字鏡射本檔，由 DataPreviewFrontendTests 把關。
/// </summary>
internal static class DataPreviewDatasetLabels
{
    internal const string PbcGl = "PBC-GL";
    internal const string Gl = "GL";
    internal const string PbcTb = "PBC-TB";
    internal const string Tb = "TB";
    internal const string GlExcluded = "GL(未納入測試)";

    /// <summary>右側常駐資料預覽五個直接頁籤。</summary>
    internal static readonly IReadOnlyList<DataPreviewDatasetLabel> MainTabs =
    [
        new("pbcGl", PbcGl),
        new("gl", Gl),
        new("pbcTb", PbcTb),
        new("tb", Tb),
        new("accountMappings", "科目配對")
    ];

    /// <summary>全部可預覽資料集，順序與「其他資料」選單一致。</summary>
    internal static readonly IReadOnlyList<DataPreviewDatasetLabel> All =
    [
        new("pbcGl", PbcGl),
        new("pbcTb", PbcTb),
        new("gl", Gl),
        new("glExcluded", GlExcluded),
        new("tb", Tb),
        new("accountMappings", "科目配對"),
        new("authorizedPreparers", "授權編製人員清單"),
        new("dateDimension", "行事曆（假日與補班日）"),
        new("schemaOverview", "資料表摘要")
    ];
}
