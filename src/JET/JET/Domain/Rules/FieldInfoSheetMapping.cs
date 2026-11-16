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
/// 從「自動化工具-檔案欄位資訊」看得見的配對前後欄位對照表推得欄位配對草稿。三種寫法都要認得：
/// <list type="bullet">
/// <item>舊版 IDEA JET：欄名不加符號，改名時把原欄名寫進說明；金額加借貸別與過帳判斷值的說明句用【】包住欄名與值。</item>
/// <item>較早版本的 JET：欄名一律用【】包住，例如改名列寫「【來源欄名】」，說明句裡的欄名也加【】，值用「」。</item>
/// <item>目前的 JET：欄名和來源檔一字不差、不加符號，說明句裡用半形空白和前後文分開，值用「」；兩欄相減的算式與「同總帳日期_JE來源」照舊版的寫法（FieldInfoProjector 與 FieldInfoProjection）。</item>
/// </list>
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
    private static readonly string GlLineItemName = GlLegacyName(JetFieldCatalog.GlLineItem);
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

    // 舊版與目前的寫法「傳票金額_JE 由系統產生 : …」，較早版本「【傳票金額_JE】由系統產生 : …」。
    private static readonly Regex GlAmountSentence = GeneratedSentence(GlAmountName);
    private static readonly Regex TbChangeAmountSentence = GeneratedSentence(TbChangeAmountName);

    // 核准日選「與總帳日期相同」：舊版寫「系統產生 : 同總帳日期_JE來源」，較早版本寫「【傳票核准日_JE】由系統產生 : 同【總帳日期_JE】來源」，
    // 目前寫「傳票核准日_JE 由系統產生 : 同總帳日期_JE來源」。欄名前後的括號與空白都可有可無。
    private static readonly Regex SameAsGlDateSentence = new(
        $@"^(?:【?{Regex.Escape(GlApprovalDateName)}】?\s*由)?\s*系統產生\s*[:：]\s*同\s*【?{Regex.Escape(GlDateName)}】?\s*來源$",
        SentenceOptions);

    // 舊版與較早版本用【】包住欄名：舊版借方代碼寫「借方為【Z】」，較早版本以「」寫值，兩側都填時兩側都寫；舊版另一支腳本不寫借方代碼。
    // 「】、借貸方判斷欄位【」與句尾的寫法固定，欄名本身有「】」也分得開。
    private static readonly Regex SidedAmount = new(
        @"^金額欄位【(?<amount>.+?)】、借貸方判斷欄位【(?<drCr>.+?)】(?:，借方為(?:【(?<code>[^】]*)】|「(?<quotedCode>[^」]*)」))?(?:，貸方為「(?<creditCode>[^」]*)」)?$",
        SentenceOptions);

    // 目前的寫法欄名不加符號，「金額欄位」與「借貸方判斷欄位」後面各留一個半形空白；
    // 「、借貸方判斷欄位 」與句尾的寫法固定，欄名本身有「、」「，」或括號也分得開。
    private static readonly Regex PlainSidedAmount = new(
        @"^金額欄位 (?<amount>.+?)、借貸方判斷欄位 (?<drCr>.+?)(?:，借方為「(?<quotedCode>[^」]*)」)?(?:，貸方為「(?<creditCode>[^」]*)」)?$",
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

    // 較早版本「【X】過帳判斷值「V1」「V2」，空白也算已過帳」；「】過帳判斷值」後面的寫法固定，欄名本身有「】」也分得開。
    private static readonly Regex MarkedPostingSentence = new(
        @"^【(?<column>.+)】過帳判斷值(?<values>(?:「[^」]*」)*)(?<blank>，空白也算已過帳)?$",
        SentenceOptions);

    // 目前的寫法「X 過帳判斷值「V1」「V2」，空白也算已過帳」：欄名後面留一個半形空白，「 過帳判斷值」之後的寫法固定。
    private static readonly Regex PlainPostingSentence = new(
        @"^(?<column>.+) 過帳判斷值(?<values>(?:「[^」]*」)*)(?<blank>，空白也算已過帳)?$",
        SentenceOptions);

    // 舊版「X ：過帳判斷值【V】」。
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

        var marked = UsesMarkedWriting(sheet);
        var glRows = Rows(sheet.Gl, marked);
        var tbRows = Rows(sheet.Tb, marked);
        var unmatched = new List<UnmatchedMappingField>();
        var gl = InferGl(new Section(glRows, marked, new ImportedColumnNames(glColumns)), newRdeFieldId, unmatched);
        var glUnmatchedCount = unmatched.Count;
        var tb = tbColumns is null || tbRows.Count == 0
            ? null
            : InferTb(new Section(tbRows, marked, new ImportedColumnNames(tbColumns)), unmatched);
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
        Section section,
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

        foreach (var row in section.Rows.Where(row => IsMapping(row, section)))
        {
            if (row.After == GlAmountName)
            {
                if (!amountRead)
                {
                    amountRead = true;
                    amountMode = ReadGlAmount(row, section, mapping, unmatched);
                }
            }
            else if (row.After == GlPostingStatusName)
            {
                if (!postingRead)
                {
                    postingRead = true;
                    postingPolicy = ReadPostingStatus(row, section, mapping, unmatched);
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
                        mapping[GlMappingKeys.ApprovalDate] = section.Columns.Resolve(RenamedSource(row, section));
                    }
                }
            }
            else if (GlStandardNames.TryGetValue(row.After, out var key))
            {
                mapping.TryAdd(key, section.Columns.Resolve(RenamedSource(row, section)));
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

        var rdeFields = ReadRdeFields(rdeRows, section, mapping, newRdeFieldId, unmatched);
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
    /// 較早版本「【X】-【Y】」是借貸兩欄、「【X】」是單一帶號金額；不加括號的算式見 <see cref="ReadPlainAmountExpression"/>。
    /// 推不出來時不配對金額，列出請審計員選。
    /// </summary>
    private static string ReadGlAmount(
        SheetRow row,
        Section section,
        Dictionary<string, string> mapping,
        List<UnmatchedMappingField> unmatched)
    {
        var columns = section.Columns;
        var sentence = GlAmountSentence.Match(row.Before);
        if (!sentence.Success)
        {
            mapping[GlMappingKeys.Amount] = columns.Resolve(RenamedSource(row, section));
            return GlAmountModeNames.Signed;
        }

        var expression = sentence.Groups["expression"].Value;
        var sided = SidedAmount.Match(expression);
        if (!sided.Success)
        {
            sided = PlainSidedAmount.Match(expression);
        }

        if (sided.Success)
        {
            var drCr = sided.Groups["drCr"].Value;
            // 判斷欄通常是原樣留著的來源欄（E 欄空白），A 欄就是欄名原文。較早版本的寫法裡，來源欄名本身整格加了【】時（例如「【DC】」），
            // Rows 會去掉外層，而另一個來源欄「DC」改名後 A 欄也寫「【DC】」，所以先在原樣留著的列比原文，
            // 再比全部列的原文，最後比去掉括號後的欄名（判斷欄同時配給其他欄位、已經沒有原樣列時）。
            var drCrRow = section.Rows.FirstOrDefault(candidate => candidate.After.Length == 0
                                                                   && string.Equals(candidate.Text, drCr, StringComparison.Ordinal))
                          ?? section.Rows.FirstOrDefault(candidate => string.Equals(candidate.Text, drCr, StringComparison.Ordinal))
                          ?? section.Rows.FirstOrDefault(candidate => string.Equals(candidate.Before, drCr, StringComparison.Ordinal));
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

        if (expression.Length == 0)
        {
            unmatched.Add(GlUnmatched(GlMappingKeys.Amount, string.Empty));
            return GlAmountModeNames.Signed;
        }

        if (!section.Marked)
        {
            return ReadPlainAmountExpression(expression, section, mapping, unmatched);
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

        // 較早版本的說明句欄名都加【】。上面兩種寫法對不上，多半是欄名本身有「】」：整句只有一個欄名時去掉最外層的括號再找，
        // 其他情形不套用不加括號的規則，列出金額請審計員選。
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

    /// <summary>
    /// 不加括號的金額算式，舊版與目前的寫法相同：單一帶號金額只寫欄名；借貸兩欄寫「借方欄-貸方欄」，減號前後不空格。
    /// 欄名本身可能有「-」，同一段文字可能是一個欄名，也可能是兩個欄名相減，所以先用對照表 A 欄列出的名稱確認讀法：
    /// 整段是一個列出的名稱，或在某個「-」切開、去掉兩邊空白後兩邊都是列出的名稱，只有一種讀法對得上時照那一種讀；
    /// 不只一種讀法對得上時看不出是哪一種，不帶入金額，列出請審計員選。
    /// A 欄沒有列出這些名稱時（例如舊版檔案只留下金額那一列），照舊版的規則把「-」當成借方欄與貸方欄的分隔（<see cref="TrySplitPair"/>）。
    /// </summary>
    private static string ReadPlainAmountExpression(
        string expression,
        Section section,
        Dictionary<string, string> mapping,
        List<UnmatchedMappingField> unmatched)
    {
        var columns = section.Columns;
        if (!expression.Contains('-', StringComparison.Ordinal))
        {
            mapping[GlMappingKeys.Amount] = columns.Resolve(expression);
            return GlAmountModeNames.Signed;
        }

        var listed = DashSplits(expression)
            .Select(static split => new[] { split.Left, split.Right })
            .Prepend([expression])
            .Where(reading => reading.All(section.ListedNames.Contains))
            .ToArray();
        if (listed.Length == 1)
        {
            if (listed[0].Length == 1)
            {
                mapping[GlMappingKeys.Amount] = columns.Resolve(listed[0][0]);
                return GlAmountModeNames.Signed;
            }

            mapping[GlMappingKeys.DebitAmount] = columns.Resolve(listed[0][0]);
            mapping[GlMappingKeys.CreditAmount] = columns.Resolve(listed[0][1]);
            return GlAmountModeNames.Dual;
        }

        if (listed.Length == 0 && TrySplitPair(expression, "-", columns, out var debit, out var credit))
        {
            mapping[GlMappingKeys.DebitAmount] = debit;
            mapping[GlMappingKeys.CreditAmount] = credit;
            return GlAmountModeNames.Dual;
        }

        unmatched.Add(GlUnmatched(GlMappingKeys.Amount, expression));
        return GlAmountModeNames.Signed;
    }

    /// <summary>
    /// 過帳碼_JE 那一列：讀出過帳狀態的來源欄與過帳判斷值。值是空的或讀不懂時不配對過帳狀態。
    /// 三種寫法在欄名後面接的字不同（「】過帳判斷值」「 過帳判斷值」「：過帳判斷值【」），不會互相誤認，所以不看整張表是哪一種寫法，依序比對。
    /// </summary>
    private static GlPostingStatusPolicy? ReadPostingStatus(
        SheetRow row,
        Section section,
        Dictionary<string, string> mapping,
        List<UnmatchedMappingField> unmatched)
    {
        string column;
        IEnumerable<string> values;
        bool includeBlank;
        var current = MarkedPostingSentence.Match(row.Before);
        if (!current.Success)
        {
            current = PlainPostingSentence.Match(row.Before);
        }

        if (current.Success)
        {
            column = current.Groups["column"].Value;
            values = QuotedValue.Matches(current.Groups["values"].Value).Select(static match => match.Groups["value"].Value);
            includeBlank = current.Groups["blank"].Success;
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

        mapping[GlMappingKeys.PostingStatus] = section.Columns.Resolve(column);
        return new GlPostingStatusPolicy(acceptedValues, includeBlank);
    }

    /// <summary>
    /// 其他以 _JE 結尾、不是標準欄名的列當作攸關資料元素（舊版的自行定義欄位）。
    /// 顯示名稱去掉 _JE。JET 只在名稱和另一個攸關資料元素相同，或加 _JE 剛好是標準欄名時，在名稱後補「（來源欄名）」
    /// （GlRdeOutputNames.Resolve），照同一條規則反推：去掉後綴的名稱有兩列以上相同，或加 _JE 是標準欄名，就當成補上的後綴去掉，
    /// 其他情形照寫的名稱。名稱本身就叫「X（來源欄名）」而 X 符合上面兩種情形時，寫出來的欄名和補了後綴的一模一樣，
    /// 推回的名稱會是 X，這是已知限制。值型別依欄位型態。名稱空白或太長、
    /// 來源欄重複或已被核心欄位使用時不帶入，列出請審計員自己選。名稱相同但來源欄不同時照樣帶入。
    /// </summary>
    private static IReadOnlyList<GlRdeFieldMetadata> ReadRdeFields(
        IReadOnlyList<SheetRow> rows,
        Section section,
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
                var sourceName = RenamedSource(row, section);
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
            var source = section.Columns.Resolve(sourceName);
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
        Section section,
        List<UnmatchedMappingField> unmatched)
    {
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        var changeMode = TbChangeModeNames.Direct;
        var amountRead = false;
        foreach (var row in section.Rows.Where(row => IsMapping(row, section)))
        {
            if (row.After == TbChangeAmountName)
            {
                if (!amountRead)
                {
                    amountRead = true;
                    changeMode = ReadTbChangeAmount(row, section, mapping, unmatched);
                }
            }
            else if (TbStandardNames.TryGetValue(row.After, out var key))
            {
                mapping.TryAdd(key, section.Columns.Resolve(RenamedSource(row, section)));
            }
        }

        return new TbMappingDraftMetadata(mapping, changeMode);
    }

    /// <summary>
    /// 試算表變動金額_TB 那一列。沒有前綴是原位改名，算直接變動金額；「(A - B) - (C - D)」是期末與期初各分借貸。
    /// 只有兩欄相減時看不出是借減貸還是期末減期初（舊版這兩種寫法相同），不帶入金額，列出請審計員選；
    /// 期末與期初各分借貸的算式有不只一種讀法時也一樣。
    /// </summary>
    private static string ReadTbChangeAmount(
        SheetRow row,
        Section section,
        Dictionary<string, string> mapping,
        List<UnmatchedMappingField> unmatched)
    {
        var sentence = TbChangeAmountSentence.Match(row.Before);
        if (!sentence.Success)
        {
            mapping[TbMappingKeys.ChangeAmount] = section.Columns.Resolve(RenamedSource(row, section));
            return TbChangeModeNames.Direct;
        }

        var expression = sentence.Groups["expression"].Value;
        (string ClosingDebitBalance, string ClosingCreditBalance, string OpeningDebitBalance, string OpeningCreditBalance) operands;
        var found = false;
        if (section.Marked)
        {
            if (MarkedOpenCloseBySide.Match(expression) is { Success: true } marked)
            {
                operands = (
                    section.Columns.Resolve(marked.Groups["closingDebitBalance"].Value),
                    section.Columns.Resolve(marked.Groups["closingCreditBalance"].Value),
                    section.Columns.Resolve(marked.Groups["openingDebitBalance"].Value),
                    section.Columns.Resolve(marked.Groups["openingCreditBalance"].Value));
                found = true;
            }
            else
            {
                // 讀不出四個欄名時不會用到這組值，只是讓四個欄名都有內容。
                operands = (string.Empty, string.Empty, string.Empty, string.Empty);
            }
        }
        else
        {
            found = TryReadPlainOpenCloseBySide(expression, section, out operands);
        }

        if (found)
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

    /// <summary>
    /// 不加括號的「(期末借 - 期末貸) - (期初借-期初貸)」，舊版與目前的寫法相同。欄名本身可能有「-」或括號，
    /// 所以把每一種切法都列出來，先用對照表 A 欄列出的名稱確認：只有一種切法的四個欄名都列在 A 欄時照那一種讀，
    /// 不只一種時看不出來，回 false 由呼叫端列出請審計員選。A 欄沒有列出這些名稱時照舊版的規則切（<see cref="TryReadLegacyOpenCloseBySide"/>）。
    /// </summary>
    private static bool TryReadPlainOpenCloseBySide(
        string expression,
        Section section,
        out (string ClosingDebitBalance, string ClosingCreditBalance, string OpeningDebitBalance, string OpeningCreditBalance) operands)
    {
        operands = default;
        const string middle = ") - (";
        if (!expression.StartsWith('(') || !expression.EndsWith(')'))
        {
            return false;
        }

        var inner = expression[1..^1];
        var listed = new List<(string, string, string, string)>();
        for (var split = inner.IndexOf(middle, StringComparison.Ordinal);
             split >= 0;
             split = inner.IndexOf(middle, split + 1, StringComparison.Ordinal))
        {
            foreach (var closing in DashSplits(inner[..split]))
            {
                foreach (var opening in DashSplits(inner[(split + middle.Length)..]))
                {
                    if (section.ListedNames.Contains(closing.Left)
                        && section.ListedNames.Contains(closing.Right)
                        && section.ListedNames.Contains(opening.Left)
                        && section.ListedNames.Contains(opening.Right))
                    {
                        listed.Add((closing.Left, closing.Right, opening.Left, opening.Right));
                    }
                }
            }
        }

        if (listed.Count == 1)
        {
            var (closingDebitBalance, closingCreditBalance, openingDebitBalance, openingCreditBalance) = listed[0];
            operands = (
                section.Columns.Resolve(closingDebitBalance),
                section.Columns.Resolve(closingCreditBalance),
                section.Columns.Resolve(openingDebitBalance),
                section.Columns.Resolve(openingCreditBalance));
            return true;
        }

        return listed.Count == 0 && TryReadLegacyOpenCloseBySide(expression, section.Columns, out operands);
    }

    /// <summary>舊版「(期末借 - 期末貸) - (期初借-期初貸)」，欄名不加括號；中間的「) - (」只能出現一次。</summary>
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
    /// 照舊版的規則把「左欄 分隔 右欄」切成兩欄。舊版借貸兩欄與 TB 期初那一組的分隔是「-」，TB 期末那一組是「 - 」。
    /// 欄名本身也可能有分隔字，所以先找恰好一種兩邊都對得上目前欄位的切法；找不到時，只有一個分隔字就照那一刀切，
    /// 對不上的欄之後列出請審計員重選；其他情況推不出來。
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

    /// <summary>在每一個「-」把文字切成左右兩段，去掉兩邊的空白；切開後有一邊是空的不算。</summary>
    private static IEnumerable<(string Left, string Right)> DashSplits(string text)
    {
        for (var index = text.IndexOf('-', StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf('-', index + 1))
        {
            var left = text[..index].Trim();
            var right = text[(index + 1)..].Trim();
            if (left.Length > 0 && right.Length > 0)
            {
                yield return (left, right);
            }
        }
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
    /// 這張表是不是較早版本「欄名加【】」的寫法。系統產生的金額說明句最看得出來：那個版本一律以「【傳票金額_JE】」開頭，
    /// 舊版與目前的寫法不加括號；舊版把單一金額欄原位改名時沒有說明句，也不是加【】的寫法。GL 沒有金額列時看 TB 變動金額的說明句；
    /// 兩者都看不出來時，才看有沒有任何一格整格加【】。只看整格有沒有括號會把本來就叫「【X】」的來源欄當成加了括號。
    /// </summary>
    private static bool UsesMarkedWriting(FieldInfoSheet sheet)
    {
        var glAmount = sheet.Gl.FirstOrDefault(row => string.Equals(row.AfterName?.Trim(), GlAmountName, StringComparison.Ordinal)
                                                      && !string.IsNullOrWhiteSpace(row.BeforeName));
        if (glAmount is not null)
        {
            return GlAmountSentence.Match(glAmount.BeforeName!.Trim()) is { Success: true } sentence
                   && sentence.Groups["marked"].Success;
        }

        foreach (var row in sheet.Tb)
        {
            if (string.Equals(row.AfterName?.Trim(), TbChangeAmountName, StringComparison.Ordinal)
                && TbChangeAmountSentence.Match(row.BeforeName?.Trim() ?? string.Empty) is { Success: true } sentence)
            {
                return sentence.Groups["marked"].Success;
            }
        }

        return sheet.Gl.Concat(sheet.Tb).Any(static row => IsWholeBracketed(row.BeforeName?.Trim() ?? string.Empty));
    }

    /// <summary>
    /// 較早版本把配對前欄位名稱寫成「【X】」：整格由一對括號包起來、裡面的括號也成對時，去掉最外層並記下這一格有括號。
    /// 來源欄名本身有括號時（例如「【【舊】地區】」）也照樣去掉最外層；說明句（例如「【傳票金額_JE】由系統產生 : …」）不動。
    /// 舊版與目前的寫法照 A 欄原文，本來就叫「【X】」的來源欄不去括號。
    /// </summary>
    private static IReadOnlyList<SheetRow> Rows(IReadOnlyList<FieldInfoSheetRow> rows, bool markedWriting) =>
        rows.Select(row =>
            {
                var text = row.BeforeName?.Trim() ?? string.Empty;
                var marked = markedWriting && IsWholeBracketed(text);
                return new SheetRow(marked ? text[1..^1] : text, row.FieldType?.Trim(), row.AfterName?.Trim() ?? string.Empty, marked, text);
            })
            .ToArray();

    private static bool IsWholeBracketed(string text) =>
        text.Length > 2
        && text[0] == '【'
        && text[^1] == '】'
        && BracketsArePaired(text.AsSpan(1, text.Length - 2));

    /// <summary>
    /// 改名列的來源欄名。較早版本改名列的 A 欄一律寫「【來源欄名】」；來源欄名本身的括號不成對時
    /// （例如「【舊地區」寫成「【【舊地區】」），Rows 不去掉外層，這裡照樣去掉。舊版與目前的寫法照 A 欄原文。
    /// </summary>
    private static string RenamedSource(SheetRow row, Section section) =>
        section.Marked && !row.Marked && row.Text.Length > 2 && row.Text[0] == '【' && row.Text[^1] == '】'
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

    /// <summary>Before 是 A 欄的欄名（較早版本的寫法去掉外層括號），Text 是 A 欄原文（只去頭尾空白）。</summary>
    private sealed record SheetRow(string Before, string? FieldType, string After, bool Marked, string Text);

    /// <summary>
    /// 對照表的一段（GL 或 TB）。ListedNames 是 A 欄列出的名稱：每一個來源欄都列在 A 欄，原樣留著、改名或留下 _Temp 原欄的列都寫來源欄名，
    /// 讓名的來源欄寫「原名（來源）」，去掉後綴的原名也算。說明句的切法用它確認。
    /// </summary>
    private sealed class Section
    {
        internal Section(IReadOnlyList<SheetRow> rows, bool marked, ImportedColumnNames columns)
        {
            Rows = rows;
            Marked = marked;
            Columns = columns;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows.Where(static row => row.Before.Length > 0))
            {
                names.Add(row.Before);
                names.Add(WithoutYieldSuffix(row.Before));
            }

            ListedNames = names;
            LineItemYielded = rows.Any(static row => row.After.Length == 0
                                                    && !string.Equals(row.Before, GlLineItemName, StringComparison.Ordinal)
                                                    && string.Equals(WithoutYieldSuffix(row.Before), GlLineItemName, StringComparison.Ordinal));
        }

        internal IReadOnlyList<SheetRow> Rows { get; }

        internal bool Marked { get; }

        internal ImportedColumnNames Columns { get; }

        internal IReadOnlySet<string> ListedNames { get; }

        /// <summary>沒有配對的來源欄本來就叫「傳票文件項次_JE_S」，被讓名成「傳票文件項次_JE_S（來源）」。</summary>
        internal bool LineItemYielded { get; }

        private static string WithoutYieldSuffix(string name)
        {
            while (name.Length > FieldInfoProjector.SourceNameSuffix.Length
                   && name.EndsWith(FieldInfoProjector.SourceNameSuffix, StringComparison.Ordinal))
            {
                name = name[..^FieldInfoProjector.SourceNameSuffix.Length];
            }

            return name;
        }
    }

    /// <summary>
    /// 這一列算不算配對。E 欄空白是原樣的來源欄或 _Temp 原欄，A 欄空白是系統產生，都不算；A 欄和 E 欄不同就是配對。
    /// A 欄和 E 欄相同時，表示來源欄本來就叫這個 JET 欄名，或是說明空白的系統產生欄位（只有自動編號的傳票文件項次_JE_S）：
    /// <list type="bullet">
    /// <item>較早版本同名改名的 A 欄寫成「【X】」，沒有括號的是系統產生。</item>
    /// <item>舊版 A 欄寫欄位說明，沒有說明時寫欄名，改名時說明是原欄名；寫進 E 欄而說明空白的系統產生欄位在舊版 JET 查不到
    /// （Step1_Append_IsManual 沒有被呼叫），所以 A 欄和 E 欄相同就算配對。</item>
    /// <item>目前的寫法同名改名與自動編號的傳票文件項次_JE_S 寫得一模一樣，這一列另外看：同一段有讓名成「傳票文件項次_JE_S（來源）」的列，
    /// 表示來源欄沒有配對、是自動編號；沒有讓名列時，目前匯入的 GL 有一字不差叫這個名稱的欄才算配對，否則當成自動編號。</item>
    /// </list>
    /// </summary>
    private static bool IsMapping(SheetRow row, Section section)
    {
        if (row.After.Length == 0 || row.Before.Length == 0)
        {
            return false;
        }

        if (!string.Equals(row.Before, row.After, StringComparison.Ordinal))
        {
            return true;
        }

        if (section.Marked)
        {
            return row.Marked;
        }

        return !string.Equals(row.After, GlLineItemName, StringComparison.Ordinal)
               || !section.LineItemYielded && section.Columns.ContainsExactly(GlLineItemName);
    }

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

        internal bool ContainsExactly(string name) => exact.Contains(name);

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
