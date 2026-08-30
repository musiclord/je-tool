using System.Globalization;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 定義六種工作簿在內容比對時應保留的固定標題列，以及續頁的判定方式。
/// 這裡只描述工作簿結構，不包含案件名稱、差異白名單或執行階段。
/// </summary>
internal static class LegacyWorkbookHeaderCatalog
{
    private const string ValidationSummary = "ValidationReport";
    private const string FieldInfo = "自動化工具-檔案欄位資訊";
    private const string CompletenessDifference = "step1-3 完整性測試之差異說明";
    private const string CompletenessDifferenceGuidance = "完整性測試出現差異時之指引";
    private const string PrescreenSummary = "Pre-screening_Report";
    private const string CriteriaSummary = "Summary Inforamtion";
    private const string InfMain = "INF Testing 可靠性測試";
    private const string InfAllFields = "可靠性樣本_所有欄位";

    private static readonly IReadOnlyDictionary<string, WorkingPaperHeaderDefinition>
        WorkingPaperRules = new Dictionary<string, WorkingPaperHeaderDefinition>(StringComparer.Ordinal)
        {
            ["資料預先整理之說明"] = new(SpreadsheetContentHeaderRule.None, false),
            ["JE WorkingPaper說明"] = new(SpreadsheetContentHeaderRule.None, false),
            ["step1 完整性測試"] = new(SpreadsheetContentHeaderRule.Fixed(19), true),
            ["step1-1 借貸不平測試"] = new(SpreadsheetContentHeaderRule.Fixed(14), true),
            ["step1-2 分錄編製人員說明"] = new(SpreadsheetContentHeaderRule.Fixed(11), true),
            [CompletenessDifference] = new(SpreadsheetContentHeaderRule.Fixed(16), true),
            ["step2 可靠性測試"] = new(SpreadsheetContentHeaderRule.Fixed(49, 50, 51, 52), false),
            ["step3 高風險條件彙總"] = new(SpreadsheetContentHeaderRule.Fixed(18), false),
            ["step4 符合高風險條件傳票"] = new(SpreadsheetContentHeaderRule.Fixed(11), true),
            ["step4-1 符合高風險條件傳票明細"] = new(SpreadsheetContentHeaderRule.Fixed(5), true),
            ["step5 財務報表關帳後調整之分錄"] = new(SpreadsheetContentHeaderRule.None, false),
            [FieldInfo] = new(SpreadsheetContentHeaderRule.FixedWithRepeats(3), false),
            ["自動化工具-科目配對資訊"] = new(SpreadsheetContentHeaderRule.Fixed(1), true),
        };

    internal static SpreadsheetContentHeaderRule Rule(
        LegacyReportKind report,
        string sheetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sheetName);
        return report switch
        {
            LegacyReportKind.ValidationReport => ValidationRule(sheetName),
            LegacyReportKind.AccountMapping => AccountMappingRule(sheetName),
            LegacyReportKind.InfReport => InfRule(sheetName),
            LegacyReportKind.PrescreenReport => PrescreenRule(sheetName),
            LegacyReportKind.CriteriaSelectionReport => CriteriaRule(sheetName),
            LegacyReportKind.WorkingPaper => WorkingPaperRule(sheetName),
            _ => throw Unknown(report, sheetName),
        };
    }

    private static SpreadsheetContentHeaderRule ValidationRule(string sheetName)
    {
        if (sheetName == ValidationSummary)
        {
            return SpreadsheetContentHeaderRule.None;
        }
        if (sheetName == FieldInfo)
        {
            return SpreadsheetContentHeaderRule.FixedWithRepeats(3);
        }
        if (sheetName == CompletenessDifferenceGuidance)
        {
            return SpreadsheetContentHeaderRule.None;
        }
        if (IsSeries(sheetName, CompletenessDifference, localizedSuffix: false))
        {
            return SpreadsheetContentHeaderRule.Fixed(16);
        }
        // Validation v4（jet-guide §7.2）：blank post date 不建 V7，改由條件式
        // `Source_Quality` 揭示來源品質明細。
        if (IsSeries(sheetName, "Source_Quality", localizedSuffix: false))
        {
            return SpreadsheetContentHeaderRule.Fixed(1);
        }
        for (var index = 1; index <= 7; index++)
        {
            if (IsSeries(sheetName, $"V_Report {index}", localizedSuffix: false))
            {
                return SpreadsheetContentHeaderRule.Fixed(1);
            }
        }
        throw Unknown(LegacyReportKind.ValidationReport, sheetName);
    }

    private static SpreadsheetContentHeaderRule AccountMappingRule(string sheetName) =>
        sheetName switch
        {
            "AccountMapping" => SpreadsheetContentHeaderRule.Fixed(3),
            "List" => SpreadsheetContentHeaderRule.Fixed(1),
            _ => throw Unknown(LegacyReportKind.AccountMapping, sheetName),
        };

    private static SpreadsheetContentHeaderRule InfRule(string sheetName) =>
        sheetName switch
        {
            InfMain => SpreadsheetContentHeaderRule.Fixed(51, 52),
            InfAllFields => SpreadsheetContentHeaderRule.Fixed(1),
            _ => throw Unknown(LegacyReportKind.InfReport, sheetName),
        };

    private static SpreadsheetContentHeaderRule PrescreenRule(string sheetName)
    {
        if (sheetName == PrescreenSummary)
        {
            return SpreadsheetContentHeaderRule.None;
        }
        for (var index = 1; index <= 7; index++)
        {
            if (IsSeries(sheetName, $"R{index}", localizedSuffix: false))
            {
                return SpreadsheetContentHeaderRule.Fixed(1);
            }
        }
        throw Unknown(LegacyReportKind.PrescreenReport, sheetName);
    }

    private static SpreadsheetContentHeaderRule CriteriaRule(string sheetName)
    {
        if (sheetName == CriteriaSummary)
        {
            return SpreadsheetContentHeaderRule.None;
        }
        if (TryCriteriaBaseName(sheetName, out var baseName)
            && IsSeries(sheetName, baseName, localizedSuffix: false))
        {
            return SpreadsheetContentHeaderRule.Fixed(1);
        }
        throw Unknown(LegacyReportKind.CriteriaSelectionReport, sheetName);
    }

    private static SpreadsheetContentHeaderRule WorkingPaperRule(string sheetName)
    {
        if (sheetName == "自動化工具-假期假日資訊")
        {
            return SpreadsheetContentHeaderRule.Fixed(1, 10)
                .WithExactTextSignature(
                    (1, "DATE_OF_MAKEUPDAY"),
                    (2, "MAKEUPDAY_DESC"));
        }
        if (IsContinuation(
                sheetName,
                "自動化工具-假期假日資訊",
                localizedSuffix: true))
        {
            return SpreadsheetContentHeaderRule.Fixed(1);
        }
        if (WorkingPaperRules.TryGetValue(sheetName, out var exact)
            && exact is not null)
        {
            return exact.Rule;
        }
        foreach (var (baseName, definition) in WorkingPaperRules)
        {
            if (definition.AllowsContinuation
                && IsContinuation(sheetName, baseName, localizedSuffix: true))
            {
                return definition.Rule;
            }
        }
        throw Unknown(LegacyReportKind.WorkingPaper, sheetName);
    }

    private static bool TryCriteriaBaseName(string sheetName, out string baseName)
    {
        const string prefix = "#Criteria Select ";
        baseName = string.Empty;
        if (!sheetName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }
        var suffixIndex = sheetName.IndexOf(" (", prefix.Length, StringComparison.Ordinal);
        var candidate = suffixIndex < 0 ? sheetName : sheetName[..suffixIndex];
        if (!int.TryParse(
                candidate.AsSpan(prefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var position)
            || position is < 1 or > 10)
        {
            return false;
        }
        baseName = candidate;
        return true;
    }

    private static bool IsSeries(
        string sheetName,
        string baseName,
        bool localizedSuffix) =>
        sheetName == baseName
        || IsContinuation(sheetName, baseName, localizedSuffix);

    private static bool IsContinuation(
        string sheetName,
        string baseName,
        bool localizedSuffix)
    {
        var suffixStart = sheetName.LastIndexOf(" (", StringComparison.Ordinal);
        if (suffixStart <= 0 || sheetName[^1] != ')')
        {
            return false;
        }
        var prefix = sheetName[..suffixStart];
        if (!string.Equals(baseName, prefix, StringComparison.Ordinal))
        {
            return false;
        }
        var suffix = sheetName[(suffixStart + 2)..^1];
        if (localizedSuffix)
        {
            if (!suffix.StartsWith('續'))
            {
                return false;
            }
            suffix = suffix[1..];
        }
        else if (suffix.StartsWith('續'))
        {
            return false;
        }
        return int.TryParse(
                suffix,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var page)
            && page >= 2;
    }

    private static InvalidDataException Unknown(
        LegacyReportKind report,
        string sheetName) =>
        new(
            $"Legacy workbook content header catalog has no closed rule for {report}/{sheetName}; "
            + "cell values were suppressed.");

    private sealed record WorkingPaperHeaderDefinition(
        SpreadsheetContentHeaderRule Rule,
        bool AllowsContinuation);
}
