using System.Text.RegularExpressions;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 驗證結果可用性的 frontend mirror：前端只讀 validate.run 的 backend eligibility，
/// 不得自行重算 part A／B，也不得讓缺少 additive 欄位的舊摘要放行。
/// </summary>
public sealed class CompletenessEligibilityFrontendTests
{
    [Fact]
    public void StepGate_UsesBackendEligibility_AllowsAdvancedFilterWithoutPrescreen_AndDoesNotRecomputeRawFacts()
    {
        var core = ReadFrontend("js", "ui-core.js");
        var eligibility = ExtractFunction(core, "completenessEligibility");
        var stepGate = ExtractFunction(core, "stepGate");

        Assert.Contains("validation.completenessTest.eligibility", eligibility, StringComparison.Ordinal);
        Assert.Contains("eligibility.isEligible !== true", eligibility, StringComparison.Ordinal);
        Assert.Contains("COMPLETENESS_RERUN_REASON", eligibility, StringComparison.Ordinal);
        Assert.Contains("eligibility.reason", eligibility, StringComparison.Ordinal);
        Assert.DoesNotContain("rowCountMatch", eligibility, StringComparison.Ordinal);
        Assert.DoesNotContain("amountMatch", eligibility, StringComparison.Ordinal);
        Assert.DoesNotContain("diffAccountCount", eligibility, StringComparison.Ordinal);
        Assert.DoesNotContain("naReason", eligibility, StringComparison.Ordinal);

        Assert.Contains("completenessEligibility(state.lastRuns.validate)", stepGate, StringComparison.Ordinal);
        Assert.Contains("completeness.reason", stepGate, StringComparison.Ordinal);
        Assert.DoesNotContain("else if (!state.lastRuns.prescreen)", stepGate, StringComparison.Ordinal);
        Assert.DoesNotContain("prescreenRunId", stepGate, StringComparison.Ordinal);
        Assert.DoesNotContain("rowCountMatch", stepGate, StringComparison.Ordinal);
        Assert.DoesNotContain("amountMatch", stepGate, StringComparison.Ordinal);
        Assert.DoesNotContain("diffAccountCount", stepGate, StringComparison.Ordinal);
        Assert.DoesNotContain("naReason", stepGate, StringComparison.Ordinal);

        Assert.DoesNotContain("需先完成驗證與預篩選", core, StringComparison.Ordinal);
        Assert.Contains("4: '需先完成資料驗證'", core, StringComparison.Ordinal);

        var app = ReadFrontend("js", "app.js");
        Assert.DoesNotContain("'可進入預篩選'", app, StringComparison.Ordinal);
        Assert.Contains("'可執行預篩選或進階條件篩選'", app, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidationCardAndPrescreenButton_SeparateWarningsFromAvailability()
    {
        var validate = ReadFrontend("js", "steps", "validate-step.js");
        var prescreenCard = ExtractFunction(validate, "prescreenCardHtml");

        Assert.True(
            Regex.Matches(validate, @"Ui\.completenessEligibility\(v\)").Count >= 3,
            "完整性卡、詳情與 render 必須共用 ui-core 的 backend eligibility mirror。");
        Assert.Contains("eligibility.isEligible", prescreenCard, StringComparison.Ordinal);
        Assert.Contains("rule-card__gate", prescreenCard, StringComparison.Ordinal);
        Assert.Contains("Ui.esc(eligibility.reason)", prescreenCard, StringComparison.Ordinal);
        Assert.Contains("var canRun = ready && eligibility.isEligible", prescreenCard, StringComparison.Ordinal);
        Assert.Contains("(canRun ? '' : ' disabled')", prescreenCard, StringComparison.Ordinal);
        Assert.DoesNotContain("rowCountMatch", prescreenCard, StringComparison.Ordinal);
        Assert.DoesNotContain("amountMatch", prescreenCard, StringComparison.Ordinal);
        Assert.DoesNotContain("diffAccountCount", prescreenCard, StringComparison.Ordinal);
        Assert.DoesNotContain("naReason", prescreenCard, StringComparison.Ordinal);
        // 2026-09-17 裁定：可繼續操作仍須顯示差異，取代舊的禁止非阻擋提示斷言。
        Assert.Contains("eligibility.warning", validate, StringComparison.Ordinal);
        // C8 adds the display-precision explanation alongside, not instead of, the backend warning.
        // First failure: 20261004-091831760-a762b9ef96db4d61a4bb2bff7cdb0022.
        Assert.Contains("prefix: [eligibility.reason || eligibility.warning, differencePrecisionNote].filter(Boolean).join(' ')", validate, StringComparison.Ordinal);
        Assert.Contains("text: eligibility.reason || eligibility.warning", validate, StringComparison.Ordinal);
    }

    [Fact]
    public void PrescreenAction_StopsBeforeBackendCallWhenBackendEligibilityIsIneligible()
    {
        var binding = ExtractFunction(
            ReadFrontend("js", "steps", "validate-step.js"),
            "bind");

        Assert.Contains("Ui.completenessEligibility", binding, StringComparison.Ordinal);
        Assert.Contains("if (!eligibility.isEligible)", binding, StringComparison.Ordinal);
        Assert.DoesNotContain("rowCountMatch", binding, StringComparison.Ordinal);
        Assert.DoesNotContain("amountMatch", binding, StringComparison.Ordinal);
        Assert.DoesNotContain("diffAccountCount >", binding, StringComparison.Ordinal);
    }

    [Fact]
    public void UiRun_MapsCompletenessPrerequisiteFailure_AndPreservesBackendDetail()
    {
        var run = ExtractFunction(ReadFrontend("js", "ui-core.js"), "run");

        Assert.Contains(
            "error.code === 'completeness_prerequisite_failed'",
            run,
            StringComparison.Ordinal);
        Assert.Contains("已阻擋：", run, StringComparison.Ordinal);
        Assert.Contains("error.message", run, StringComparison.Ordinal);
    }

    [Fact]
    public void StepFooter_EscapesDynamicBackendGateReason()
    {
        var footer = ExtractFunction(ReadFrontend("js", "ui-core.js"), "stepFooterHtml");

        Assert.Contains("esc(gate.missing.join('、'))", footer, StringComparison.Ordinal);
        Assert.DoesNotContain("+ gate.missing.join('、') +", footer, StringComparison.Ordinal);
    }

    private static string ExtractFunction(string source, string name)
    {
        var start = source.IndexOf($"function {name}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到 {name} 函式。");

        var depth = 0;
        var opened = false;
        for (var index = start; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
                opened = true;
            }
            else if (source[index] == '}' && opened && --depth == 0)
            {
                return source[start..(index + 1)];
            }
        }

        throw new InvalidOperationException($"找不到 {name} 函式結尾。");
    }

    private static string ReadFrontend(params string[] segments)
        => File.ReadAllText(
            Path.Combine(new[] { RepoRoot(), "JET", "wwwroot" }.Concat(segments).ToArray()));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET.slnx。");
    }
}
