using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 來源值怎麼整理只寫在 Domain 的 Primitives 資料夾：文字的比較鍵在 SourceText，金額的解析、放大與還原在 MoneyScaling。
/// 其他地方要用到時呼叫這兩個類別，不自己再寫一份，同一個值才不會在不同畫面、報告與查詢被整理成不同的結果。
/// 這個測試讀產品的 C# 原始碼，找出寫在別處的同類寫法，並列出檔案與行號。
/// </summary>
public sealed class SourceValueOwnershipTests
{
    /// <summary>只能寫在擁有者檔案裡的寫法：代號、樣式、擁有者檔案、該改用的成員，以及這段寫法的意思。</summary>
    private sealed record OwnedHandling(string Id, string Pattern, string OwnerFile, string Owner, string Meaning);

    private const string TextComparisonKeyRule = "text-comparison-key";
    private const string ScaledToDecimalRule = "scaled-to-decimal";
    private const string MidpointRoundingRule = "midpoint-rounding";
    private const string DecimalTryParseRule = "decimal-try-parse";

    private static readonly OwnedHandling[] OwnedHandlings =
    [
        new(TextComparisonKeyRule,
            @"\.Trim\(\)\s*\??\s*\.ToUpperInvariant\(\)",
            "Domain/Primitives/SourceText.cs",
            "SourceText.ComparisonKey",
            "文字的比較鍵（去頭尾空白後轉大寫）"),
        // 除數的名稱以 scale 結尾就算，例如 scale、moneyScale、project.MoneyScale。
        new(ScaledToDecimalRule,
            @"\(decimal\)[^;\n]*/\s*[\w.]*[Ss]cale\b",
            "Domain/Primitives/MoneyScaling.cs",
            "MoneyScaling.FromScaled",
            "把放大後的金額整數轉回十進位"),
        new(MidpointRoundingRule,
            @"\bMidpointRounding\b",
            "Domain/Primitives/MoneyScaling.cs",
            "MoneyScaling.TryToScaled 或 MoneyScaling.TryParseScaled",
            "金額的四捨五入方式"),
        new(DecimalTryParseRule,
            @"\bdecimal\.TryParse\(",
            "Domain/Primitives/MoneyScaling.cs",
            "MoneyScaling.TryParseAmount 或 MoneyScaling.TryParseScaled",
            "把文字解析成金額")
    ];

    /// <summary>字樣相同但不是在整理來源值的地方：哪一條、檔案、出現幾次、理由。次數不符就要重新檢查。</summary>
    private sealed record AllowedUse(string HandlingId, string File, int Count, string Reason);

    private static readonly AllowedUse[] AllowedUses =
    [
        new(MidpointRoundingRule, "AuditCore/JetAuditProgram.cs", 1,
            "金額分布的累計百分比取到顯示位數，算的是比例，不是金額。"),
        new(MidpointRoundingRule, "AuditCore/PrescreenProgram.cs", 1,
            "預篩選的占比取到小數一位供畫面顯示，算的是比例，不是金額。"),
        new(DecimalTryParseRule, "Domain/Primitives/DateNormalizer.cs", 2,
            "判斷日期欄的內容是不是純數字；純數字不當成日期，沒有把它當金額使用。"),
        new(DecimalTryParseRule, "Infrastructure/Export/WorkingPaperWriter.Step2To41.cs", 2,
            "寫底稿時把已經存進案件資料庫的數值文字寫成 Excel 的數值儲存格，要收科學記號，和來源金額的解析規則不同。")
    ];

    private static readonly string[] OwnerSourceMarkers =
    [
        "public static string Trim(string value)",
        "public static string ComparisonKey(string value)"
    ];

    private static readonly string[] MoneyOwnerSourceMarkers =
    [
        "public static bool TryParseAmount(string? text, out decimal value)",
        "public static bool TryToScaled(decimal value, int moneyScale, out long scaled)",
        "public static bool TryParseScaled(string? text, int moneyScale, out long scaled)",
        "public static decimal FromScaled(long scaled, int moneyScale)"
    ];

    [Fact]
    public void SourceValueHandling_IsWrittenOnlyInDomainPrimitives()
    {
        var product = ProductSources.ProductRoot;
        var violations = new List<string>();
        var allowedCounts = AllowedUses.ToDictionary(entry => entry, _ => 0);
        foreach (var file in Directory
                     .EnumerateFiles(product, "*.cs", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(product, file).Replace('\\', '/');
            if (relative.Split('/')[0] is "bin" or "obj")
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (var handling in OwnedHandlings)
            {
                if (relative == handling.OwnerFile)
                {
                    continue;
                }

                var allowance = AllowedUses.FirstOrDefault(entry =>
                    entry.HandlingId == handling.Id && entry.File == relative);
                foreach (var lineNumber in CodeLineNumbers(text, handling.Pattern))
                {
                    if (allowance is not null)
                    {
                        allowedCounts[allowance]++;
                        continue;
                    }

                    violations.Add($"{relative}:{lineNumber} 自己寫了「{handling.Meaning}」，應改用 {handling.Owner}");
                }
            }
        }

        violations.AddRange(allowedCounts
            .Where(entry => entry.Value != entry.Key.Count)
            .Select(entry =>
                $"允許清單和原始碼對不上：{entry.Key.File} 預期 {entry.Key.Count} 處、實際 {entry.Value} 處（{entry.Key.Reason}）"));

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void DomainPrimitives_DefineTheSourceValueHandlingTheOtherLayersCall()
    {
        var sourceTextPath = Path.Combine(ProductSources.ProductRoot, "Domain", "Primitives", "SourceText.cs");
        Assert.True(File.Exists(sourceTextPath), "找不到 Domain/Primitives/SourceText.cs");
        var sourceText = File.ReadAllText(sourceTextPath);
        foreach (var marker in OwnerSourceMarkers)
        {
            Assert.True(sourceText.Contains(marker, StringComparison.Ordinal), "SourceText 找不到 " + marker);
        }

        var moneyScaling = ProductSources.Product("Domain", "Primitives", "MoneyScaling.cs");
        foreach (var marker in MoneyOwnerSourceMarkers)
        {
            Assert.True(moneyScaling.Contains(marker, StringComparison.Ordinal), "MoneyScaling 找不到 " + marker);
        }
    }

    [Fact]
    public void Detector_FlagsSourceValueHandlingWrittenElsewhere()
    {
        (string Id, string Sample)[] samples =
        [
            (TextComparisonKeyRule, "var key = value.Trim().ToUpperInvariant();"),
            (TextComparisonKeyRule, "var key = value?.Trim()?.ToUpperInvariant();"),
            (TextComparisonKeyRule, "var key = value\n    .Trim()\n    .ToUpperInvariant();"),
            (ScaledToDecimalRule, "amount = (decimal)row.AmountScaled / scale,"),
            (ScaledToDecimalRule, "private static decimal Display(long scaled, int moneyScale) => (decimal)scaled / moneyScale;"),
            (ScaledToDecimalRule, "voucherTotal = (decimal)row.VoucherTotalScaled / project.MoneyScale }).ToArray();"),
            (MidpointRoundingRule, "var rounded = Math.Round(value * moneyScale, 0, MidpointRounding.ToEven);"),
            (DecimalTryParseRule, "if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))")
        ];

        foreach (var (id, sample) in samples)
        {
            var handling = OwnedHandlings.Single(entry => entry.Id == id);
            Assert.True(CodeLineNumbers(sample, handling.Pattern).Count == 1, "偵測不到：" + sample);
        }

        // 比例的計算與註解裡的字樣不算。
        Assert.Empty(CodeLineNumbers(
            "var percent = (decimal)hitLines * 100m / population;",
            OwnedHandlings.Single(entry => entry.Id == ScaledToDecimalRule).Pattern));
        Assert.Empty(CodeLineNumbers(
            "// 比對值用 value.Trim().ToUpperInvariant() 的結果",
            OwnedHandlings.Single(entry => entry.Id == TextComparisonKeyRule).Pattern));
    }

    /// <summary>樣式在原始碼裡每一次出現的行號；整行是註解的不算。樣式可以跨行，行號取開頭那一行。</summary>
    private static List<int> CodeLineNumbers(string text, string pattern)
    {
        var lineNumbers = new List<int>();
        foreach (Match match in Regex.Matches(text, pattern))
        {
            var lineStart = match.Index == 0 ? 0 : text.LastIndexOf('\n', match.Index - 1) + 1;
            var lineEnd = text.IndexOf('\n', match.Index);
            var line = text[lineStart..(lineEnd < 0 ? text.Length : lineEnd)];
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            lineNumbers.Add(text.AsSpan(0, match.Index).Count('\n') + 1);
        }

        return lineNumbers;
    }
}
