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
        return Validate(
            spec.Mapping,
            JetFieldCatalog.RequiredGlMappingKeys(spec.AmountMode),
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
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!knownKeySet.Contains(key))
            {
                unknownColumns.Add($"{key} (unknown mapping key)");
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
