using JET.Domain;

namespace JET.AuditCore;

/// <summary>
/// 資料驗證共用的 SQL 片段都定義在這裡，各查詢不自己另寫一份。
///
/// <para><b>有效分錄</b>指納入測試的分錄，也就是 <c>is_excluded = 0</c> 的列。借貸不平測試、資料缺漏輔助檢視、INF 抽樣、
/// 預篩選與完整性測試的 GL 彙總，一律只讀整理分錄時存好的 <c>is_excluded</c>，SQL 不重新判斷查核期間或過帳狀態。
/// 唯一刻意連未納入測試的分錄一起讀的是總帳日期空白 <c>blankGlDate</c>，只有來源資料品質的查詢與摘要使用。</para>
///
/// <para><b>共用片段</b>：<see cref="UnbalancedCore"/> 是借貸不平三處查詢共用的傳票彙總。
/// <see cref="UnbalancedDetailCore"/> 把借貸不平的傳票接回有效分錄，算筆數與分頁讀取都用它。
/// <see cref="InfSampleInsert"/> 是 INF 抽樣唯一的 INSERT。資料缺漏輔助檢視四個類別的條件集中在
/// <c>BlankRecordsCategoryPredicate</c>，計數、明細與分頁三種查詢共用。</para>
///
/// <para><see cref="CompletenessDiffCte"/> 是算出每個科目 GL 彙總與 TB 彙總差異的 CTE。GL 那一側只取有效分錄；
/// TB 那一側取全部，因為 TB 本身就是本期變動。CTE 用 LEFT JOIN 加 UNION ALL 做出 FULL OUTER JOIN 的結果。
/// 執行資料驗證的 repository、完整性差異與全科目兩個分頁查詢，以及科目配對匯出都共用它。
/// 輸出欄：account_number、account_name、tb_s、gl_s、not_in_tb。</para>
/// </summary>
public static class ValidationProcedures
{
    public const string MissingTbImportReason = AccountComparisonProcedure.MissingTbImportReason;

    public const string MissingTbMappingReason = AccountComparisonProcedure.MissingTbMappingReason;

    /// <summary>總帳日期空白的條件，連未納入測試的分錄一起看。這是來源資料品質的發現，不併進資料缺漏輔助檢視。</summary>
    public const string BlankGlDateSourceQualityPredicate = "gl_date IS NULL";

    public static string BlankGlDateSourceQualityPredicateWith(string tableAlias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableAlias);
        return $"{tableAlias}.gl_date IS NULL";
    }

    /// <summary>
    /// 借貸不平三處查詢共用的 SQL，從 FROM 開始：只看納入測試的分錄，依傳票號碼彙總，留下借貸淨額不是 0 的傳票。
    /// 三處是 validate.run 的計數與明細，以及 voucherBalancePage。
    /// 要算筆數還是取明細欄，由呼叫端在前面的 SELECT 決定。<paramref name="extraFilter"/> 會接在 WHERE 的最後：
    /// 分頁時傳 <c>" AND voucher_number &gt; @cursor"</c>，從上一頁最後一張傳票之後讀起；其他情況傳空字串。
    /// </summary>
    public static string UnbalancedCore(string schemaPrefix = "", string extraFilter = "") =>
        $"FROM {schemaPrefix}gl WHERE {GlExcludedPopulation.SqlPredicate()} AND voucher_number IS NOT NULL{extraFilter} " +
        "GROUP BY voucher_number HAVING SUM(amount_scaled) <> 0";

    /// <summary>
    /// 同一號碼跨不同入帳日的提醒，只讀有效分錄。傳票身分與既有程序仍只看號碼；空白的傳票號碼和
    /// 空白傳票號碼類別用同一個判定（去掉頭尾空白後是空字串也算空白），不列入提醒。
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
    /// 驗證報告借貸不平明細共用的 SQL：先用 <see cref="UnbalancedCore"/> 找出納入測試分錄裡的不平傳票，
    /// 再回接 GL 裡納入測試的每一列。
    /// 匯出前算列數的查詢和匯出時分頁讀明細的查詢必須共用這個片段。這樣 10,000 列的明細上限，
    /// 比的是寫出程式真正會讀到的明細列數，不是不平傳票的張數。
    /// <paramref name="extraFilter"/> 只給外層依 entry_id 分頁的條件使用，內容要自己帶開頭的 AND。
    /// </summary>
    public static string UnbalancedDetailCore(
        string schemaPrefix = "",
        string extraFilter = "") =>
        $"FROM {schemaPrefix}gl g " +
        $"WHERE {GlExcludedPopulation.SqlPredicate("g")} " +
        $"AND g.voucher_number IN (SELECT voucher_number {UnbalancedCore(schemaPrefix)}) " +
        extraFilter;

    /// <summary>
    /// 底稿 Step 1-1 明細：不平傳票的有效分錄依傳票號碼與總帳日期彙總借方與貸方（照舊版 JET）。
    /// 不平的判定沿用 <see cref="UnbalancedDetailCore"/>，只看傳票號碼。
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
    /// INF 抽樣唯一的 INSERT。母體只含有效分錄。排序時用 gl 的 source_row_number 當每一列的識別，
    /// 它在同一批匯入裡依序遞增、固定不變；不用 entry_id，因為重新整理分錄後 entry_id 會重編。
    /// entry_id 只在排序鍵相同時用來分先後。排序鍵由 <see cref="InfSamplingPrf"/> 算出，是抽樣演算法第 2 版。
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

    /// <summary>完整性差異 CTE：GL 側只取 GL，不含 GL(未納入測試)。</summary>
    public static readonly string CompletenessDiffCte = CompletenessDiffCteFor("");

    /// <summary>
    /// 完整性差異 CTE 的唯一定義。
    /// 中間結果另取 gl_by_account、tb_by_account 這兩個名字，不和資料表 gl、tb 同名，
    /// 否則 SQLite 會把 CTE 裡的 FROM gl 當成引用自己。
    /// </summary>
    /// <param name="schemaPrefix">資料表名稱前的限定詞；SQLite 使用空字串。</param>
    /// <param name="useTbAccountNames">全科目報表只使用 TB 科目名稱；預設供差異頁沿用 GL 名稱補值。</param>
    public static string CompletenessDiffCteFor(string schemaPrefix, bool useTbAccountNames = false) =>
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
                   {{(useTbAccountNames ? "t.account_name" : "COALESCE(t.account_name, g.account_name)")}} AS account_name,
                   t.s                        AS tb_s,
                   COALESCE(g.s, 0)           AS gl_s,
                   0                          AS not_in_tb
            FROM tb_by_account t
            LEFT JOIN gl_by_account g ON g.account_number = t.account_number
            UNION ALL
            SELECT g.account_number, {{(useTbAccountNames ? "NULL" : "g.account_name")}}, 0, g.s, 1
            FROM gl_by_account g
            LEFT JOIN tb_by_account t ON t.account_number = g.account_number
            WHERE t.account_number IS NULL
        )
        """;

    /// <summary>完整性測試的程序定義；slug 取自 <see cref="RuleCatalog"/>。</summary>
    public static ProcedureDefinition Definition => AccountComparisonProcedure.Definition;

    /// <summary>沒有匯入 TB 或 TB 欄位配對還沒確認時，只有 GL 與 TB 逐科目比對判為 N/A，其他資料驗證程序照常執行。</summary>
    public static ProcedureVerdict Evaluate(bool hasTbMapping, bool hasTbImport = false) =>
        AccountComparisonProcedure.EvaluateApplicability(hasTbMapping, hasTbImport);
}

/// <summary>
/// INF 抽樣排序鍵（演算法第 2 版）的 C# 實作，以及產生同一個算式的 SQL。兩邊算出的值必須相同。
/// 模數 p 是 2^31-1。做法是把正整數的來源列號用 p 進位拆成左右兩半，再做三輪帶金鑰的 Feistel 運算。
/// 每一輪只有一個乘法，是一個小於 p 的數自乘；每輪金鑰的乘數也遠小於 p，所以所有中間值都不會超出有號 BIGINT。
/// 輸出是 0 以上、小於 p² 的 62 位元排序鍵，用 BIGINT 存放。
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
        // 只接受目前的抽樣演算法版本；缺版本或其他版本都是設定檔損壞。較早版本的 JET 建立的案件在讀設定檔版本時就擋下，
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
