using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 資料驗證 SQL 的單一事實來源（共用述詞與有效分錄母體）。
///
/// <para><b>有效分錄母體</b>：驗證（voucher_balance／一般 blank_records／INF 抽樣）、預篩選、完整性 GL 彙總
/// 一律只消費投影已落地的 <c>is_excluded</c>，SQL 不重做期間或過帳狀態政策。唯一刻意的 raw
/// source-quality 例外是 <c>blankGlDate</c>，只由 source-quality query／summary 消費。</para>
///
/// <para><b>共用片段</b>：<see cref="UnbalancedCore"/>（voucher_balance 6 處共用的傳票彙總核心）、
/// <see cref="UnbalancedDetailCore"/>（借貸不平傳票回接有效 GL 的 count／page 共用核心）、
/// <see cref="InfSampleInsert"/>（INF 抽樣 INSERT，Local/SqlServer 兩份逐字重複收斂為一，方言差
/// 取 N 列走 <see cref="ISqlDialect.LimitClause"/> 既有縫）、blank_records 四類述詞則以
/// <c>BlankRecordsCategoryPredicate</c> 為中心（計數/明細/分頁三形狀共用）。</para>
///
/// <para><see cref="CompletenessDiffCte"/> 為「每科目 GL/TB 彙總差異」的 CTE；GL 側只取有效分錄，
/// TB 側取全部（TB 本身即本期變動）。兩 provider CTE 文字相同（皆 ANSI、LEFT JOIN + UNION ALL 模擬 FULL OUTER JOIN），
/// 故抽到此處由 ValidationRunRepository 與 completenessDiff/Account page repo、科目配對匯出共用。
/// 輸出欄：account_number、account_name、tb_s、gl_s、not_in_tb。</para>
/// </summary>
public static class ValidationProcedures
{
    public const string MissingTbImportReason = AccountComparisonProcedure.MissingTbImportReason;

    public const string MissingTbMappingReason = AccountComparisonProcedure.MissingTbMappingReason;

    /// <summary>raw target 中總帳日期空白的 finding；不得併回 blank_records。</summary>
    public const string BlankGlDateSourceQualityPredicate = "gl_date IS NULL";

    public static string BlankGlDateSourceQualityPredicateWith(string tableAlias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableAlias);
        return $"{tableAlias}.gl_date IS NULL";
    }

    /// <summary>
    /// voucher_balance 共用的借貸不平母體核心（自 FROM 起）——只看納入測試的分錄、依傳票彙總、留借貸淨額≠0。
    /// 六處呼叫（validate 計數/明細 × Local/SqlServer + voucherBalancePage × Local/SqlServer）共用此單一定義；
    /// 投影（COUNT 包裹 vs 明細欄）由各呼叫端在前綴 SELECT 決定，游標 keyset 由 <paramref name="extraFilter"/>
    /// 併入 WHERE（分頁用 <c>" AND voucher_number &gt; @cursor"</c>，否則空）。
    /// </summary>
    public static string UnbalancedCore(string schemaPrefix = "", string extraFilter = "") =>
        $"FROM {schemaPrefix}gl WHERE {GlExcludedPopulation.SqlPredicate()} AND voucher_number IS NOT NULL{extraFilter} " +
        "GROUP BY voucher_number HAVING SUM(amount_scaled) <> 0";

    /// <summary>
    /// 同一號碼跨不同入帳日的提醒，只讀有效分錄。傳票身分與既有程序仍只看號碼；空白的傳票號碼和
    /// 空白傳票號碼類別用同一個判定（去掉頭尾空白後是空字串也算空白），不列入提醒。
    /// countFunction 只由 provider 傳 COUNT 或 COUNT_BIG，避免 SQL Server 大量列計數溢位。
    /// </summary>
    internal static string VoucherNumberAcrossGlDatesSummary(
        ISqlDialect dialect,
        string schemaPrefix = "",
        string countFunction = "COUNT") =>
        $"SELECT {countFunction}(*), CAST(COALESCE(SUM(entry_count), 0) AS BIGINT) " +
        $"FROM (SELECT voucher_number, {countFunction}(*) AS entry_count " +
        $"FROM {schemaPrefix}gl WHERE {GlExcludedPopulation.SqlPredicate()} " +
        $"AND {SqlBlankText.IsNotBlank(dialect, "voucher_number")} " +
        $"GROUP BY voucher_number HAVING {countFunction}(DISTINCT gl_date) > 1) AS reused;";

    /// <summary>
    /// Validation workbook 借貸不平明細的單一母體核心：先以
    /// <see cref="UnbalancedCore"/> 找出納入測試分錄裡的不平傳票，再回接 GL 裡納入測試的每一列。
    /// Planning count 與 export keyset page 必須共用此片段，確保 10,000 列明細上限檢查看的是
    /// writer 真正會讀取的有效 detail row count，而非 distinct voucher count。
    /// <paramref name="extraFilter"/> 只供外層 entry_id keyset，需自行包含前導 AND。
    /// </summary>
    public static string UnbalancedDetailCore(
        string schemaPrefix = "",
        string extraFilter = "") =>
        $"FROM {schemaPrefix}gl g " +
        $"WHERE {GlExcludedPopulation.SqlPredicate("g")} " +
        $"AND g.voucher_number IN (SELECT voucher_number {UnbalancedCore(schemaPrefix)}) " +
        extraFilter;

    /// <summary>
    /// 底稿 Step 1-1 明細：不平傳票的有效分錄依傳票號碼與總帳日期彙總借方與貸方（legacy
    /// idea-tool.bas:6686-6691）。不平的判定沿用 <see cref="UnbalancedDetailCore"/>，只看傳票號碼。
    /// 輸出欄：voucher_number、gl_date、debit_s、credit_s（貸方為非負合計）。
    /// </summary>
    public static string UnbalancedVoucherDateSummary(string schemaPrefix = "") =>
        "SELECT g.voucher_number, g.gl_date, " +
        "COALESCE(SUM(g.debit_amount_scaled), 0) AS debit_s, " +
        "COALESCE(SUM(g.credit_amount_scaled), 0) AS credit_s " +
        UnbalancedDetailCore(schemaPrefix) +
        "GROUP BY g.voucher_number, g.gl_date " +
        "ORDER BY g.voucher_number, g.gl_date";

    /// <summary>
    /// INF 抽樣 INSERT 的共用定義：Local 與 SqlServer 共用主體，方言差由
    /// <see cref="ISqlDialect"/> 渲染。母體限有效分錄。排序識別用 target 的
    /// source_row_number（批次內單調穩定），不用重投影會重編的 entry_id；entry_id 只作唯一 tiebreak。
    /// 排序鍵使用 AuditCore canonical PRF（演算法第 2 版）。
    /// </summary>
    public static string InfSampleInsert(
        string schemaPrefix,
        ISqlDialect dialect)
    {
        var orderingKey = dialect.InfSampleOrderingKey("source_row_number", "@seed");

        return
        $"""
        INSERT INTO {schemaPrefix}inf_sample (result_id, entry_id, voucher_number, line_item)
        SELECT @resultId, entry_id, voucher_number, line_item
        FROM {schemaPrefix}gl
        WHERE {GlExcludedPopulation.SqlPredicate()}
        ORDER BY {orderingKey}, entry_id
        {dialect.LimitClause("@n")};
        """;
    }

    /// <summary>完整性差異 CTE：GL 側只取 GL，不含 GL(未納入測試)。SQLite 與 DuckDB 直接用這份。</summary>
    public static readonly string CompletenessDiffCte = CompletenessDiffCteFor("");

    /// <summary>
    /// 完整性差異 CTE 的唯一定義。<paramref name="schemaPrefix"/> 是兩張資料表 gl、tb 前面的限定詞：
    /// SQLite 與 DuckDB 傳空字串，SQL Server 傳 <c>SqlServerProjectSchema.QualifierFor</c> 的結果。
    /// 中間結果另取 gl_by_account、tb_by_account 這兩個名字，不和資料表 gl、tb 同名，
    /// 否則 SQLite 會把 CTE 裡的 FROM gl 當成引用自己。
    /// </summary>
    public static string CompletenessDiffCteFor(string schemaPrefix) =>
        $$"""
        WITH gl_by_account AS (
            SELECT account_number, MAX(account_name) AS account_name, SUM(amount_scaled) AS s
            FROM {{schemaPrefix}}gl
            WHERE {{GlExcludedPopulation.SqlPredicate()}}
            GROUP BY account_number
        ),
        tb_by_account AS (
            SELECT account_number, MAX(account_name) AS account_name, SUM(change_amount_scaled) AS s
            FROM {{schemaPrefix}}tb
            GROUP BY account_number
        ),
        diff AS (
            SELECT t.account_number            AS account_number,
                   COALESCE(t.account_name, g.account_name) AS account_name,
                   t.s                        AS tb_s,
                   COALESCE(g.s, 0)           AS gl_s,
                   0                          AS not_in_tb
            FROM tb_by_account t
            LEFT JOIN gl_by_account g ON g.account_number = t.account_number
            UNION ALL
            SELECT g.account_number, g.account_name, 0, g.s, 1
            FROM gl_by_account g
            LEFT JOIN tb_by_account t ON t.account_number = g.account_number
            WHERE t.account_number IS NULL
        )
        """;

    /// <summary>完整性測試的程序定義；slug 取既有 catalog。</summary>
    public static ProcedureDefinition Definition => AccountComparisonProcedure.Definition;

    /// <summary>TB 是完整性 GL 與 TB 逐科目比對的軟依賴；缺席時只判定該程序 N/A，不跳過其他 validation 程序。</summary>
    public static ProcedureVerdict Evaluate(bool hasTbMapping, bool hasTbImport = false) =>
        AccountComparisonProcedure.EvaluateApplicability(hasTbMapping, hasTbImport);
}

/// <summary>
/// INF v2 的 canonical 參考實作與 SQL renderer。模數 p=2^31-1；把正的 source row
/// 拆成 p 進位左右兩半，再做三輪 keyed Feistel。每輪唯一乘法是小於 p 的平方，
/// round-key 乘數也遠小於 p，所有 intermediate 都嚴格落在 signed BIGINT 內。
/// 輸出為 [0,p²) 的非負 62-bit 排序鍵，以 BIGINT 承載。
/// </summary>
internal static class InfSamplingPrf
{
    internal const int CurrentAlgorithmVersion = 2;
    internal const long Modulus = ProjectDocument.SampleSeedExclusiveUpperBound;
    internal const long OrderingDomainSize = Modulus * Modulus;

    private static readonly long[] RoundMultipliers = [48_271, 69_621, 65_539];
    private static readonly long[] RoundOffsets = [1, 104_729, 130_363];

    internal static InfSamplingSeedResolution ResolveSeed(
        long? persistedSeed,
        int? persistedVersion)
    {
        // 只接受目前的抽樣演算法版本；缺版本或其他版本都是設定檔損壞。舊版 JET 建立的案件在讀設定檔版本時就擋下，
        // 目前版本不保留舊排序法，也不替缺種子的案件補固定種子。
        if (persistedVersion is null)
        {
            return InfSamplingSeedResolution.Invalid("缺少 sampleSeedVersion");
        }

        if (persistedVersion != CurrentAlgorithmVersion)
        {
            return InfSamplingSeedResolution.Invalid(
                $"sampleSeedVersion '{persistedVersion}' 不合法；只接受 {CurrentAlgorithmVersion}");
        }

        if (persistedSeed is not { } seed)
        {
            return InfSamplingSeedResolution.Invalid(
                "sampleSeedVersion 已存在，但 sampleSeed 缺漏");
        }

        if (seed <= 0 || seed >= Modulus)
        {
            return InfSamplingSeedResolution.Invalid(
                $"sampleSeed 必須是 1 到 {Modulus - 1} 的整數（最多 10 位數）");
        }

        return new InfSamplingSeedResolution(
            IsValid: true,
            Seed: seed,
            AlgorithmVersion: CurrentAlgorithmVersion,
            Error: null);
    }

    internal static long ComputeOrderingKey(long seed, long sourceRowNumber)
    {
        if (seed <= 0 || seed >= Modulus)
        {
            throw new ArgumentOutOfRangeException(nameof(seed));
        }

        if (sourceRowNumber <= 0 || sourceRowNumber >= OrderingDomainSize)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceRowNumber));
        }

        var left = (sourceRowNumber / Modulus) % Modulus;
        var right = sourceRowNumber % Modulus;
        for (var round = 0; round < RoundMultipliers.Length; round++)
        {
            var roundKey = checked(seed * RoundMultipliers[round] + RoundOffsets[round]) % Modulus;
            var roundFunction = (checked(right * right) % Modulus + roundKey) % Modulus;
            var nextRight = (left + roundFunction) % Modulus;
            left = right;
            right = nextRight;
        }

        return checked(left * Modulus + right);
    }

    internal static string SqlOrderingKey(
        ISqlDialect dialect,
        string sourceRowNumberExpression,
        string seedExpression)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRowNumberExpression);
        ArgumentException.ThrowIfNullOrWhiteSpace(seedExpression);

        var modulus = Modulus.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var left = $"(({dialect.IntegerQuotient(sourceRowNumberExpression, modulus)}) % {modulus})";
        var right = $"(({sourceRowNumberExpression}) % {modulus})";

        for (var round = 0; round < RoundMultipliers.Length; round++)
        {
            var multiplier = RoundMultipliers[round].ToString(System.Globalization.CultureInfo.InvariantCulture);
            var offset = RoundOffsets[round].ToString(System.Globalization.CultureInfo.InvariantCulture);
            var roundKey = $"((({seedExpression}) * {multiplier} + {offset}) % {modulus})";
            var square = $"((({right}) * ({right})) % {modulus})";
            var roundFunction = $"((({square}) + ({roundKey})) % {modulus})";
            var nextRight = $"((({left}) + ({roundFunction})) % {modulus})";
            left = right;
            right = nextRight;
        }

        return $"((({left}) * {modulus}) + ({right}))";
    }
}

internal sealed record InfSamplingSeedResolution(
    bool IsValid,
    long Seed,
    int AlgorithmVersion,
    string? Error)
{
    internal static InfSamplingSeedResolution Invalid(string error) =>
        new(false, 0, 0, error);
}

public static partial class JetAuditProgram
{
    internal const int CurrentInfSamplingAlgorithmVersion = InfSamplingPrf.CurrentAlgorithmVersion;
    internal const long InfSamplingOrderingDomainSize = InfSamplingPrf.OrderingDomainSize;

    internal static InfSamplingSeedResolution ResolveInfSamplingSeed(
        long? persistedSeed,
        int? persistedVersion) =>
        InfSamplingPrf.ResolveSeed(persistedSeed, persistedVersion);
}
