using System.Text;
using System.Text.RegularExpressions;

namespace JET.Domain;

/// <summary>
/// 要審計員在第三步自己選的一個配對位置。Key 是配對鍵，攸關資料元素則是後端產生的欄位識別碼；
/// Label 是第三步的欄位名稱；SourceColumn 是檔案裡寫的來源欄，沒有可寫的時候是空字串。
/// </summary>
internal sealed record UnmatchedMappingField(
    string Dataset,
    string Key,
    string Label,
    string SourceColumn);

/// <summary>由「自動化工具-檔案欄位資訊」看得見的對照表推得的草稿，以及要審計員自己選的配對位置。</summary>
internal sealed record FieldInfoSheetMappingResult(
    MappingDraftMetadata Draft,
    IReadOnlyList<UnmatchedMappingField> Unmatched);

/// <summary>
/// 從「自動化工具-檔案欄位資訊」看得見的配對前後欄位對照表推得欄位配對草稿。
/// 舊版 IDEA JET 與新版 JET 的寫法都要認得：舊版改名時把原欄名寫進說明，系統產生的欄位另有說明句；
/// 新版寫法見 FieldInfoProjector 與 FieldInfoProjection。
/// 欄名和目前匯入的欄位對得上的帶入；對不上的留著原名，由 handler 列出請審計員重選；表上看不出來的直接列在 Unmatched。
/// 推得的結果只是草稿，按「確認配對」才儲存。
/// </summary>
internal static class FieldInfoSheetMapping
{
    private const string DateFieldType = "日期型態";
    private const string NumberFieldType = "數字型態";

    private static readonly string GlAmountName = GlLegacyName(JetFieldCatalog.GlAmount);
    private static readonly string GlPostingStatusName = GlLegacyName(JetFieldCatalog.GlPostingStatus);
    private static readonly string GlApprovalDateName = GlLegacyName(JetFieldCatalog.GlApprovalDate);
    private static readonly string GlDateName = GlLegacyName(JetFieldCatalog.GlDate);
    private static readonly string TbChangeAmountName = JetFieldCatalog.TbFields
        .Single(static field => field.SemanticIdentity == JetFieldCatalog.TbChangeAmount).LegacyFieldName!;

    /// <summary>標準欄名（不含金額、過帳碼與核准日，這三種另外讀說明句）對回第一個配對位置的鍵。</summary>
    private static readonly IReadOnlyDictionary<string, string> GlStandardNames = JetFieldCatalog.GlFields
        .Where(static field => field.LegacyFieldName is not null
                               && field.SemanticIdentity is not (JetFieldCatalog.GlAmount
                                   or JetFieldCatalog.GlPostingStatus
                                   or JetFieldCatalog.GlApprovalDate))
        .ToDictionary(static field => field.LegacyFieldName!, static field => field.MappingSlots[0].Key, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> TbStandardNames = JetFieldCatalog.TbFields
        .Where(static field => field.LegacyFieldName is not null
                               && field.SemanticIdentity != JetFieldCatalog.TbChangeAmount)
        .ToDictionary(static field => field.LegacyFieldName!, static field => field.MappingSlots[0].Key, StringComparer.Ordinal);

    // 來源欄名可能有 Excel 儲存格裡的換行（匯入時只去掉頭尾空白，TabularHeaderNormalizer），說明句裡的「.」要能比到換行。
    private const RegexOptions SentenceOptions = RegexOptions.CultureInvariant | RegexOptions.Singleline;

    // 舊版「傳票金額_JE 由系統產生 : …」，新版「【傳票金額_JE】由系統產生 : …」。
    private static readonly Regex GlAmountSentence = GeneratedSentence(GlAmountName);
    private static readonly Regex TbChangeAmountSentence = GeneratedSentence(TbChangeAmountName);

    // 核准日選「與總帳日期相同」：舊版寫「系統產生 : 同總帳日期_JE來源」，
    // 新版寫「【傳票核准日_JE】由系統產生 : 同【總帳日期_JE】來源」；不帶【】的「傳票核准日_JE 由系統產生 : 同總帳日期_JE來源」也認得。
    private static readonly Regex SameAsGlDateSentence = new(
        $@"^(?:【?{Regex.Escape(GlApprovalDateName)}】?\s*由)?\s*系統產生\s*[:：]\s*同\s*【?{Regex.Escape(GlDateName)}】?\s*來源$",
        SentenceOptions);

    // 舊版借方代碼寫「借方為【Z】」，新版以「」寫值，兩側都填時兩側都寫；舊版另一支腳本不寫借方代碼。
    // 「】、借貸方判斷欄位【」與句尾的寫法固定，欄名本身有「】」也分得開。
    private static readonly Regex SidedAmount = new(
        @"^金額欄位【(?<amount>.+?)】、借貸方判斷欄位【(?<drCr>.+?)】(?:，借方為(?:【(?<code>[^】]*)】|「(?<quotedCode>[^」]*)」))?(?:，貸方為「(?<creditCode>[^」]*)」)?$",
        SentenceOptions);

    private static readonly Regex MarkedDualAmount = new(
        @"^【(?<debit>[^】]+)】\s*-\s*【(?<credit>[^】]+)】$",
        SentenceOptions);

    private static readonly Regex MarkedSingleName = new(
        @"^【(?<name>[^】]+)】$",
        SentenceOptions);

    private static readonly Regex MarkedOpenCloseBySide = new(
        @"^\(\s*【(?<closingDebitBalance>[^】]+)】\s*-\s*【(?<closingCreditBalance>[^】]+)】\s*\)\s*-\s*\(\s*【(?<openingDebitBalance>[^】]+)】\s*-\s*【(?<openingCreditBalance>[^】]+)】\s*\)$",
        SentenceOptions);

    // 新版「【X】過帳判斷值「V1」「V2」，空白也算已過帳」；舊版「X ：過帳判斷值【V】」。
    // 「】過帳判斷值」後面的寫法固定，欄名本身有「】」也分得開。
    private static readonly Regex MarkedPostingSentence = new(
        @"^【(?<column>.+)】過帳判斷值(?<values>(?:「[^」]*」)*)(?<blank>，空白也算已過帳)?$",
        SentenceOptions);

    private static readonly Regex LegacyPostingSentence = new(
        @"^(?<column>.+?)\s*[：:]\s*過帳判斷值【(?<value>[^】]*)】$",
        SentenceOptions);

    private static readonly Regex QuotedValue = new(@"「(?<value>[^」]*)」", SentenceOptions);

    /// <summary>舊版過帳判斷值填「XXXX」表示只留過帳狀態空白的分錄。</summary>
    private const string LegacyBlankPostingValue = "XXXX";

    internal static FieldInfoSheetMappingResult Infer(
        FieldInfoSheet sheet,
        IReadOnlyList<string> glColumns,
        IReadOnlyList<string>? tbColumns,
        Func<string> newRdeFieldId)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(glColumns);
        ArgumentNullException.ThrowIfNull(newRdeFieldId);

        var glRows = Rows(sheet.Gl);
        var tbRows = Rows(sheet.Tb);
        var newVersion = glRows.Concat(tbRows).Any(static row => row.Marked);
        var unmatched = new List<UnmatchedMappingField>();
        var gl = InferGl(glRows, newVersion, new ImportedColumnNames(glColumns), newRdeFieldId, unmatched);
        var glUnmatchedCount = unmatched.Count;
        var tb = tbColumns is null || tbRows.Count == 0
            ? null
            : InferTb(tbRows, newVersion, new ImportedColumnNames(tbColumns), unmatched);
        // TB 段沒有任何配對、也沒有要重選的項目時不回 TB 草稿，審計員已選的 TB 欄位與金額格式不動。
        if (tb is not null && tb.Mapping.Count == 0 && unmatched.Count == glUnmatchedCount)
        {
            tb = null;
        }

        return new FieldInfoSheetMappingResult(
            new MappingDraftMetadata(MappingMetadataFormat.CurrentVersion, gl, tb),
            unmatched);
    }

    private static GlMappingDraftMetadata InferGl(
        IReadOnlyList<SheetRow> rows,
        bool newVersion,
        ImportedColumnNames columns,
        Func<string> newRdeFieldId,
        List<UnmatchedMappingField> unmatched)
    {
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        var amountMode = GlAmountModeNames.Signed;
        var approvalDateMode = ApprovalDateModeNames.Unmapped;
        GlPostingStatusPolicy? postingPolicy = null;
        var amountRead = false;
        var postingRead = false;
        var approvalRead = false;
        var rdeRows = new List<SheetRow>();

        foreach (var row in rows.Where(row => IsMapping(row, newVersion)))
        {
            if (row.After == GlAmountName)
            {
                if (!amountRead)
                {
                    amountRead = true;
                    amountMode = ReadGlAmount(row, rows, newVersion, columns, mapping, unmatched);
                }
            }
            else if (row.After == GlPostingStatusName)
            {
                if (!postingRead)
                {
                    postingRead = true;
                    postingPolicy = ReadPostingStatus(row, columns, mapping, unmatched);
                }
            }
            else if (row.After == GlApprovalDateName)
            {
                if (!approvalRead)
                {
                    approvalRead = true;
                    if (SameAsGlDateSentence.IsMatch(row.Before))
                    {
                        approvalDateMode = ApprovalDateModeNames.SameAsGlDate;
                    }
                    else
                    {
                        approvalDateMode = ApprovalDateModeNames.Mapped;
                        mapping[GlMappingKeys.ApprovalDate] = columns.Resolve(RenamedSource(row, newVersion));
                    }
                }
            }
            else if (GlStandardNames.TryGetValue(row.After, out var key))
            {
                mapping.TryAdd(key, columns.Resolve(RenamedSource(row, newVersion)));
            }
            else if (row.After.EndsWith("_JE_S", StringComparison.Ordinal))
            {
                continue;
            }
            else if (row.After.EndsWith(GlRdeOutputNames.Suffix, StringComparison.Ordinal))
            {
                rdeRows.Add(row);
            }
        }

        var rdeFields = ReadRdeFields(rdeRows, newVersion, columns, mapping, newRdeFieldId, unmatched);
        // 人工傳票否照舊版：人工是 1、自動是 0，同 GlMappingOptions.NormalizeLegacy。
        return new GlMappingDraftMetadata(
            mapping,
            amountMode,
            approvalDateMode,
            postingPolicy,
            new GlManualAutoPolicy(["1"], ["0"]),
            rdeFields);
    }

    /// <summary>
    /// 傳票金額_JE 那一列。A 欄沒有「由系統產生」的前綴時，是舊版另一支腳本把單一金額欄原位改名，算單一帶號金額。
    /// 有前綴時依說明句：金額欄位與借貸方判斷欄位是金額加借貸別（判斷欄位是數字型態算借貸標誌）；
    /// 新版「【X】-【Y】」是借貸兩欄、「【X】」是單一帶號金額；舊版沒有括號時，有「-」是借貸兩欄，沒有是單一帶號金額。推不出來時不配對金額。
    /// </summary>
    private static string ReadGlAmount(
        SheetRow row,
        IReadOnlyList<SheetRow> rows,
        bool newVersion,
        ImportedColumnNames columns,
        Dictionary<string, string> mapping,
        List<UnmatchedMappingField> unmatched)
    {
        var sentence = GlAmountSentence.Match(row.Before);
        if (!sentence.Success)
        {
            mapping[GlMappingKeys.Amount] = columns.Resolve(RenamedSource(row, newVersion));
            return GlAmountModeNames.Signed;
        }

        var expression = sentence.Groups["expression"].Value;
        var sided = SidedAmount.Match(expression);
        if (sided.Success)
        {
            var drCr = sided.Groups["drCr"].Value;
            // 判斷欄通常是原樣留著的來源欄（E 欄空白），A 欄就是欄名原文。來源欄名本身整格加了【】時（例如「【DC】」），
            // Rows 會去掉外層，而另一個來源欄「DC」改名後 A 欄也寫「【DC】」，所以先在原樣留著的列比原文，
            // 再比全部列的原文，最後比去掉括號後的欄名（判斷欄同時配給其他欄位、已經沒有原樣列時）。
            var drCrRow = rows.FirstOrDefault(candidate => candidate.After.Length == 0
                                                           && string.Equals(candidate.Text, drCr, StringComparison.Ordinal))
                          ?? rows.FirstOrDefault(candidate => string.Equals(candidate.Text, drCr, StringComparison.Ordinal))
                          ?? rows.FirstOrDefault(candidate => string.Equals(candidate.Before, drCr, StringComparison.Ordinal));
            mapping[GlMappingKeys.Amount] = columns.Resolve(sided.Groups["amount"].Value);
            mapping[GlMappingKeys.DrCr] = columns.Resolve(drCr);
            var code = sided.Groups["code"].Success ? sided.Groups["code"].Value : sided.Groups["quotedCode"].Value;
            var creditCode = sided.Groups["creditCode"].Value;
            if (!string.IsNullOrWhiteSpace(creditCode))
            {
                mapping[GlMappingKeys.DrCrCreditCode] = creditCode;
            }
            if (!string.IsNullOrWhiteSpace(code)
                && (code != GlMappingKeys.DrCrDebitCode || !string.IsNullOrWhiteSpace(creditCode)))
            {
                mapping[GlMappingKeys.DrCrDebitCode] = code;
            }
            // 較早的版本只填貸方代碼時，說明句可能只寫配對鍵，沒有可帶回的代碼。
            else if (string.IsNullOrWhiteSpace(creditCode))
            {
                unmatched.Add(GlUnmatched(GlMappingKeys.DrCrDebitCode, string.Empty));
            }

            return drCrRow?.FieldType == NumberFieldType ? GlAmountModeNames.Flag : GlAmountModeNames.Side;
        }

        if (MarkedDualAmount.Match(expression) is { Success: true } markedDual)
        {
            mapping[GlMappingKeys.DebitAmount] = columns.Resolve(markedDual.Groups["debit"].Value);
            mapping[GlMappingKeys.CreditAmount] = columns.Resolve(markedDual.Groups["credit"].Value);
            return GlAmountModeNames.Dual;
        }

        if (MarkedSingleName.Match(expression) is { Success: true } markedSingle)
        {
            mapping[GlMappingKeys.Amount] = columns.Resolve(markedSingle.Groups["name"].Value);
            return GlAmountModeNames.Signed;
        }

        if (expression.Length == 0)
        {
            unmatched.Add(GlUnmatched(GlMappingKeys.Amount, string.Empty));
            return GlAmountModeNames.Signed;
        }

        // 新版說明句的欄名都加【】。上面三種寫法對不上，多半是欄名本身有「】」：整句只有一個欄名時去掉最外層的括號再找，
        // 其他情形不套用下面舊版的規則，列出金額請審計員選。
        if (sentence.Groups["marked"].Success)
        {
            if (expression.Length > 2
                && expression[0] == '【'
                && expression[^1] == '】'
                && columns.TryResolve(expression[1..^1], out var markedColumn))
            {
                mapping[GlMappingKeys.Amount] = markedColumn!;
                return GlAmountModeNames.Signed;
            }

            unmatched.Add(GlUnmatched(GlMappingKeys.Amount, expression));
            return GlAmountModeNames.Signed;
        }

        // 舊版不加括號：單一帶號金額的說明句與算式都只寫欄名，
        // 借貸兩欄的說明句寫「借方欄-貸方欄」、算式寫「借方欄 - 貸方欄」。
        // 欄名若有「-」，單一金額的算式就成了減法，所以說明句裡的「-」一律當成借方欄與貸方欄的分隔。

        if (!expression.Contains('-', StringComparison.Ordinal))
        {
            mapping[GlMappingKeys.Amount] = columns.Resolve(expression);
            return GlAmountModeNames.Signed;
        }

        if (TrySplitPair(expression, "-", columns, out var debit, out var credit))
        {
            mapping[GlMappingKeys.DebitAmount] = debit;
            mapping[GlMappingKeys.CreditAmount] = credit;
            return GlAmountModeNames.Dual;
        }

        unmatched.Add(GlUnmatched(GlMappingKeys.Amount, expression));
        return GlAmountModeNames.Signed;
    }

    /// <summary>過帳碼_JE 那一列：讀出過帳狀態的來源欄與過帳判斷值。值是空的或讀不懂時不配對過帳狀態。</summary>
    private static GlPostingStatusPolicy? ReadPostingStatus(
        SheetRow row,
        ImportedColumnNames columns,
        Dictionary<string, string> mapping,
        List<UnmatchedMappingField> unmatched)
    {
        string column;
        IEnumerable<string> values;
        bool includeBlank;
        if (MarkedPostingSentence.Match(row.Before) is { Success: true } marked)
        {
            column = marked.Groups["column"].Value;
            values = QuotedValue.Matches(marked.Groups["values"].Value).Select(static match => match.Groups["value"].Value);
            includeBlank = marked.Groups["blank"].Success;
        }
        else if (LegacyPostingSentence.Match(row.Before) is { Success: true } legacy)
        {
            column = legacy.Groups["column"].Value;
            var value = legacy.Groups["value"].Value;
            includeBlank = value == LegacyBlankPostingValue;
            values = includeBlank ? Array.Empty<string>() : new[] { value };
        }
        else
        {
            unmatched.Add(GlUnmatched(GlMappingKeys.PostingStatus, row.Before));
            return null;
        }

        var acceptedValues = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values.Select(static value => value.Trim()))
        {
            if (value.Length > 0 && seen.Add(value))
            {
                acceptedValues.Add(value);
            }
        }

        if (acceptedValues.Count == 0 && !includeBlank)
        {
            unmatched.Add(GlUnmatched(GlMappingKeys.PostingStatus, column));
            return null;
        }

        mapping[GlMappingKeys.PostingStatus] = columns.Resolve(column);
        return new GlPostingStatusPolicy(acceptedValues, includeBlank);
    }

    /// <summary>
    /// 其他以 _JE 結尾、不是標準欄名的列當作攸關資料元素（舊版的自行定義欄位）。
    /// 顯示名稱去掉 _JE。新版只在名稱和另一個攸關資料元素相同，或加 _JE 剛好是標準欄名時，在名稱後補「（來源欄名）」
    /// （GlRdeOutputNames.Resolve），照同一條規則反推：去掉後綴的名稱有兩列以上相同，或加 _JE 是標準欄名，就當成補上的後綴去掉，
    /// 其他情形照寫的名稱。名稱本身就叫「X（來源欄名）」而 X 符合上面兩種情形時，寫出來的欄名和補了後綴的一模一樣，
    /// 推回的名稱會是 X，這是已知限制。值型別依欄位型態。名稱空白或太長、
    /// 來源欄重複或已被核心欄位使用時不帶入，列出請審計員自己選。名稱相同但來源欄不同時照樣帶入。
    /// </summary>
    private static IReadOnlyList<GlRdeFieldMetadata> ReadRdeFields(
        IReadOnlyList<SheetRow> rows,
        bool newVersion,
        ImportedColumnNames columns,
        IReadOnlyDictionary<string, string> mapping,
        Func<string> newRdeFieldId,
        List<UnmatchedMappingField> unmatched)
    {
        var coreSources = mapping
            .Where(static pair => !JetFieldCatalog.IsGlLiteralMappingKey(pair.Key))
            .Select(static pair => pair.Value)
            .ToHashSet(StringComparer.Ordinal);
        var sources = new HashSet<string>(StringComparer.Ordinal);
        var fields = new List<GlRdeFieldMetadata>();
        var candidates = rows
            .Select(row =>
            {
                var sourceName = RenamedSource(row, newVersion);
                var written = row.After[..^GlRdeOutputNames.Suffix.Length];
                var sourceSuffix = $"（{sourceName}）";
                var withoutSuffix = written.EndsWith(sourceSuffix, StringComparison.Ordinal)
                    ? written[..^sourceSuffix.Length]
                    : null;
                return (SourceName: sourceName, Row: row, Written: written, WithoutSuffix: withoutSuffix);
            })
            .ToArray();
        var withoutSuffixCounts = candidates
            .Where(static candidate => candidate.WithoutSuffix is not null)
            .GroupBy(static candidate => candidate.WithoutSuffix!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
        foreach (var (sourceName, row, written, withoutSuffix) in candidates)
        {
            var suffixAdded = withoutSuffix is not null
                              && (withoutSuffixCounts[withoutSuffix] > 1
                                  || GlRdeOutputNames.IsStandardOutputName(withoutSuffix + GlRdeOutputNames.Suffix));
            var label = (suffixAdded ? withoutSuffix! : written).Trim();
            var source = columns.Resolve(sourceName);
            var fieldId = newRdeFieldId();
            if (label.Length == 0
                || label.Length > GlRdeStorageLimits.LabelUtf16CodeUnits
                || sources.Contains(source)
                || coreSources.Contains(source))
            {
                unmatched.Add(new UnmatchedMappingField(GlDataset, fieldId, label, sourceName));
                continue;
            }

            sources.Add(source);
            fields.Add(new GlRdeFieldMetadata(
                fieldId,
                source,
                label,
                row.FieldType switch
                {
                    DateFieldType => RdeFieldValueTypeNames.Date,
                    NumberFieldType => RdeFieldValueTypeNames.Amount,
                    _ => RdeFieldValueTypeNames.Text
                }));
        }

        return fields;
    }

    private static TbMappingDraftMetadata InferTb(
        IReadOnlyList<SheetRow> rows,
        bool newVersion,
        ImportedColumnNames columns,
        List<UnmatchedMappingField> unmatched)
    {
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        var changeMode = TbChangeModeNames.Direct;
        var amountRead = false;
        foreach (var row in rows.Where(row => IsMapping(row, newVersion)))
        {
            if (row.After == TbChangeAmountName)
            {
                if (!amountRead)
                {
                    amountRead = true;
                    changeMode = ReadTbChangeAmount(row, newVersion, columns, mapping, unmatched);
                }
            }
            else if (TbStandardNames.TryGetValue(row.After, out var key))
            {
                mapping.TryAdd(key, columns.Resolve(RenamedSource(row, newVersion)));
            }
        }

        return new TbMappingDraftMetadata(mapping, changeMode);
    }

    /// <summary>
    /// 試算表變動金額_TB 那一列。沒有前綴是原位改名，算直接變動金額；「(A - B) - (C-D)」是期末與期初各分借貸。
    /// 只有兩欄相減時看不出是借減貸還是期末減期初（舊版這兩種寫法相同），不帶入金額，列出請審計員選。
    /// </summary>
    private static string ReadTbChangeAmount(
        SheetRow row,
        bool newVersion,
        ImportedColumnNames columns,
        Dictionary<string, string> mapping,
        List<UnmatchedMappingField> unmatched)
    {
        var sentence = TbChangeAmountSentence.Match(row.Before);
        if (!sentence.Success)
        {
            mapping[TbMappingKeys.ChangeAmount] = columns.Resolve(RenamedSource(row, newVersion));
            return TbChangeModeNames.Direct;
        }

        var expression = sentence.Groups["expression"].Value;
        if (MarkedOpenCloseBySide.Match(expression) is { Success: true } marked)
        {
            mapping[TbMappingKeys.ClosingDebitBalance] = columns.Resolve(marked.Groups["closingDebitBalance"].Value);
            mapping[TbMappingKeys.ClosingCreditBalance] = columns.Resolve(marked.Groups["closingCreditBalance"].Value);
            mapping[TbMappingKeys.OpeningDebitBalance] = columns.Resolve(marked.Groups["openingDebitBalance"].Value);
            mapping[TbMappingKeys.OpeningCreditBalance] = columns.Resolve(marked.Groups["openingCreditBalance"].Value);
            return TbChangeModeNames.OpenCloseBySide;
        }

        if (!sentence.Groups["marked"].Success && TryReadLegacyOpenCloseBySide(expression, columns, out var operands))
        {
            mapping[TbMappingKeys.ClosingDebitBalance] = operands.ClosingDebitBalance;
            mapping[TbMappingKeys.ClosingCreditBalance] = operands.ClosingCreditBalance;
            mapping[TbMappingKeys.OpeningDebitBalance] = operands.OpeningDebitBalance;
            mapping[TbMappingKeys.OpeningCreditBalance] = operands.OpeningCreditBalance;
            return TbChangeModeNames.OpenCloseBySide;
        }

        unmatched.Add(new UnmatchedMappingField(
            TbDataset,
            TbMappingKeys.ChangeAmount,
            JetFieldCatalog.MappingSlotLabel(JetFieldCatalog.TbMappingSlots, TbMappingKeys.ChangeAmount),
            expression));
        return TbChangeModeNames.Direct;
    }

    /// <summary>舊版「(期末借 - 期末貸) - (期初借-期初貸)」，欄名不加括號。</summary>
    private static bool TryReadLegacyOpenCloseBySide(
        string expression,
        ImportedColumnNames columns,
        out (string ClosingDebitBalance, string ClosingCreditBalance, string OpeningDebitBalance, string OpeningCreditBalance) operands)
    {
        operands = default;
        const string middle = ") - (";
        if (!expression.StartsWith('(') || !expression.EndsWith(')'))
        {
            return false;
        }

        var inner = expression[1..^1];
        var split = inner.IndexOf(middle, StringComparison.Ordinal);
        if (split < 0 || inner.IndexOf(middle, split + 1, StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        if (!TrySplitPair(inner[..split], " - ", columns, out var closingDebitBalance, out var closingCreditBalance)
            || !TrySplitPair(inner[(split + middle.Length)..], "-", columns, out var openingDebitBalance, out var openingCreditBalance))
        {
            return false;
        }

        operands = (closingDebitBalance, closingCreditBalance, openingDebitBalance, openingCreditBalance);
        return true;
    }

    /// <summary>
    /// 把「左欄 分隔 右欄」切成兩欄。欄名本身也可能有分隔字，所以先找恰好一種兩邊都對得上目前欄位的切法；
    /// 找不到時，只有一個分隔字就照那一刀切，對不上的欄之後列出請審計員重選；其他情況推不出來。
    /// </summary>
    private static bool TrySplitPair(
        string text,
        string separator,
        ImportedColumnNames columns,
        out string left,
        out string right)
    {
        left = string.Empty;
        right = string.Empty;
        var positions = new List<int>();
        for (var index = text.IndexOf(separator, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(separator, index + 1, StringComparison.Ordinal))
        {
            positions.Add(index);
        }

        var resolvable = positions
            .Where(position => columns.TryResolve(text[..position], out _)
                               && columns.TryResolve(text[(position + separator.Length)..], out _))
            .ToArray();
        int chosen;
        if (resolvable.Length == 1)
        {
            chosen = resolvable[0];
        }
        else if (resolvable.Length == 0 && positions.Count == 1)
        {
            chosen = positions[0];
        }
        else
        {
            return false;
        }

        left = columns.Resolve(text[..chosen].Trim());
        right = columns.Resolve(text[(chosen + separator.Length)..].Trim());
        return left.Length > 0 && right.Length > 0;
    }

    private static UnmatchedMappingField GlUnmatched(string key, string sourceColumn) =>
        new(GlDataset, key, JetFieldCatalog.MappingSlotLabel(JetFieldCatalog.GlMappingSlots, key), sourceColumn);

    private static string GlDataset => DatasetKind.Gl.ToStorageName();

    private static string TbDataset => DatasetKind.Tb.ToStorageName();

    private static string GlLegacyName(string semanticIdentity) =>
        JetFieldCatalog.GlFields.Single(field => field.SemanticIdentity == semanticIdentity).LegacyFieldName!;

    private static Regex GeneratedSentence(string legacyName) => new(
        $@"^(?:(?<marked>【{Regex.Escape(legacyName)}】)|{Regex.Escape(legacyName)})\s*由系統產生\s*[:：]\s*(?<expression>.*?)\s*$",
        SentenceOptions);

    /// <summary>
    /// 新版把配對前欄位名稱寫成「【X】」：整格由一對括號包起來、裡面的括號也成對時，去掉最外層並記下這一格有括號。
    /// 來源欄名本身有括號時（例如「【【舊】地區】」）也照樣去掉最外層；說明句（例如「【傳票金額_JE】由系統產生 : …」）不動。
    /// </summary>
    private static IReadOnlyList<SheetRow> Rows(IReadOnlyList<FieldInfoSheetRow> rows) =>
        rows.Select(static row =>
            {
                var text = row.BeforeName?.Trim() ?? string.Empty;
                var before = text;
                var marked = false;
                if (before.Length > 2
                    && before[0] == '【'
                    && before[^1] == '】'
                    && BracketsArePaired(before.AsSpan(1, before.Length - 2)))
                {
                    before = before[1..^1];
                    marked = true;
                }

                return new SheetRow(before, row.FieldType?.Trim(), row.AfterName?.Trim() ?? string.Empty, marked, text);
            })
            .ToArray();

    /// <summary>
    /// 改名列的來源欄名。新版改名列的 A 欄一律寫「【來源欄名】」（FieldInfoProjection.ProjectRow）；
    /// 來源欄名本身的括號不成對時（例如「【舊地區」寫成「【【舊地區】」），Rows 不去掉外層，這裡照樣去掉。舊版照 A 欄原文。
    /// </summary>
    private static string RenamedSource(SheetRow row, bool newVersion) =>
        newVersion && !row.Marked && row.Text.Length > 2 && row.Text[0] == '【' && row.Text[^1] == '】'
            ? row.Text[1..^1]
            : row.Before;

    private static bool BracketsArePaired(ReadOnlySpan<char> text)
    {
        var depth = 0;
        foreach (var character in text)
        {
            if (character == '【')
            {
                depth++;
            }
            else if (character == '】' && --depth < 0)
            {
                return false;
            }
        }

        return depth == 0;
    }

    /// <summary>Before 是去掉外層括號後的 A 欄，Text 是 A 欄原文（只去頭尾空白）。</summary>
    private sealed record SheetRow(string Before, string? FieldType, string After, bool Marked, string Text);

    /// <summary>
    /// 這一列算不算配對。E 欄空白是原樣的來源欄或 _Temp 原欄，A 欄空白是系統產生，都不算；A 欄和 E 欄不同就是配對。
    /// A 欄和 E 欄相同時，表示來源欄本來就叫這個 JET 欄名，或是說明空白的系統產生欄位：
    /// 新版同名改名的 A 欄寫成「【X】」，沒有括號的是系統產生，例如自動編號的傳票文件項次_JE_S（FieldInfoProjection.ProjectRow）。
    /// 舊版 A 欄寫欄位說明，沒有說明時寫欄名，改名時說明是原欄名；
    /// 寫進 E 欄而說明空白的系統產生欄位在舊版 JET 查不到（Step1_Append_IsManual 沒有被呼叫），
    /// 所以舊版 A 欄和 E 欄相同就算配對。整張表有任何一格 A 欄整格加【】，就當成新版。
    /// </summary>
    private static bool IsMapping(SheetRow row, bool newVersion) =>
        row.After.Length > 0
        && row.Before.Length > 0
        && (!string.Equals(row.Before, row.After, StringComparison.Ordinal) || row.Marked || !newVersion);

    /// <summary>
    /// 檔案裡寫的來源欄名對回目前匯入的欄名：先找完全相同的；找不到時兩邊都做 NFKC、轉大寫、只留字母與數字後比較，
    /// 只有唯一一個對得上才換成目前的欄名；否則保留原名，由 handler 列出請審計員重選。
    /// </summary>
    private sealed class ImportedColumnNames
    {
        private readonly HashSet<string> exact;
        private readonly Dictionary<string, string?> byKey = new(StringComparer.Ordinal);

        internal ImportedColumnNames(IReadOnlyList<string> columns)
        {
            exact = columns.ToHashSet(StringComparer.Ordinal);
            foreach (var column in exact)
            {
                var key = ComparisonKey(column);
                if (key.Length == 0)
                {
                    continue;
                }

                // 兩個目前的欄位正規化後相同時，這個寫法不唯一，記成 null。
                byKey[key] = byKey.ContainsKey(key) ? null : column;
            }
        }

        internal bool TryResolve(string name, out string? column)
        {
            if (exact.Contains(name))
            {
                column = name;
                return true;
            }

            var key = ComparisonKey(name);
            column = key.Length > 0 && byKey.TryGetValue(key, out var unique) ? unique : null;
            return column is not null;
        }

        internal string Resolve(string name) => TryResolve(name, out var column) ? column! : name;

        private static string ComparisonKey(string value)
        {
            var normalized = value.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
            var builder = new StringBuilder(normalized.Length);
            foreach (var rune in normalized.EnumerateRunes())
            {
                if (Rune.IsLetter(rune) || Rune.IsNumber(rune))
                {
                    builder.Append(rune.ToString());
                }
            }

            return builder.ToString();
        }
    }
}
