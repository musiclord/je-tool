using System.Text.Json;
using JET.Domain;

namespace JET.Application;

/// <summary>
/// query.dataPreview：正式版的使用者資料預覽。
/// 有界唯讀——欄位配對時對照欄名與實際內容、進階篩選前掌握數值/日期/摘要的大概樣貌；
/// 以 limit 與 offset 每次回傳最多 200 筆，不一次讀取完整母體。
/// GL(未納入測試) 可以用 exclusionReason 只看一種未納入原因（使用者 2026-10-21 裁定）。
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

        var preview = await Task.Run(
            () => repositories.DataPreview.GetPreviewAsync(
                projectId, dataset, document.MoneyScale, limit, cancellationToken, offset, exclusionReason),
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
        GlPreviewStats gl => new
        {
            amountAbsMin = gl.AmountAbsMin,
            amountAbsMax = gl.AmountAbsMax,
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
