using System.Globalization;

namespace JET.Domain;

public sealed record TbProjectedRow(
    int SourceRowNumber,
    string? AccountCode,
    string? AccountName,
    long ChangeAmountScaled);

public static class TbRowProjector
{
    public static bool TryProject(
        StagingRow row,
        TbMappingSpec spec,
        int moneyScale,
        out TbProjectedRow? projected,
        out RowProjectionError? error)
    {
        projected = null;
        error = null;

        decimal change;

        switch (spec.ChangeMode)
        {
            case TbChangeMode.DirectChange:
            {
                if (!TryParseAmountCell(row, spec, TbMappingKeys.Amount, out change, out error))
                {
                    return false;
                }

                break;
            }

            case TbChangeMode.DebitCredit:
            {
                if (!TryParseAmountCell(row, spec, TbMappingKeys.DebitAmt, out var debit, out error)
                    || !TryParseAmountCell(row, spec, TbMappingKeys.CreditAmt, out var credit, out error))
                {
                    return false;
                }

                change = debit - credit;
                break;
            }

            case TbChangeMode.OpenClose:
            {
                // legacy SA=2（idea-script.bas:11242）：本期變動 = 期末 − 期初。
                if (!TryParseAmountCell(row, spec, TbMappingKeys.OpeningBalance, out var opening, out error)
                    || !TryParseAmountCell(row, spec, TbMappingKeys.ClosingBalance, out var closing, out error))
                {
                    return false;
                }

                change = closing - opening;
                break;
            }

            case TbChangeMode.OpenCloseBySide:
            {
                // legacy SA=4（idea-script.bas:11283）：本期變動 = (期末借 − 期末貸) − (期初借 − 期初貸)。
                if (!TryParseAmountCell(row, spec, TbMappingKeys.OpeningDebit, out var openingDr, out error)
                    || !TryParseAmountCell(row, spec, TbMappingKeys.OpeningCredit, out var openingCr, out error)
                    || !TryParseAmountCell(row, spec, TbMappingKeys.ClosingDebit, out var closingDr, out error)
                    || !TryParseAmountCell(row, spec, TbMappingKeys.ClosingCredit, out var closingCr, out error))
                {
                    return false;
                }

                change = (closingDr - closingCr) - (openingDr - openingCr);
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(spec), spec.ChangeMode, null);
        }

        if (!MoneyScaling.TryToScaled(change, moneyScale, out var changeScaled))
        {
            error = new RowProjectionError(
                row.SourceRowNumber,
                MappedColumnOrKey(spec, TbMappingKeys.Amount),
                change.ToString(CultureInfo.InvariantCulture),
                ProjectionErrorReasons.AmountOutOfRange);
            return false;
        }

        var accountCode = GetMappedValue(row, spec, TbMappingKeys.AccNum);
        var accountName = GetMappedValue(row, spec, TbMappingKeys.AccName);
        // 和 SQL Server 欄寬相同的共用上限（TextStorageLimits）；超過就回列錯誤，不截斷。
        if (TextTooLong(row, spec, TbMappingKeys.AccNum, accountCode, TextStorageLimits.KeyUtf16CodeUnits, out error)
            || TextTooLong(row, spec, TbMappingKeys.AccName, accountName, TextStorageLimits.TextUtf16CodeUnits, out error))
        {
            return false;
        }

        projected = new TbProjectedRow(
            row.SourceRowNumber,
            accountCode,
            accountName,
            changeScaled);

        return true;
    }

    private static bool TextTooLong(
        StagingRow row,
        TbMappingSpec spec,
        string key,
        string? value,
        int limit,
        out RowProjectionError? error)
    {
        error = value is not null && value.Length > limit
            ? new RowProjectionError(
                row.SourceRowNumber,
                MappedColumnOrKey(spec, key),
                value,
                ProjectionErrorReasons.TextTooLong(limit))
            : null;
        return error is not null;
    }

    private static bool TryParseAmountCell(
        StagingRow row,
        TbMappingSpec spec,
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

    private static string? GetMappedValue(StagingRow row, TbMappingSpec spec, string key)
    {
        if (!spec.Mapping.TryGetValue(key, out var column) || string.IsNullOrWhiteSpace(column))
        {
            return null;
        }

        return row.Values.TryGetValue(column, out var value) ? value : null;
    }

    private static string MappedColumnOrKey(TbMappingSpec spec, string key)
    {
        return spec.Mapping.TryGetValue(key, out var column) && !string.IsNullOrWhiteSpace(column)
            ? column
            : key;
    }
}
