using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// Production WorkingPaper planning adapter. It reads only the bounded first-row
/// existence facts and the at-most-ten scenario count map required by AuditCore;
/// workbook rows remain on the writer's existing keyset streaming path.
/// </summary>
internal sealed class WorkingPaperPlanningFactsPort(
    ICompletenessDiffPageRepository completenessDiffs,
    IVoucherBalancePageRepository voucherBalances,
    IHighRiskSummaryScenariosRepository scenarioCounts,
    IFieldInfoFactsPort fieldInfos) : IWorkingPaperPlanningFactsPort
{
    public async Task<WorkingPaperPlanningFacts> ExecuteAsync(
        WorkingPaperPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var request = plan.Request;
        var firstRow = new PageRequest(null, 1);
        // 逐科目比對無法執行時不讀差異科目；只有 GL 的科目會被當成差異，不能拿來決定 Step1-3。
        var accountComparisonApplicable = request.CompletenessNaReason is null;
        var hasCompletenessDifferences = accountComparisonApplicable
            && (await completenessDiffs.GetPageAsync(
                request.ProjectId,
                request.MoneyScale,
                request.PeriodStart,
                request.PeriodEnd,
                firstRow,
                cancellationToken)).Rows.Count > 0;
        var unbalanced = await voucherBalances.GetPageAsync(
            request.ProjectId,
            request.MoneyScale,
            request.PeriodStart,
            request.PeriodEnd,
            firstRow,
            cancellationToken);
        var counts = await scenarioCounts.GetCountsAsync(
            request.ProjectId,
            cancellationToken);
        var targetTbDefinitions = await fieldInfos.ReadAsync(
            request.ProjectId,
            DatasetKind.Tb,
            FieldInfoScope.Target,
            cancellationToken);
        var targetGlDefinitions = await fieldInfos.ReadAsync(
            request.ProjectId,
            DatasetKind.Gl,
            FieldInfoScope.Target,
            cancellationToken);
        RequireTargetDefinitions(targetGlDefinitions, "GL");
        // 裁定 2026-10-29-16：沒有 TB 也能匯出底稿，只有逐科目比對能執行時才要求 TB 的欄位定義。
        if (accountComparisonApplicable)
        {
            RequireTargetDefinitions(targetTbDefinitions, "TB");
        }

        return new WorkingPaperPlanningFacts(
            hasCompletenessDifferences,
            unbalanced.Rows.Count > 0,
            counts,
            targetTbDefinitions,
            targetGlDefinitions);
    }

    private static void RequireTargetDefinitions(
        IReadOnlyList<FieldInfo> definitions,
        string datasetName)
    {
        // 底稿欄位定義在提交欄位配對時寫入，每次匯入都會清掉。缺少代表這一側尚未匯入，
        // 或最近一次匯入後還沒有重新確認欄位配對。
        if (definitions.Count == 0)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidProjectSchema,
                $"{datasetName} 在最近一次匯入後尚未確認欄位配對，無法匯出底稿。"
                + $"請回第三步確認 {datasetName} 欄位配對；尚未匯入 {datasetName} 時，請先回第二步匯入。");
        }
    }
}
