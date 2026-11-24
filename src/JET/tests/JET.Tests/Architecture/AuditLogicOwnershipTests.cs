using System.Text.RegularExpressions;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 審計邏輯只寫在 AuditCore 與 Domain。Application、Bridge、Infrastructure 與主程式的查詢要用到這些邏輯時，
/// 呼叫 AuditCore 提供的條件，不自己再寫一份：完整性測試的差異條件、分錄是否納入測試、分錄有沒有傳票號碼、
/// 傳票有沒有命中的分錄、人員的識別值、資料驗證報告借貸淨額的顯示門檻，以及集中度分析的排序與候選列數。
/// </summary>
public sealed class AuditLogicOwnershipTests
{
    /// <summary>不得出現在 AuditCore 與 Domain 以外的寫法、定義這段邏輯的 AuditCore 成員，以及這段邏輯的意思。</summary>
    private sealed record OwnedLogic(string Pattern, string Owner, string Meaning);

    private static readonly OwnedLogic[] OwnedLogics =
    [
        new(@"tb_s\s*<>\s*gl_s", "ValidationProcedures.CompletenessDifferencePredicate", "完整性測試的差異條件"),
        new(@"not_in_tb\s*=\s*1", "ValidationProcedures.GlOnlyAccountPredicate", "GL 有分錄而 TB 沒有的科目"),
        new(@"is_excluded\s*=\s*[01]", "GlExcludedPopulation.SqlPredicate 或 ExcludedSqlPredicate", "分錄是否納入測試"),
        new(@"voucher_number\s+IS\s+(NOT\s+)?NULL", "HitVoucherRows", "分錄有沒有傳票號碼"),
        new(@"MAX\(is_hit\)|is_hit\s*=\s*1", "HitVoucherRows", "傳票有沒有命中的分錄"),
        new(@"Upper\((database\.)?[Dd]ialect\.Trim\(|Dialect\.Upper\(person\)", "PersonIdentity", "人員的識別值"),
        new(@">\s*0\.01m", "ValidationProcedures.DisplayedNetDifference", "資料驗證報告借貸淨額的顯示門檻"),
        new(@"ORDER BY COUNT\(\*\) (ASC|DESC)|SummaryRowLimit", "ConcentrationLimits", "集中度分析的排序與候選列數"),
        new(@"semantic_role IN \(@receivables", "GlRulePredicates.RevenueCounterpartRoles", "收入的一般對方科目分類")
    ];

    /// <summary>字樣相同但不是查詢條件的地方：檔案、那一行一定出現的片段、理由。</summary>
    private sealed record AllowedLine(string File, string Marker, string Reason);

    private static readonly AllowedLine[] AllowedLines =
    [
        new("Application/Support/FilterConditionSqlRenderer.cs",
            "is_excluded = 0：納入測試的分錄。",
            "第五步「資料庫條件式」旁的欄位說明文字，不是查詢條件。")
    ];

    private static readonly string[] OwnerSourceMarkers =
    [
        "public const string CompletenessDifferencePredicate = \"tb_s <> gl_s\";",
        "public const string GlOnlyAccountPredicate = \"not_in_tb = 1\";",
        "internal static string ExcludedSqlPredicate(",
        "internal static class HitVoucherRows",
        "internal static class PersonIdentity",
        "internal static decimal DisplayedNetDifference(",
        "internal const int CandidateRows = 50;",
        "internal static IReadOnlyList<string> RevenueCounterpartRoles"
    ];

    [Fact]
    public void AuditLogic_IsWrittenOnlyInAuditCoreAndDomain()
    {
        var product = ProductSources.ProductRoot;
        var violations = new List<string>();
        var usedAllowances = new HashSet<AllowedLine>();
        foreach (var file in Directory
                     .EnumerateFiles(product, "*.cs", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(product, file).Replace('\\', '/');
            var top = relative.Split('/')[0];
            if (top is "bin" or "obj" or "AuditCore" or "Domain")
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var logic in OwnedLogics)
                {
                    if (!Regex.IsMatch(line, logic.Pattern))
                    {
                        continue;
                    }

                    var allowance = AllowedLines.FirstOrDefault(entry =>
                        entry.File == relative && line.Contains(entry.Marker, StringComparison.Ordinal));
                    if (allowance is not null)
                    {
                        usedAllowances.Add(allowance);
                        continue;
                    }

                    violations.Add($"{relative}:{index + 1} 自己寫了「{logic.Meaning}」，應改用 AuditCore 的 {logic.Owner}");
                }
            }
        }

        violations.AddRange(AllowedLines
            .Where(entry => !usedAllowances.Contains(entry))
            .Select(entry => $"允許清單過期，找不到對應的那一行：{entry.File} 的「{entry.Marker}」"));

        Assert.Empty(violations);
    }

    [Fact]
    public void AuditCore_DefinesTheAuditLogicTheOtherLayersCall()
    {
        var auditCore = string.Join(
            "\n",
            Directory
                .EnumerateFiles(Path.Combine(ProductSources.ProductRoot, "AuditCore"), "*.cs", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.Ordinal)
                .Select(File.ReadAllText));

        foreach (var marker in OwnerSourceMarkers)
        {
            Assert.True(auditCore.Contains(marker, StringComparison.Ordinal), "AuditCore 找不到 " + marker);
        }
    }

    [Fact]
    public void Detector_FlagsAuditLogicWrittenOutsideAuditCore()
    {
        string[] samples =
        [
            "SELECT COUNT(*) FROM diff WHERE tb_s <> gl_s;",
            "SELECT account_number FROM diff WHERE not_in_tb = 1",
            "FROM gl WHERE is_excluded = 1;",
            "WHERE g2.voucher_number IS NOT NULL AND",
            "WHERE g.voucher_number IS NULL AND",
            "GROUP BY voucher_number HAVING MAX(is_hit) = 1) v",
            "var personKey = dialect.Upper(dialect.Trim(\"ap.name\"));",
            "cells.SetNumber(\"E10\", netDifference > 0.01m ? netDifference : 0m);",
            "ORDER BY COUNT(*) ASC, account_number",
            "WHERE t.semantic_role IN (@receivables, @cash, @receiptInAdvance)),"
        ];

        foreach (var sample in samples)
        {
            Assert.True(
                OwnedLogics.Any(logic => Regex.IsMatch(sample, logic.Pattern)),
                "偵測不到：" + sample);
        }
    }
}
