using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 資料驗證 SQL 的單一事實來源（共用述詞與有效分錄母體）。
///
/// <para><b>有效分錄母體</b>：驗證（doc_balance／一般 null_records／INF 抽樣）、預篩選、完整性 GL 彙總
/// 一律只消費投影已落地的 <c>is_effective</c>，SQL 不重做期間或過帳狀態政策。唯一刻意的 raw
/// source-quality 例外是 <c>nullPostDate</c>，只由 source-quality query／summary 消費。</para>
///
/// <para><b>共用片段</b>：<see cref="UnbalancedCore"/>（doc_balance 6 處共用的傳票彙總核心）、
/// <see cref="UnbalancedDetailCore"/>（借貸不平傳票回接有效 GL 的 count／page 共用核心）、
/// <see cref="InfSampleInsert"/>（INF 抽樣 INSERT，Local/SqlServer 兩份逐字重複收斂為一，方言差
/// 取 N 列走 <see cref="ISqlDialect.LimitClause"/> 既有縫）、null_records 四類述詞則以
/// <c>NullRecordsCategoryPredicate</c> 為中心（計數/明細/分頁三形狀共用）。</para>
///
/// <para><see cref="CompletenessDiffCte"/> 為「每科目 GL/TB 彙總差異」的 CTE；GL 側只取有效分錄，
/// TB 側取全部（TB 本身即本期變動）。兩 provider CTE 文字相同（皆 ANSI、LEFT JOIN + UNION ALL 模擬 FULL OUTER JOIN），
/// 故抽到此處由 ValidationRunRepository 與 completenessDiff/Account page repo、科目配對匯出共用。
/// 輸出欄：account_code、account_name、tb_s、gl_s、not_in_tb。</para>
/// </summary>
public static class ValidationProcedures
{
    public const string MissingTbMappingReason = CompletenessPartBProcedure.MissingTbMappingReason;

    /// <summary>raw target 中缺過帳日的來源品質 finding；不得併回 null_records。</summary>
    public const string NullPostDateSourceQualityPredicate = "post_date IS NULL";

    public static string NullPostDateSourceQualityPredicateWith(string tableAlias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableAlias);
        return $"{tableAlias}.post_date IS NULL";
    }

    /// <summary>
    /// 明示日期窗口的相容片段。不得用作一般 GL 母體；一般母體只走
    /// <see cref="GlEffectivePopulation.SqlPredicate"/>。
    /// </summary>
    public const string PeriodBounds =
        "post_date >= @periodStart AND post_date <= @periodEnd";

    /// <summary>明示日期窗口的帶前綴相容片段；不得用作一般 GL 母體。</summary>
    public static string PeriodBoundsWith(string columnPrefix) =>
        $"({columnPrefix}post_date >= @periodStart AND {columnPrefix}post_date <= @periodEnd)";

    /// <summary>
    /// doc_balance 共用的借貸不平母體核心（自 FROM 起）——有效母體、依傳票彙總、留借貸淨額≠0。
    /// 六處呼叫（validate 計數/明細 × Local/SqlServer + docBalancePage × Local/SqlServer）共用此單一定義；
    /// 投影（COUNT 包裹 vs 明細欄）由各呼叫端在前綴 SELECT 決定，游標 keyset 由 <paramref name="extraFilter"/>
    /// 併入 WHERE（分頁用 <c>" AND document_number &gt; @cursor"</c>，否則空）。
    /// </summary>
    public static string UnbalancedCore(string schemaPrefix = "", string extraFilter = "") =>
        $"FROM {schemaPrefix}target_gl_entry WHERE {GlEffectivePopulation.SqlPredicate()} AND document_number IS NOT NULL{extraFilter} " +
        "GROUP BY document_number HAVING SUM(amount_scaled) <> 0";

    /// <summary>
    /// 同一號碼跨不同入帳日的提醒，只讀有效分錄。傳票身分與既有程序仍只看號碼。
    /// countFunction 只由 provider 傳 COUNT 或 COUNT_BIG，避免 SQL Server 大量列計數溢位。
    /// </summary>
    internal static string DocumentDateReuseSummary(string schemaPrefix = "", string countFunction = "COUNT") =>
        $"SELECT {countFunction}(*), CAST(COALESCE(SUM(entry_count), 0) AS BIGINT) " +
        $"FROM (SELECT document_number, {countFunction}(*) AS entry_count " +
        $"FROM {schemaPrefix}target_gl_entry WHERE {GlEffectivePopulation.SqlPredicate()} " +
        "AND document_number IS NOT NULL AND document_number <> '' " +
        $"GROUP BY document_number HAVING {countFunction}(DISTINCT post_date) > 1) AS reused;";

    /// <summary>
    /// Validation workbook 借貸不平明細的單一母體核心：先以
    /// <see cref="UnbalancedCore"/> 找出有效母體的不平傳票，再回接有效 target GL 的每一列。
    /// Planning count 與 export keyset page 必須共用此片段，確保 10,000 列明細上限檢查看的是
    /// writer 真正會讀取的有效 detail row count，而非 distinct voucher count。
    /// <paramref name="extraFilter"/> 只供外層 entry_id keyset，需自行包含前導 AND。
    /// </summary>
    public static string UnbalancedDetailCore(
        string schemaPrefix = "",
        string extraFilter = "") =>
        $"FROM {schemaPrefix}target_gl_entry g " +
        $"WHERE {GlEffectivePopulation.SqlPredicate("g")} " +
        $"AND g.document_number IN (SELECT document_number {UnbalancedCore(schemaPrefix)}) " +
        extraFilter;

    /// <summary>
    /// 底稿 Step 1-1 明細：不平傳票的有效分錄依傳票號碼與總帳入帳日彙總借方與貸方（legacy
    /// idea-tool.bas:6686-6691）。不平的判定沿用 <see cref="UnbalancedDetailCore"/>，只看傳票號碼。
    /// 輸出欄：document_number、post_date、debit_s、credit_s（貸方為非負合計）。
    /// </summary>
    public static string UnbalancedVoucherDateSummary(string schemaPrefix = "") =>
        "SELECT g.document_number, g.post_date, " +
        "COALESCE(SUM(g.debit_amount_scaled), 0) AS debit_s, " +
        "COALESCE(SUM(g.credit_amount_scaled), 0) AS credit_s " +
        UnbalancedDetailCore(schemaPrefix) +
        "GROUP BY g.document_number, g.post_date " +
        "ORDER BY g.document_number, g.post_date";

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
        INSERT INTO {schemaPrefix}result_inf_sampling_test_sample (run_id, entry_id, document_number, line_item)
        SELECT @runId, entry_id, document_number, line_item
        FROM {schemaPrefix}target_gl_entry
        WHERE {GlEffectivePopulation.SqlPredicate()}
        ORDER BY {orderingKey}, entry_id
        {dialect.LimitClause("@n")};
        """;
    }

    /// <summary>完整性差異 CTE：GL 側只取有效分錄。</summary>
    public static readonly string CompletenessDiffCte =
        $$"""
        WITH gl AS (
            SELECT account_code, MAX(account_name) AS account_name, SUM(amount_scaled) AS s
            FROM target_gl_entry
            WHERE {{GlEffectivePopulation.SqlPredicate()}}
            GROUP BY account_code
        ),
        tb AS (
            SELECT account_code, MAX(account_name) AS account_name, SUM(change_amount_scaled) AS s
            FROM target_tb_balance
            GROUP BY account_code
        ),
        diff AS (
            SELECT tb.account_code            AS account_code,
                   COALESCE(tb.account_name, gl.account_name) AS account_name,
                   tb.s                       AS tb_s,
                   COALESCE(gl.s, 0)          AS gl_s,
                   0                          AS not_in_tb
            FROM tb
            LEFT JOIN gl ON gl.account_code = tb.account_code
            UNION ALL
            SELECT gl.account_code, gl.account_name, 0, gl.s, 1
            FROM gl
            LEFT JOIN tb ON tb.account_code = gl.account_code
            WHERE tb.account_code IS NULL
        )
        """;

    /// <summary>
    /// 與 <see cref="CompletenessDiffCte"/> 同一份 CTE，但把兩個專案事實表（target_gl_entry、
    /// target_tb_balance）前綴 <paramref name="schemaPrefix"/> 以支援 SQL Server schema-per-project。
    /// 預設 <c>""</c> 即逐字等於 <see cref="CompletenessDiffCte"/>（SQLite 路徑仍直接用常數，
    /// 不需呼叫本方法）。CTE 文字為單一事實來源（此處僅就兩個 FROM 子句加限定詞，不複製 SQL 主體）；
    /// SQL Server 呼叫端傳 <c>SqlServerProjectSchema.QualifierFor</c> 的結果。
    /// </summary>
    public static string CompletenessDiffCteFor(string schemaPrefix) =>
        CompletenessDiffCte
            .Replace("FROM target_gl_entry", $"FROM {schemaPrefix}target_gl_entry")
            .Replace("FROM target_tb_balance", $"FROM {schemaPrefix}target_tb_balance");

    /// <summary>完整性測試的程序定義；slug 取既有 catalog。</summary>
    public static ProcedureDefinition Definition => CompletenessPartBProcedure.Definition;

    /// <summary>TB 是完整性 GL 與 TB 逐科目比對的軟依賴；缺席時只判定該程序 N/A，不跳過其他 validation 程序。</summary>
    public static ProcedureVerdict Evaluate(bool hasTbMapping) =>
        CompletenessPartBProcedure.EvaluateApplicability(hasTbMapping);
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
        // 缺 sampleSeedVersion，或版本是現行版以前的正整數，都是舊版 JET 建立的案件；
        // 目前版本不再保留舊排序法，也不替缺種子的案件補固定種子。
        if (persistedVersion is null or (> 0 and < CurrentAlgorithmVersion))
        {
            return InfSamplingSeedResolution.Legacy(persistedVersion is null
                ? "缺少 sampleSeedVersion"
                : $"sampleSeedVersion 是舊版的 {persistedVersion}");
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
    string? Error,
    bool IsLegacyProject = false)
{
    internal static InfSamplingSeedResolution Invalid(string error) =>
        new(false, 0, 0, error);

    /// <summary>舊版 JET 建立的案件：不是損壞，而是目前版本不再支援的設定。</summary>
    internal static InfSamplingSeedResolution Legacy(string error) =>
        new(false, 0, 0, error, IsLegacyProject: true);

    /// <summary>舊版案件的使用者訊息；project.json 讀取與驗證執行共用同一段文字。</summary>
    internal static string LegacyProjectMessage(string source, string? detail) =>
        $"{source} 是舊版 JET 建立的案件（{detail}），目前版本無法讀取。"
        + "請用目前版本重新建立案件，再重新匯入資料。";
}

public static partial class JetAuditProgram
{
    internal const int CurrentInfSamplingAlgorithmVersion = InfSamplingPrf.CurrentAlgorithmVersion;
    internal const long InfSamplingOrderingDomainSize = InfSamplingPrf.OrderingDomainSize;

    internal static long ComputeInfSamplingOrderingKey(long seed, long sourceRowNumber) =>
        InfSamplingPrf.ComputeOrderingKey(seed, sourceRowNumber);

    internal static InfSamplingSeedResolution ResolveInfSamplingSeed(
        long? persistedSeed,
        int? persistedVersion) =>
        InfSamplingPrf.ResolveSeed(persistedSeed, persistedVersion);
}
