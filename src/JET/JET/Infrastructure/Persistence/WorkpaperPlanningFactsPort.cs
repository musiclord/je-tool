using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// Production Working Paper planning adapter. It reads only the bounded first-row
/// existence facts and the at-most-ten scenario count map required by AuditCore;
/// workbook rows remain on the writer's existing keyset streaming path.
/// </summary>
internal sealed class WorkpaperPlanningFactsPort(
    ICompletenessDiffPageRepository completenessDiffs,
    IDocBalancePageRepository docBalances,
    ITagMatrixScenariosRepository scenarioCounts,
    ILegacyFieldDefinitionFactsPort fieldDefinitions,
    IMappingStateStore? mappings = null) : IWorkpaperPlanningFactsPort
{
    public async Task<WorkpaperPlanningFacts> ExecuteAsync(
        WorkpaperPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var request = plan.Request;
        var firstRow = new PageRequest(null, 1);
        var completeness = await completenessDiffs.GetPageAsync(
            request.ProjectId,
            request.MoneyScale,
            request.PeriodStart,
            request.PeriodEnd,
            firstRow,
            cancellationToken);
        var unbalanced = await docBalances.GetPageAsync(
            request.ProjectId,
            request.MoneyScale,
            request.PeriodStart,
            request.PeriodEnd,
            firstRow,
            cancellationToken);
        var counts = await scenarioCounts.GetCountsAsync(
            request.ProjectId,
            cancellationToken);
        var targetTbDefinitions = await fieldDefinitions.ReadAsync(
            request.ProjectId,
            DatasetKind.Tb,
            LegacyFieldDefinitionScope.Target,
            cancellationToken);
        var targetGlDefinitions = await fieldDefinitions.ReadAsync(
            request.ProjectId,
            DatasetKind.Gl,
            LegacyFieldDefinitionScope.Target,
            cancellationToken);
        RequireTargetDefinitions(targetGlDefinitions, "GL");
        RequireTargetDefinitions(targetTbDefinitions, "TB");
        var glMapping = mappings is null
            ? null
            : await mappings.FindAsync(
                request.ProjectId,
                DatasetKind.Gl,
                cancellationToken);
        var voucherDateSourceField = glMapping is not null
                                     && glMapping.Mapping.TryGetValue(
                                         GlMappingKeys.VoucherDate,
                                         out var voucherDateSource)
            ? voucherDateSource
            : null;

        return new WorkpaperPlanningFacts(
            completeness.Rows.Count > 0,
            unbalanced.Rows.Count > 0,
            counts,
            targetTbDefinitions,
            targetGlDefinitions,
            voucherDateSourceField);
    }

    private static void RequireTargetDefinitions(
        IReadOnlyList<LegacyFieldDefinition> definitions,
        string datasetName)
    {
        if (definitions.Count == 0)
        {
            throw new JetActionException(
                JetErrorCodes.InvalidProjectSchema,
                $"{datasetName} 批次缺少 Legacy 欄位定義；本版不反推舊批次，"
                + "請以 mode 'replace' 重新匯入。");
        }
    }
}
