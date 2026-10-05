using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.dataPreview：正式版的使用者資料預覽。
/// 有界唯讀——欄位配對時對照欄名與實際內容、進階篩選前掌握數值/日期/摘要的大概樣貌；
/// 明細分頁由 query.*Page 負責，本 action 絕不回完整母體。
/// </summary>
public sealed class QueryDataPreviewHandler(
    IProjectStore projectStore,
    ProjectSession session) : IApplicationActionHandler
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 100;

    public string Action => "query.dataPreview";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();

        var datasetName = PayloadReader.GetRequiredString(payload, "dataset");
        if (!DataPreviewDatasetNames.TryParse(datasetName, out var dataset))
        {
            throw new JetActionException(
                JetErrorCodes.InvalidPayload,
                $"dataset '{datasetName}' 無效，允許值：glStaging、tbStaging、glEntries、glExcludedEntries、tbBalances、accountMappings、authorizedPreparers、dateDimension、schemaOverview。");
        }

        var limit = Math.Clamp(PayloadReader.GetOptionalInt(payload, "limit") ?? DefaultLimit, 1, MaxLimit);

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                $"找不到專案 '{projectId}'。");

        var preview = await Task.Run(
            () => repositories.DataPreview.GetPreviewAsync(projectId, dataset, document.MoneyScale, limit, cancellationToken),
            cancellationToken);

        return new
        {
            dataset = datasetName,
            columns = preview.Columns,
            rows = preview.Rows,
            totalCount = preview.TotalCount,
            stats = ToWireStats(preview.Stats)
        };
    }

    private static object? ToWireStats(DataPreviewStats? stats) => stats switch
    {
        null => null,
        GlEntriesPreviewStats glEntries => new
        {
            amountAbsMin = glEntries.AmountAbsMin,
            amountAbsMax = glEntries.AmountAbsMax,
            postDateMin = glEntries.PostDateMin,
            postDateMax = glEntries.PostDateMax,
            voucherCount = glEntries.VoucherCount
        },
        GlExcludedEntriesPreviewStats excluded => new
        {
            excludedByPeriodCount = excluded.ExcludedByPeriodCount,
            excludedByPostingStatusCount = excluded.ExcludedByPostingStatusCount
        },
        _ => throw new InvalidOperationException(
            $"不支援的 data preview stats 型別 '{stats.GetType().Name}'。")
    };
}
