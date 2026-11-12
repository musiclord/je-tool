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
    IReadOnlyList<GlProjectedRdeValue> RdeValues);

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
/// 將 staging row 依 mapping 投影為標準化 GL entry。
/// 純函式：一列失敗回傳 error 由呼叫端決定整批 rollback。
/// </summary>
public static class GlRowProjector
{
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

        // long.MinValue 本身可存入 BIGINT，卻無法轉成非負的借／貸控制總數：
        // -long.MinValue 仍會溢位。它必須在產生衍生欄以前成為列級 projection error，
        // 不能讓 provider 以負 credit 或 wraparound control total 繼續提交。
        if (amountScaled == long.MinValue)
        {
            error = CreateControlTotalOverflowError(row, spec, amountScaled);
            return false;
        }

        if (!TryProjectDate(row, spec, GlMappingKeys.GlDate, dateOptions, out var glDate, out error)
            || !TryProjectApprovalDate(row, spec, glDate, dateOptions, out var approvalDate, out error)
            || !TryProjectManual(row, spec, out var isManual, out error)
            || !TryProjectRdeValues(
                row,
                spec,
                moneyScale,
                dateOptions,
                cancellationToken,
                collectRdeValues,
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
            rdeValues);

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
        out RowProjectionError? error)
    {
        value = 0m;
        error = null;

        var raw = GetMappedValue(row, spec, key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (MoneyScaling.TryParseAmount(raw, out value))
        {
            return true;
        }

        error = new RowProjectionError(
            row.SourceRowNumber,
            MappedColumnOrKey(spec, key),
            raw,
            ProjectionErrorReasons.AmountInvalid);
        return false;
    }

    private static bool TryProjectDate(
        StagingRow row,
        GlMappingSpec spec,
        string key,
        DateParseOptions dateOptions,
        out string? isoDate,
        out RowProjectionError? error)
    {
        error = null;

        var raw = GetMappedValue(row, spec, key);
        if (DateNormalizer.TryNormalize(raw, dateOptions, out isoDate))
        {
            return true;
        }

        error = new RowProjectionError(
            row.SourceRowNumber,
            MappedColumnOrKey(spec, key),
            raw ?? string.Empty,
            ProjectionErrorReasons.DateInvalid);
        return false;
    }

    private static bool TryProjectApprovalDate(
        StagingRow row,
        GlMappingSpec spec,
        string? glDate,
        DateParseOptions dateOptions,
        out string? approvalDate,
        out RowProjectionError? error)
    {
        switch (spec.Options.ApprovalDateMode)
        {
            case ApprovalDateModeNames.Unmapped:
                approvalDate = null;
                error = null;
                return true;
            case ApprovalDateModeNames.Mapped:
                return TryProjectDate(
                    row,
                    spec,
                    GlMappingKeys.ApprovalDate,
                    dateOptions,
                    out approvalDate,
                    out error);
            case ApprovalDateModeNames.SameAsGlDate:
                approvalDate = glDate;
                error = null;
                return true;
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
                        values = [];
                        error = new RowProjectionError(
                            row.SourceRowNumber,
                            field.SourceColumn,
                            raw,
                            ProjectionErrorReasons.RdeDateInvalid);
                        return false;
                    }

                    result?.Add(new GlProjectedRdeValue(
                        field.FieldId,
                        field.ValueType,
                        null,
                        dateValue,
                        null));
                    break;
                case RdeFieldValueTypeNames.Amount:
                    if (!MoneyScaling.TryParseAmount(raw, out var amount)
                        || !MoneyScaling.TryToScaled(amount, moneyScale, out var scaled))
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
