using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class PrivateCaseScenarioDiagnosticsTests
{
    [Fact]
    public async Task ExplicitVoucherWitnesses_PreserveSameRowAndTextDiagnostics()
    {
        var scenarios = Element("""
            [{"groups":[{"matchScope":"row","conditions":[
              {"type":"drCrOnly","drCr":"credit"},
              {"type":"voucher","quantifier":"any","conditions":[{"type":"prescreen","prescreenKey":"blankDescription"}]},
              {"type":"voucher","quantifier":"any","conditions":[{"type":"textSet","field":"description","mode":"contains","values":["alpha"]}]}]}]}]
            """);
        var previews = 0;
        Task<JsonElement> Dispatch(string action, string payload)
        {
            if (action == "query.highRiskSummaryRowPage") return Task.FromResult(Element("""
                {"rows":[{"voucherNumber":"synthetic","description":null,"matchedPositions":[1]},
                 {"voucherNumber":"synthetic","description":"alpha","matchedPositions":[]}],"nextCursor":null}
                """));
            Assert.Equal("filter.preview", action);
            var rules = Element(payload).GetProperty("scenario").GetProperty("groups")[0].GetProperty("conditions");
            Assert.Equal(new[] { "drCrOnly", "prescreen", "textSet" }, rules.EnumerateArray().Select(r => r.GetProperty("type").GetString()));
            previews++;
            return Task.FromResult(Element("""{"scenario":{"count":3,"voucherCount":2}}"""));
        }
        var facts = Assert.Single((await PrivateCaseScenarioDiagnostics.CaptureAsync(scenarios, [], Dispatch)).Counts);
        Assert.Equal(1, previews);
        Assert.Equal(3, facts.SameRowRuleMatchRowCount);
        Assert.Equal(2, facts.SameRowRuleMatchVoucherCount);
        Assert.Equal(2, facts.ExpandedRowsWithLegacyTextMatch);
        Assert.Equal(1, facts.VouchersWithLegacyTextMatch);
        Assert.Equal(2, facts.ExpandedRowsWhoseDirectHitHasNullOrEmptyDescription);
        Assert.Equal(1, facts.VouchersWhoseDirectHitHasNullOrEmptyDescription);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureAsync_NoScenarioHits_PreservesZeroCountsForEveryPosition(bool hasUnmatchedRow)
    {
        using var scenarios = JsonDocument.Parse(
            """
            [
              {"groups":[{"conditions":[{"type":"prescreen","prescreenKey":"blankDescription"}]}]},
              {"groups":[{"conditions":[{"type":"textSet","field":"description","mode":"contains","values":["synthetic-match"]}]}]}
            ]
            """);
        var queryCount = 0;

        Task<JsonElement> Dispatch(string action, string payload)
        {
            Assert.Equal("query.highRiskSummaryRowPage", action);
            queryCount++;
            return Task.FromResult(Element(hasUnmatchedRow
                ? """{"rows":[{"voucherNumber":"synthetic-unmatched","description":"ordinary","matchedPositions":[]}],"nextCursor":null}"""
                : """{"rows":[],"nextCursor":null}"""));
        }

        var result = await PrivateCaseScenarioDiagnostics.CaptureAsync(scenarios.RootElement, [], Dispatch);

        Assert.Equal(1, queryCount);
        Assert.Equal(2, result.Counts.Count);
        Assert.Equal([1, 2], result.Counts.Select(count => count.Position));
        Assert.All(result.Counts, count =>
        {
            Assert.Equal(0L, count.ExpandedVoucherRowCount);
            Assert.Equal(0L, count.ExpandedVoucherCount);
            Assert.Null(count.SameRowRuleMatchRowCount);
            Assert.Null(count.WeekendIncludingMakeupRowCount);
            Assert.Empty(result.SelectedVoucherNumbersByPosition[count.Position]);
        });
        Assert.Equal(0L, result.Counts[0].ExpandedRowsWhoseDirectHitHasNullOrEmptyDescription);
        Assert.Equal(0L, result.Counts[0].VouchersWhoseDirectHitHasNullOrEmptyDescription);
        Assert.Null(result.Counts[0].ExpandedRowsWithLegacyTextMatch);
        Assert.Equal(0L, result.Counts[1].ExpandedRowsWithLegacyTextMatch);
        Assert.Equal(0L, result.Counts[1].VouchersWithLegacyTextMatch);
        Assert.Null(result.Counts[1].ExpandedRowsWhoseDirectHitHasNullOrEmptyDescription);
    }

    [Fact]
    public async Task CaptureAsync_UsesProductQueriesButRetainsOnlyAggregateCounts()
    {
        using var scenarios = JsonDocument.Parse(
            """
            [
              {"groups":[{"matchScope":"sameVoucher","conditions":[{"type":"prescreen","prescreenKey":"unexpectedAccountPair"},{"type":"prescreen","prescreenKey":"blankDescription"}]}]},
              {"groups":[{"conditions":[{"type":"prescreen","prescreenKey":"blankDescription"}]}]},
              {"groups":[{"conditions":[{"type":"prescreen","prescreenKey":"weekendPosting"}]}]}
            ]
            """);
        var actions = new List<string>();

        async Task<JsonElement> Dispatch(string action, string payload)
        {
            actions.Add(action);
            await Task.Yield();
            return action switch
            {
                "query.highRiskSummaryRowPage" => Element(
                    """
                    {
                      "rows":[
                        {"voucherNumber":"synthetic-1","description":"first","matchedPositions":[1]},
                        {"voucherNumber":"synthetic-1","description":"second","matchedPositions":[]},
                        {"voucherNumber":"synthetic-2","description":null,"matchedPositions":[2]},
                        {"voucherNumber":"synthetic-2","description":"filled","matchedPositions":[]},
                        {"voucherNumber":"synthetic-3","description":"direct hit","matchedPositions":[2]},
                        {"voucherNumber":"synthetic-3","description":null,"matchedPositions":[]},
                        {"voucherNumber":"synthetic-4","description":"weekend","matchedPositions":[3]}
                      ],
                      "nextCursor":null
                    }
                    """),
                "filter.preview" => Element(
                    """{"scenario":{"count":9,"voucherCount":4}}"""),
                _ => throw new InvalidOperationException("unexpected synthetic action"),
            };
        }

        var result = await PrivateCaseScenarioDiagnostics.CaptureAsync(
            scenarios.RootElement,
            [new DateOnly(2025, 1, 4)],
            Dispatch);

        Assert.Equal(3, result.Counts.Count);
        Assert.Equal((2L, 1L), (
            result.Counts[0].ExpandedVoucherRowCount,
            result.Counts[0].ExpandedVoucherCount));
        Assert.Equal(
            2L,
            result.Counts[1].ExpandedRowsWhoseDirectHitHasNullOrEmptyDescription);
        Assert.Equal(
            1L,
            result.Counts[1].VouchersWhoseDirectHitHasNullOrEmptyDescription);
        Assert.Equal(9L, result.Counts[2].WeekendIncludingMakeupRowCount);
        Assert.Equal(4L, result.Counts[2].WeekendIncludingMakeupVoucherCount);
        Assert.Single(result.SelectedVoucherNumbersByPosition[1]);
        Assert.Equal(2, result.SelectedVoucherNumbersByPosition[2].Count);
        Assert.Equal(9L, result.Counts[0].SameRowRuleMatchRowCount);
        Assert.Equal(4L, result.Counts[0].SameRowRuleMatchVoucherCount);
        Assert.Contains("query.highRiskSummaryRowPage", actions);
        Assert.Contains("filter.preview", actions);

        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("synthetic-1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("2025-01-04", json, StringComparison.Ordinal);
        Assert.StartsWith("private case scenario diagnostics", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureAsync_MalformedProductEvidence_IsRejectedWithoutEchoingValues()
    {
        using var scenarios = JsonDocument.Parse(
            """[{"groups":[{"conditions":[{"type":"prescreen","prescreenKey":"blankDescription"}]}]}]""");

        var exception = await Assert.ThrowsAsync<PrivateCaseScenarioDiagnosticException>(() =>
            PrivateCaseScenarioDiagnostics.CaptureAsync(
                scenarios.RootElement,
                [],
                (_, _) => Task.FromResult(Element("""{"rows":[]}"""))));

        Assert.Equal(PrivateCaseScenarioDiagnosticFailure.InvalidEvidence, exception.Failure);
        Assert.Equal("contract", exception.Stage);
        Assert.Equal("contract", exception.CauseType);
        Assert.DoesNotContain("rows", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CaptureAsync_ProductError_RetainsOnlyStageAndRegisteredCode()
    {
        using var scenarios = JsonDocument.Parse(
            """[{"groups":[{"matchScope":"sameVoucher","conditions":[{"type":"prescreen","prescreenKey":"blankDescription"}]}]}]""");

        var exception = await Assert.ThrowsAsync<PrivateCaseScenarioDiagnosticException>(() =>
            PrivateCaseScenarioDiagnostics.CaptureAsync(
                scenarios.RootElement,
                [],
                (action, _) => action == "query.highRiskSummaryRowPage"
                    ? Task.FromResult(Element("""{"rows":[],"nextCursor":null}"""))
                    : throw new JetActionException(
                        "synthetic_code",
                        "private product message")));

        Assert.Equal("same-row-1", exception.Stage);
        Assert.Equal("JetActionException:synthetic_code", exception.CauseType);
        Assert.DoesNotContain("private product message", exception.Message, StringComparison.Ordinal);
    }

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
