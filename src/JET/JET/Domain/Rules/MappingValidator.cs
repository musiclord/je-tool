namespace JET.Domain;

public sealed record MappingValidationResult(
    IReadOnlyList<string> MissingRequiredKeys,
    IReadOnlyList<string> UnknownColumns)
{
    public bool IsValid => MissingRequiredKeys.Count == 0 && UnknownColumns.Count == 0;
}

public static class MappingValidator
{
    public static MappingValidationResult ValidateGl(
        GlMappingSpec spec,
        IReadOnlyCollection<string> availableColumns)
    {
        var required = JetFieldCatalog.RequiredGlMappingKeys(spec.AmountMode);
        // 2026-10-08：借貸代碼至少一側；只填一側時，另一側是其餘所有來源值。
        if (spec.AmountMode is GlAmountMode.AmountWithSide or GlAmountMode.AmountWithFlag
            && ((spec.Mapping.TryGetValue(GlMappingKeys.DcDebitCode, out var debit) && !string.IsNullOrWhiteSpace(debit))
                || (spec.Mapping.TryGetValue(GlMappingKeys.DcCreditCode, out var credit) && !string.IsNullOrWhiteSpace(credit))))
        {
            required = required.Where(key => key is not GlMappingKeys.DcDebitCode and not GlMappingKeys.DcCreditCode).ToArray();
        }
        return Validate(
            spec.Mapping,
            required,
            GlMappingKeys.All,
            availableColumns,
            JetFieldCatalog.IsGlLiteralMappingKey);
    }

    public static MappingValidationResult ValidateTb(
        TbMappingSpec spec,
        IReadOnlyCollection<string> availableColumns)
    {
        return Validate(
            spec.Mapping,
            JetFieldCatalog.RequiredTbMappingKeys(spec.ChangeMode),
            TbMappingKeys.All,
            availableColumns,
            JetFieldCatalog.IsTbLiteralMappingKey);
    }

    private static MappingValidationResult Validate(
        IReadOnlyDictionary<string, string> mapping,
        IReadOnlyList<string> requiredKeys,
        IReadOnlyList<string> knownKeys,
        IReadOnlyCollection<string> availableColumns,
        Func<string, bool> isLiteralMappingKey)
    {
        var missing = new List<string>();
        var unknownColumns = new List<string>();
        var columnSet = new HashSet<string>(availableColumns, StringComparer.Ordinal);
        var knownKeySet = new HashSet<string>(knownKeys, StringComparer.Ordinal);

        foreach (var key in requiredKeys)
        {
            if (!mapping.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                missing.Add(key);
            }
        }

        foreach (var (key, value) in mapping)
        {
            if (!knownKeySet.Contains(key))
            {
                unknownColumns.Add($"{key} (unknown mapping key)");
                continue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            // dcDebitCode 的值是借方代碼字面值（如 "D"、"1"），不是欄位名稱。
            if (isLiteralMappingKey(key))
            {
                continue;
            }

            if (!columnSet.Contains(value))
            {
                unknownColumns.Add(value);
            }
        }

        return new MappingValidationResult(missing, unknownColumns);
    }
}
