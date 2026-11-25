using System.Globalization;

namespace JET.Domain;

public sealed record GlProjectedRow(
    int SourceRowNumber,
    string? VoucherNumber,
    string? LineItem,
    string? GlDate,
    string? ApprovalDate,
    string? AccountNumber,
    string? AccountName,
    string? Description,
    string? SourceModule,
    string? CreatedBy,
    string? ApprovedBy,
    bool? IsManual,
    string? PostingStatus,
    long AmountScaled,
    long DebitAmountScaled,
    long CreditAmountScaled,
    string DrCr,
    IReadOnlyList<GlProjectedRdeValue> RdeValues)
{
    /// <summary>來源的總帳日期有內容卻讀不成日期。<see cref="GlDate"/> 這時是 null，這一列列入 GL(未納入測試)。</summary>
    public bool GlDateUnrecognized { get; init; }

    /// <summary>這一列的日期欄位裡，讀不成日期、年份不在 1900 到 2100 年之間，或早於 1900-03-01 的值；沒有時是空的。</summary>
    public IReadOnlyList<GlDateIssue> DateIssues { get; init; } = [];
}

public sealed record GlProjectedRdeValue(
    string FieldId,
    string ValueType,
    string? TextValue,
    string? DateValue,
    long? AmountScaled);

/// <summary>
/// 一列投影錯誤。SourceRowNumber = 來源檔內的實際列號。
/// SourceLabel 由 repository 在多來源批次補上（如「JE-q2.csv [Q2]」），
/// 單來源批次保持 null（訊息與單檔時代一致）；投影純函式本身不知道來源概念。
/// </summary>
public sealed record RowProjectionError(
    int SourceRowNumber,
    string Field,
    string RawValue,
    string Reason,
    string? SourceLabel = null)
{
    public string? ReasonCode { get; init; }
}

/// <summary>
/// 依欄位配對，把匯入暫存表的一列轉成標準化的 GL 分錄。
/// 這是純函式：一列失敗時回傳錯誤，要不要整批 rollback 由呼叫端決定。
/// 日期讀不成日期時不算失敗：總帳日期與傳票核准日記成沒有日期，日期型別的攸關資料元素不產生值，
/// 並把原文記在 <see cref="GlProjectedRow.DateIssues"/>，由呼叫端決定怎麼提醒審計員。
/// </summary>
public static class GlRowProjector
{
    private static readonly string GlDateLabel = MappingLabel(GlMappingKeys.GlDate);
    private static readonly string ApprovalDateLabel = MappingLabel(GlMappingKeys.ApprovalDate);

    private static string MappingLabel(string key) =>
        JetFieldCatalog.GlFields
            .SelectMany(static field => field.MappingSlots)
            .First(slot => slot.Key == key)
            .Label;

    /// <summary>便利 overload：日期解析採預設選項（民國年啟用）。</summary>
    public static bool TryProject(
        StagingRow row,
        GlMappingSpec spec,
        int moneyScale,
        out GlProjectedRow? projected,
        out RowProjectionError? error,
        CancellationToken cancellationToken = default,
        bool collectRdeValues = true)
    {
        return TryProject(
            row,
            spec,
            moneyScale,
            DateParseOptions.Default,
            out projected,
            out error,
            cancellationToken,
            collectRdeValues);
    }

    public static bool TryProject(
        StagingRow row,
        GlMappingSpec spec,
        int moneyScale,
        DateParseOptions dateOptions,
        out GlProjectedRow? projected,
        out RowProjectionError? error,
        CancellationToken cancellationToken = default,
        bool collectRdeValues = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        projected = null;
        error = null;

        if (!TryResolveAmount(row, spec, out var amount, out error))
        {
            return false;
        }

        if (!MoneyScaling.TryToScaled(amount, moneyScale, out var amountScaled))
        {
            error = new RowProjectionError(
                row.SourceRowNumber,
                MappedColumnOrKey(spec, GlMappingKeys.Amount),
                amount.ToString(CultureInfo.InvariantCulture),
                ProjectionErrorReasons.AmountOutOfRange);
            return false;
        }

        // long.MinValue 本身存得進 BIGINT，但轉不成非負的借方或貸方控制總數：-long.MinValue 還是會溢位。
        // 所以要在產生衍生欄之前，就把這一列判成投影錯誤；
        // 不能讓資料庫那一層帶著負的貸方金額，或溢位後繞回去的控制總數繼續提交。
        if (amountScaled == long.MinValue)
        {
            error = CreateControlTotalOverflowError(row, spec, amountScaled);
            return false;
        }

        List<GlDateIssue>? dateIssues = null;
        var glDate = ProjectDate(row, spec, GlMappingKeys.GlDate, GlDateLabel, dateOptions, ref dateIssues, out var glDateUnrecognized);
        var approvalDate = ProjectApprovalDate(row, spec, glDate, dateOptions, ref dateIssues);
        if (!TryProjectManual(row, spec, out var isManual, out error)
            || !TryProjectRdeValues(
                row,
                spec,
                moneyScale,
                dateOptions,
                cancellationToken,
                collectRdeValues,
                ref dateIssues,
                out var rdeValues,
                out error))
        {
            return false;
        }

        if (!TryCheckTextLengths(row, spec, out error))
        {
            return false;
        }

        // 衍生欄一律由標準化後的 AmountScaled 計算，
        // 不直接取原始借/貸欄，確保四種金額模式語意一致。
        var debitScaled = amountScaled >= 0 ? amountScaled : 0;
        var creditScaled = amountScaled < 0 ? -amountScaled : 0;
        var drCr = amountScaled >= 0 ? "DEBIT" : "CREDIT";

        projected = new GlProjectedRow(
            row.SourceRowNumber,
            GetMappedValue(row, spec, GlMappingKeys.VoucherNumber),
            GetMappedValue(row, spec, GlMappingKeys.LineItem),
            glDate,
            approvalDate,
            GetMappedValue(row, spec, GlMappingKeys.AccountNumber),
            GetMappedValue(row, spec, GlMappingKeys.AccountName),
            GetMappedValue(row, spec, GlMappingKeys.Description),
            GetMappedValue(row, spec, GlMappingKeys.SourceModule),
            GetMappedValue(row, spec, GlMappingKeys.CreatedBy),
            GetMappedValue(row, spec, GlMappingKeys.ApprovedBy),
            isManual,
            GetMappedValue(row, spec, GlMappingKeys.PostingStatus),
            amountScaled,
            debitScaled,
            creditScaled,
            drCr,
            rdeValues)
        {
            GlDateUnrecognized = glDateUnrecognized,
            DateIssues = dateIssues is null ? [] : dateIssues
        };

        return true;
    }

    private static bool TryResolveAmount(
        StagingRow row,
        GlMappingSpec spec,
        out decimal amount,
        out RowProjectionError? error)
    {
        amount = 0m;
        error = null;

        switch (spec.AmountMode)
        {
            case GlAmountMode.SignedAmount:
            {
                return TryParseAmountCell(row, spec, GlMappingKeys.Amount, out amount, out error);
            }

            case GlAmountMode.AmountWithSide:
            case GlAmountMode.AmountWithFlag:
            {
                if (!TryParseAmountCell(row, spec, GlMappingKeys.Amount, out var magnitude, out error))
                {
                    return false;
                }

                // 借方與貸方代碼都是字面值；side / flag 兩模式共用
                // trim + 不分大小寫的文字相等比對（涵蓋 "D"/"d" 與 "1"/"0"）。
                var dcValue = GetMappedValue(row, spec, GlMappingKeys.DrCr);
                spec.Mapping.TryGetValue(GlMappingKeys.DrCrDebitCode, out var debitCode);
                spec.Mapping.TryGetValue(GlMappingKeys.DrCrCreditCode, out var creditCode);
                var hasDebitCode = !string.IsNullOrWhiteSpace(debitCode);
                var hasCreditCode = !string.IsNullOrWhiteSpace(creditCode);

                if (string.IsNullOrWhiteSpace(dcValue) && hasDebitCode == hasCreditCode)
                {
                    error = new RowProjectionError(row.SourceRowNumber,
                        MappedColumnOrKey(spec, GlMappingKeys.DrCr), dcValue ?? string.Empty,
                        ProjectionErrorReasons.DebitCreditBlank) { ReasonCode = ProjectionErrorCodes.DebitCreditBlank };
                    return false;
                }

                var isDebit = hasDebitCode && string.Equals(
                    dcValue?.Trim(),
                    debitCode?.Trim(),
                    StringComparison.OrdinalIgnoreCase);
                var isCredit = hasCreditCode && string.Equals(dcValue?.Trim(), creditCode?.Trim(), StringComparison.OrdinalIgnoreCase);
                // 只設單邊代碼時，其餘的值都歸另一側，空白也算；兩邊都設時仍拒絕未定義的值。
                if (hasDebitCode && !hasCreditCode) { isCredit = !isDebit; }
                if (hasCreditCode && !hasDebitCode) { isDebit = !isCredit; }
                if (!isDebit && !isCredit)
                {
                    error = new RowProjectionError(row.SourceRowNumber,
                        MappedColumnOrKey(spec, GlMappingKeys.DrCr), dcValue ?? string.Empty,
                        ProjectionErrorReasons.DebitCreditUnlisted) { ReasonCode = ProjectionErrorCodes.DebitCreditUnlisted };
                    return false;
                }

                // 照舊版 JET 保留來源金額的正負號，借方代碼的列照原金額，其他列乘以 -1。
                // 紅字沖銷（借方代碼的列寫 -100）因此算成貸方 100；資料本來就用正負號表示借貸時改選「單一帶號金額欄」。
                amount = isDebit ? magnitude : -magnitude;
                return true;
            }

            case GlAmountMode.DualAmount:
            {
                if (!TryParseAmountCell(row, spec, GlMappingKeys.DebitAmount, out var debit, out error)
                    || !TryParseAmountCell(row, spec, GlMappingKeys.CreditAmount, out var credit, out error))
                {
                    return false;
                }

                amount = debit - credit;
                return true;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(spec), spec.AmountMode, null);
        }
    }

    /// <summary>缺 cell / 未配對 / 空白皆視為 0（稀疏借貸欄常態）；非空但不可解析才是錯誤。</summary>
    private static bool TryParseAmountCell(
        StagingRow row,
        GlMappingSpec spec,
        string key,
        out decimal value,
        out RowProjectionError? error) =>
        MappedAmountCell.TryParse(row, spec.Mapping, key, out value, out error);

    // 空白是沒有日期。有內容卻讀不成日期時也回 null，並記下原文；讀得成但年份不在 1900 到 2100 年之間，
    // 或早於 1900-03-01、在底稿寫成文字的日期，照樣回日期，也記下來。
    private static string? ProjectDate(
        StagingRow row,
        GlMappingSpec spec,
        string key,
        string label,
        DateParseOptions dateOptions,
        ref List<GlDateIssue>? issues,
        out bool unrecognized)
    {
        var raw = GetMappedValue(row, spec, key);
        if (DateNormalizer.TryNormalize(raw, dateOptions, out var isoDate))
        {
            unrecognized = false;
            if (isoDate is not null && KeptDateIssue(isoDate) is { } kind)
            {
                (issues ??= []).Add(new GlDateIssue(
                    key, label, MappedColumnOrKey(spec, key), raw!, kind, isoDate));
            }

            return isoDate;
        }

        unrecognized = true;
        (issues ??= []).Add(new GlDateIssue(
            key, label, MappedColumnOrKey(spec, key), raw ?? string.Empty, GlDateIssueKind.Unrecognized, null));
        return null;
    }

    // 讀得成、照來源資料保留的日期要讓審計員知道的情形。年份早於 1900 的日期也早於 1900-03-01，只記年份不尋常，不重複記。
    private static GlDateIssueKind? KeptDateIssue(string isoDate) =>
        DateNormalizer.HasUnusualYear(isoDate) ? GlDateIssueKind.UnusualYear
        : DateNormalizer.IsBeforeFirstExcelFaithfulDate(isoDate) ? GlDateIssueKind.TextInWorkingPaper
        : null;

    private static string? ProjectApprovalDate(
        StagingRow row,
        GlMappingSpec spec,
        string? glDate,
        DateParseOptions dateOptions,
        ref List<GlDateIssue>? issues)
    {
        switch (spec.Options.ApprovalDateMode)
        {
            case ApprovalDateModeNames.Unmapped:
                return null;
            case ApprovalDateModeNames.Mapped:
                return ProjectDate(row, spec, GlMappingKeys.ApprovalDate, ApprovalDateLabel, dateOptions, ref issues, out _);
            case ApprovalDateModeNames.SameAsGlDate:
                // 沿用總帳日期，總帳日期的情形已經記過一次，不重複記。
                return glDate;
            default:
                throw new InvalidOperationException(
                    $"未知的 approvalDateMode：'{spec.Options.ApprovalDateMode}'。");
        }
    }

    /// <summary>人工/自動來源值先去掉頭尾空白；投影與 <see cref="ManualAutoListedCodeAudit"/> 共用。</summary>
    internal static string NormalizeManualSourceValue(string? raw) => raw?.Trim() ?? string.Empty;

    /// <summary>非空白的來源值不分大小寫等於清單上任一代碼。</summary>
    internal static bool MatchesManualAutoCode(IReadOnlyList<string> codes, string normalized) =>
        normalized.Length > 0 && codes.Contains(normalized, StringComparer.OrdinalIgnoreCase);

    private static bool TryProjectManual(
        StagingRow row,
        GlMappingSpec spec,
        out bool? isManual,
        out RowProjectionError? error)
    {
        isManual = null;
        error = null;
        if (!spec.Mapping.TryGetValue(GlMappingKeys.IsManual, out var sourceColumn)
            || string.IsNullOrWhiteSpace(sourceColumn))
        {
            return true;
        }

        var raw = row.Values.TryGetValue(sourceColumn, out var value) ? value : null;
        var normalized = NormalizeManualSourceValue(raw);
        if (MatchesManualAutoCode(spec.Options.ManualAutoPolicy.ManualValues, normalized))
        {
            isManual = true;
            return true;
        }

        if (MatchesManualAutoCode(spec.Options.ManualAutoPolicy.AutomaticValues, normalized))
        {
            isManual = false;
            return true;
        }

        var remainingKind = normalized.Length == 0
            ? spec.Options.ManualAutoPolicy.BlankValueKind
            : spec.Options.ManualAutoPolicy.UnlistedValueKind;
        if (remainingKind is ManualAutoValueKindNames.Manual or ManualAutoValueKindNames.Automatic)
        {
            isManual = remainingKind == ManualAutoValueKindNames.Manual;
            return true;
        }
        if (normalized.Length == 0 && remainingKind == ManualAutoValueKindNames.Unclassified) return true;

        error = new RowProjectionError(
            row.SourceRowNumber,
            sourceColumn,
            raw ?? string.Empty,
            normalized.Length == 0
                ? ProjectionErrorReasons.ManualBlank
                : ProjectionErrorReasons.ManualUnlisted)
        {
            ReasonCode = normalized.Length == 0 ? ProjectionErrorCodes.ManualBlank : ProjectionErrorCodes.ManualUnlisted
        };
        return false;
    }

    private static bool TryProjectRdeValues(
        StagingRow row,
        GlMappingSpec spec,
        int moneyScale,
        DateParseOptions dateOptions,
        CancellationToken cancellationToken,
        bool collectValues,
        ref List<GlDateIssue>? dateIssues,
        out IReadOnlyList<GlProjectedRdeValue> values,
        out RowProjectionError? error)
    {
        if (spec.Options.RdeFields.Count == 0)
        {
            values = [];
            error = null;
            return true;
        }

        List<GlProjectedRdeValue>? result = collectValues
            ? new List<GlProjectedRdeValue>(spec.Options.RdeFields.Count)
            : null;
        foreach (var field in spec.Options.RdeFields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = row.Values.TryGetValue(field.SourceColumn, out var sourceValue)
                ? sourceValue
                : null;
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            switch (field.ValueType)
            {
                case RdeFieldValueTypeNames.Text:
                    if (raw.Length > GlRdeStorageLimits.TextValueUtf16CodeUnits)
                    {
                        values = [];
                        error = new RowProjectionError(
                            row.SourceRowNumber,
                            field.SourceColumn,
                            raw,
                            ProjectionErrorReasons.RdeTextTooLong(GlRdeStorageLimits.TextValueUtf16CodeUnits));
                        return false;
                    }

                    result?.Add(new GlProjectedRdeValue(
                        field.FieldId,
                        field.ValueType,
                        raw,
                        null,
                        null));
                    break;
                case RdeFieldValueTypeNames.Date:
                    if (!DateNormalizer.TryNormalize(raw, dateOptions, out var dateValue))
                    {
                        // 讀不成日期的值不存成攸關資料元素，這一格等同沒有日期；原文記下來給提醒用。
                        (dateIssues ??= []).Add(new GlDateIssue(
                            field.FieldId, field.Label, field.SourceColumn, raw, GlDateIssueKind.Unrecognized, null));
                        break;
                    }

                    if (KeptDateIssue(dateValue!) is { } keptKind)
                    {
                        (dateIssues ??= []).Add(new GlDateIssue(
                            field.FieldId, field.Label, field.SourceColumn, raw, keptKind, dateValue));
                    }

                    result?.Add(new GlProjectedRdeValue(
                        field.FieldId,
                        field.ValueType,
                        null,
                        dateValue,
                        null));
                    break;
                case RdeFieldValueTypeNames.Amount:
                    if (!MoneyScaling.TryParseScaled(raw, moneyScale, out var scaled))
                    {
                        values = [];
                        error = new RowProjectionError(
                            row.SourceRowNumber,
                            field.SourceColumn,
                            raw,
                            ProjectionErrorReasons.RdeMoneyInvalid);
                        return false;
                    }

                    result?.Add(new GlProjectedRdeValue(
                        field.FieldId,
                        field.ValueType,
                        null,
                        null,
                        scaled));
                    break;
                default:
                    throw new InvalidOperationException($"未知的 RDE value type：'{field.ValueType}'。");
            }
        }

        values = result ?? [];
        error = null;
        return true;
    }

    /// <summary>標準文字欄的共用上限（<see cref="TextStorageLimits"/>）；摘要沒有上限，不列入。</summary>
    private static readonly (string Key, int Limit)[] TextLengthLimits =
    [
        (GlMappingKeys.VoucherNumber, TextStorageLimits.KeyUtf16CodeUnits),
        (GlMappingKeys.LineItem, TextStorageLimits.TextUtf16CodeUnits),
        (GlMappingKeys.AccountNumber, TextStorageLimits.KeyUtf16CodeUnits),
        (GlMappingKeys.AccountName, TextStorageLimits.TextUtf16CodeUnits),
        (GlMappingKeys.SourceModule, TextStorageLimits.TextUtf16CodeUnits),
        (GlMappingKeys.CreatedBy, TextStorageLimits.TextUtf16CodeUnits),
        (GlMappingKeys.ApprovedBy, TextStorageLimits.TextUtf16CodeUnits),
        (GlMappingKeys.PostingStatus, TextStorageLimits.TextUtf16CodeUnits)
    ];

    private static bool TryCheckTextLengths(StagingRow row, GlMappingSpec spec, out RowProjectionError? error)
    {
        foreach (var (key, limit) in TextLengthLimits)
        {
            var value = GetMappedValue(row, spec, key);
            if (value is not null && value.Length > limit)
            {
                error = new RowProjectionError(
                    row.SourceRowNumber,
                    MappedColumnOrKey(spec, key),
                    value,
                    ProjectionErrorReasons.TextTooLong(limit));
                return false;
            }
        }

        error = null;
        return true;
    }

    private static string? GetMappedValue(StagingRow row, GlMappingSpec spec, string key)
    {
        if (!spec.Mapping.TryGetValue(key, out var column) || string.IsNullOrWhiteSpace(column))
        {
            return null;
        }

        return row.Values.TryGetValue(column, out var value) ? value : null;
    }

    private static string MappedColumnOrKey(GlMappingSpec spec, string key)
    {
        return spec.Mapping.TryGetValue(key, out var column) && !string.IsNullOrWhiteSpace(column)
            ? column
            : key;
    }

    internal static RowProjectionError CreateControlTotalOverflowError(
        StagingRow row,
        GlMappingSpec spec,
        long amountScaled)
    {
        var amountKey = spec.AmountMode == GlAmountMode.DualAmount
            ? amountScaled < 0 ? GlMappingKeys.CreditAmount : GlMappingKeys.DebitAmount
            : GlMappingKeys.Amount;
        return new RowProjectionError(
            row.SourceRowNumber,
            MappedColumnOrKey(spec, amountKey),
            GetMappedValue(row, spec, amountKey)
                ?? amountScaled.ToString(CultureInfo.InvariantCulture),
            ProjectionErrorReasons.ControlTotalOutOfRange);
    }
}
