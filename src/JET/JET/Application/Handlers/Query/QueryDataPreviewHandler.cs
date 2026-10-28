using System.Globalization;
using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.dataPreview：正式版的使用者資料預覽。
/// 有界唯讀——欄位配對時對照欄名與實際內容、進階篩選前掌握數值/日期/摘要的大概樣貌；
/// 以 limit 與 offset 每次回傳最多 200 筆，不一次讀取完整母體。
/// GL(未納入測試) 可以用 exclusionReason 只看一種未納入原因（使用者 2026-10-21 裁定）。
/// GL 與 GL(未納入測試) 依已確認的 GL 配對列欄位，回應另帶 columnDetails 寫出每欄的名稱與型別；
/// 金額一律是照存下的值換算的精確字串，畫面補成四位小數（使用者 2026-10-22 裁定，預篩選收斂計畫階段五）。
/// </summary>
public sealed class QueryDataPreviewHandler(
    IProjectStore projectStore,
    ProjectSession session) : IApplicationActionHandler
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 200;

    public string Action => "query.dataPreview";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var (projectId, repositories) = session.RequireActive();

        var datasetName = PayloadReader.GetRequiredString(payload, "dataset");
        if (!DataPreviewDatasetNames.TryParse(datasetName, out var dataset))
        {
            throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "preview_dataset_invalid");
        }

        var limit = Math.Clamp(PayloadReader.GetOptionalInt(payload, "limit") ?? DefaultLimit, 1, MaxLimit);

        var offset = Math.Max(0, PayloadReader.GetOptionalInt(payload, "offset") ?? 0);

        // 畫面只在 GL(未納入測試) 送四種原因之一；其他值只有程式送錯資料才會出現。
        GlExclusionReason? exclusionReason = null;
        var exclusionReasonName = PayloadReader.GetOptionalString(payload, "exclusionReason");
        if (exclusionReasonName is not null)
        {
            if (dataset != DataPreviewDataset.GlExcluded
                || !GlExclusionReasonNames.TryParse(exclusionReasonName, out var parsedReason))
            {
                throw JetPayloadRejection.Create(JetErrorCodes.InvalidPayload, "preview_exclusion_reason_invalid");
            }

            exclusionReason = parsedReason;
        }

        var document = await projectStore.FindAsync(projectId, cancellationToken)
            ?? throw new JetActionException(
                JetErrorCodes.ProjectNotFound,
                ProjectFolderMessages.ActiveProjectMissing);

        // GL 兩頁的欄位由已確認的配對決定：可用欄位和第四步、第五步讀同一份欄位目錄的答案。
        IReadOnlyList<DataPreviewGlColumn>? glColumns = null;
        if (dataset is DataPreviewDataset.Gl or DataPreviewDataset.GlExcluded)
        {
            var glMapping = await repositories.MappingStates.FindAsync(projectId, DatasetKind.Gl, cancellationToken);
            glColumns = DataPreviewGlColumns.For(dataset, glMapping?.Mapping, glMapping?.GlOptions);
        }

        var preview = await Task.Run(
            () => repositories.DataPreview.GetPreviewAsync(
                projectId, dataset, document.MoneyScale, limit, cancellationToken, offset, exclusionReason, glColumns),
            cancellationToken);

        return new
        {
            dataset = datasetName,
            columns = preview.Columns,
            columnDetails = preview.ColumnDetails?
                .Select(static column => new { key = column.Key, label = column.Label, valueType = column.ValueType })
                .ToArray(),
            rows = preview.Rows,
            totalCount = preview.TotalCount,
            stats = ToWireStats(preview.Stats)
        };
    }

    private static object? ToWireStats(DataPreviewStats? stats) => stats switch
    {
        null => null,
        // 金額和明細一樣用字串傳，畫面補四位小數時不經過浮點數。
        GlPreviewStats gl => new
        {
            amountAbsMin = gl.AmountAbsMin.ToString(CultureInfo.InvariantCulture),
            amountAbsMax = gl.AmountAbsMax.ToString(CultureInfo.InvariantCulture),
            postDateMin = gl.PostDateMin,
            postDateMax = gl.PostDateMax,
            voucherCount = gl.VoucherCount
        },
        GlExcludedPreviewStats excluded => new
        {
            blankPostDateCount = excluded.BlankPostDateCount,
            beforePeriodCount = excluded.BeforePeriodCount,
            afterPeriodCount = excluded.AfterPeriodCount,
            postingStatusCount = excluded.PostingStatusCount
        },
        _ => throw new InvalidOperationException(
            $"不支援的 data preview stats 型別 '{stats.GetType().Name}'。")
    };
}
