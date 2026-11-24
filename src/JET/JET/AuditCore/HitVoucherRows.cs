namespace JET.AuditCore;

/// <summary>
/// 命中傳票與整張傳票的認定，只有這一處定義。
/// 命中傳票是母體裡至少有一筆分錄命中、而且有傳票號碼的傳票；整張傳票是命中傳票在同一個母體裡的全部分錄。
/// 沒有傳票號碼的分錄不屬於任何一張傳票：它不會併成命中傳票，本身命中時在底稿裡各自一列。
/// 這裡只產生條件的 SQL 片段；分頁、排序、暫存表與讀取由查詢端負責。
/// </summary>
internal static class HitVoucherRows
{
    /// <summary>分錄有傳票號碼。</summary>
    internal static string HasVoucherNumber(string? tableAlias = null) =>
        Column(tableAlias) + " IS NOT NULL";

    /// <summary>分錄沒有傳票號碼。</summary>
    internal static string HasNoVoucherNumber(string? tableAlias = null) =>
        Column(tableAlias) + " IS NULL";

    /// <summary>
    /// 整張傳票：<paramref name="rowAlias"/> 的分錄所屬的傳票，在母體裡至少有一筆分錄符合 <paramref name="hitPredicate"/>。
    /// 子查詢的別名固定是 g，<paramref name="populationPredicate"/> 是命中分錄的母體條件，和 <paramref name="hitPredicate"/> 一樣以 g 指命中的分錄。
    /// </summary>
    internal static string InVouchersWithHit(
        string rowAlias,
        string schemaPrefix,
        string populationPredicate,
        string hitPredicate) =>
        $"{Column(rowAlias)} IN (SELECT g.voucher_number FROM {schemaPrefix}gl g "
        + $"WHERE {HasVoucherNumber("g")} AND {populationPredicate} AND ({hitPredicate}))";

    /// <summary>
    /// 分錄 g 所屬的傳票是所選已存情境的命中傳票。<paramref name="populationPredicate"/> 是命中分錄 g2 的母體條件，
    /// <paramref name="scenarioPredicate"/> 限定情境。子查詢不引用外層的 g，整個查詢只算一次命中傳票的集合。
    /// </summary>
    internal static string InSavedScenarioHitVouchers(
        string schemaPrefix,
        string scenarioPredicate,
        string populationPredicate) =>
        "g.voucher_number IN ( " +
        $"    SELECT DISTINCT g2.voucher_number FROM {schemaPrefix}filter_hit r JOIN {schemaPrefix}filter_scenario fs ON fs.scenario_id = r.scenario_id " +
        $"    JOIN {schemaPrefix}gl g2 ON g2.entry_id = r.entry_id " +
        $"    WHERE {HasVoucherNumber("g2")} AND {populationPredicate} " +
        scenarioPredicate +
        ") ";

    /// <summary>
    /// <paramref name="rowAlias"/> 的分錄所屬的傳票是參數 @scenarioId 這個已存情境的命中傳票。
    /// <paramref name="populationPredicate"/> 是命中分錄 hg 的母體條件；寫入情境結果時命中的分錄已經限定在母體裡，可以不給。
    /// </summary>
    internal static string InOneSavedScenarioHitVouchers(
        string rowAlias,
        string schemaPrefix,
        string? populationPredicate = null) =>
        $"{Column(rowAlias)} IN ("
        + $"SELECT hg.voucher_number FROM {schemaPrefix}filter_hit hh JOIN {schemaPrefix}gl hg ON hg.entry_id = hh.entry_id "
        + $"WHERE hh.scenario_id = @scenarioId AND {HasVoucherNumber("hg")}"
        + (populationPredicate is null ? string.Empty : $" AND {populationPredicate}")
        + ")";

    /// <summary>依傳票分組後，只留至少有一筆命中分錄的傳票；<paramref name="hitFlagColumn"/> 是命中記 1、沒命中記 0 的欄。</summary>
    internal static string GroupHasHit(string hitFlagColumn) => $"MAX({hitFlagColumn}) = 1";

    /// <summary>
    /// <paramref name="voucherNumberParameter"/> 這張傳票在 <paramref name="evaluatedSource"/> 裡有命中的分錄；
    /// 來源要有 voucher_number，以及命中記 1 的 is_hit 欄。
    /// </summary>
    internal static string VoucherHasHit(string evaluatedSource, string voucherNumberParameter) =>
        $"EXISTS (SELECT 1 FROM {evaluatedSource} hit WHERE hit.voucher_number = {voucherNumberParameter} AND hit.is_hit = 1)";

    /// <summary>
    /// 底稿逐筆明細要列出的分錄：屬於命中傳票，或是沒有傳票號碼但本身命中。
    /// <paramref name="hitVoucherAlias"/> 是用傳票號碼左接的命中傳票表，<paramref name="hitRowAlias"/> 是用 entry_id 左接的命中分錄表。
    /// </summary>
    internal static string ListedInWorkingPaper(string rowAlias, string hitVoucherAlias, string hitRowAlias) =>
        $"({HasVoucherNumber(hitVoucherAlias)} OR ({HasNoVoucherNumber(rowAlias)} AND {hitRowAlias}.entry_id IS NOT NULL))";

    private static string Column(string? tableAlias)
    {
        if (tableAlias is null)
        {
            return "voucher_number";
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(tableAlias);
        return tableAlias + ".voucher_number";
    }
}
