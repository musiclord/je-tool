namespace JET.Tests.Infrastructure;

internal static class LegacyAuditParityJourneyContract
{
    private static readonly HashSet<string> PaginatedActions =
    [
        "query.infSamplePage",
        "query.prescreenPage",
    ];

    internal static IReadOnlyList<string> FullActionOrder { get; } =
    [
        "project.create",
        "import.gl.fromFile",
        "import.tb.fromFile",
        "import.authorizedPreparer.fromFile",
        "import.holiday.fromFile",
        "import.makeupDay.fromFile",
        "mapping.commit.gl",
        "mapping.commit.tb",
        // 確認配對會清除科目配對（裁定 2026-10-08-12），所以科目配對檔排在確認之後；原因見 LegacyAuditParityJourney。
        "import.accountMapping.fromFile",
        "validate.run",
        "query.infSamplePage",
        "export.validationReports",
        "export.accountMappingTemplate",
        "prescreen.run",
        "query.prescreenPage",
        "query.prescreenPage",
        "export.prescreenReport",
        "filter.commit",
        "query.highRiskSummaryScenarios",
        "export.criteriaSelectionReport",
        "export.workingPaperStream",
    ];

    /// <summary>
    /// 各段後續操作的 action 順序（分頁查詢已合併成一次）。重新開啟不重算結果，只重新匯出；
    /// 重新確認配對與重新匯入照第一次的順序，重做失效政策清掉的科目配對、資料驗證、預篩選與第五步條件。
    /// </summary>
    internal static IReadOnlyList<string> FollowUpActionOrder(LegacyAuditParityFollowUp followUp) => followUp switch
    {
        LegacyAuditParityFollowUp.Reopen =>
        [
            "project.releaseLock",
            "project.list",
            "project.load",
            "query.infSamplePage",
            "export.validationReports",
            "export.accountMappingTemplate",
            "query.prescreenPage",
            "export.prescreenReport",
            "query.highRiskSummaryScenarios",
            "export.criteriaSelectionReport",
            "export.workingPaperStream",
        ],
        LegacyAuditParityFollowUp.MappingRecommit =>
            ["mapping.commit.gl", .. RedoneDownstreamActionOrder],
        LegacyAuditParityFollowUp.Reimport =>
            ["import.gl.fromFile", "mapping.commit.gl", .. RedoneDownstreamActionOrder],
        _ => throw new ArgumentOutOfRangeException(nameof(followUp), followUp, null),
    };

    private static IReadOnlyList<string> RedoneDownstreamActionOrder =>
        NormalizePaginatedActionOrder(
            FullActionOrder
                .SkipWhile(static action => action != "import.accountMapping.fromFile")
                .ToArray());

    internal static IReadOnlyList<string> DecidedNotProvidedFullActionOrder { get; } =
        FullActionOrder
            .Where(static action => action != "import.authorizedPreparer.fromFile")
            .ToArray();

    internal static IReadOnlyList<string> NormalizePaginatedActionOrder(
        IReadOnlyList<string> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var normalized = new List<string>(actions.Count);
        foreach (var action in actions)
        {
            if (normalized.Count > 0
                && PaginatedActions.Contains(action)
                && string.Equals(normalized[^1], action, StringComparison.Ordinal))
            {
                continue;
            }
            normalized.Add(action);
        }
        return normalized;
    }
}
