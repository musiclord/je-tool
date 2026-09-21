using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class FilterSideAndMonthConditionsTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task CalendarExceptions_BelongToEachDateField(string provider)
    {
        const string seed = """
            INSERT INTO target_gl_entry(batch_id,source_row_number,document_number,post_date,approval_date,
                account_code,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_effective) VALUES
            ('exceptions',1,'R1','2025-03-01','2025-03-02','A',100,100,0,'DEBIT',1),
            ('exceptions',2,'R2','2025-03-02','2025-03-01','A',100,100,0,'DEBIT',1),
            ('exceptions',3,'R3','2025-03-02','2025-03-02','A',100,100,0,'DEBIT',1),
            ('exceptions',4,'R4','2025-03-03','2025-03-03','A',100,100,0,'DEBIT',1);
            INSERT INTO staging_calendar_raw_day(day_type,date) VALUES ('makeup','2025-03-01'),('holiday','2025-03-03');
            """;
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, seed);
        var context = new FilterRuleContext(100, null, "2025-01-01", "2025-12-31");
        var cases = new (string Rules, string[] Hits)[]
        {
            ("""{"type":"fieldValue","field":"postDate","operator":"isNonBusinessDay"},{"type":"fieldValue","field":"postDate","operator":"notIn","values":["2025-03-03"]}""", ["R2", "R3"]),
            ("""{"type":"fieldValue","field":"docDate","operator":"isNonBusinessDay"},{"type":"fieldValue","field":"docDate","operator":"notIn","values":["2025-03-02"]}""", ["R4"]),
            ("""{"type":"fieldValue","field":"postDate","operator":"isNonBusinessDay"},{"type":"fieldValue","field":"postDate","operator":"notMonthStartDays","value":"2"}""", ["R4"])
        };
        foreach (var item in cases)
        {
            var scenario = FilterScenarioPayloadParser.Parse(JsonElement.Parse($$"""{"groups":[{"rules":[{{item.Rules}}]}]}"""), 100);
            var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario, context, CancellationToken.None);
            Assert.Equal(item.Hits, result.PreviewRows.Select(row => row.DocumentNumber).Order().ToArray());
        }
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task MonthWindows_SupportDateRangeLimitsAndWholeShortMonths(string provider)
    {
        const string seed = """
            INSERT INTO target_gl_entry(batch_id,source_row_number,document_number,post_date,approval_date,
                account_code,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_effective) VALUES
            ('bounds',1,'MIN','2025-01-01','0001-01-01','A',100,100,0,'DEBIT',1),
            ('bounds',2,'MAX','2025-01-01','9999-12-31','A',100,100,0,'DEBIT',1),
            ('bounds',3,'FEB','2025-01-01','2025-02-01','A',100,100,0,'DEBIT',1);
            """;
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, seed);
        var context = new FilterRuleContext(100, null, "2025-01-01", "2025-12-31");
        foreach (var item in new[] { (Days: "1", Hits: new[] { "MAX" }), (Days: "31", Hits: new[] { "FEB", "MAX", "MIN" }) })
        {
            var scenario = FilterCompletenessTests.Scenario($$"""{"type":"fieldValue","field":"docDate","operator":"monthEndDays","value":"{{item.Days}}"}""");
            var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario, context, CancellationToken.None);
            Assert.Equal(item.Hits, result.PreviewRows.Select(row => row.DocumentNumber).Order().ToArray());
        }
    }

    [Theory]
    [InlineData("Debit")]
    [InlineData("both")]
    [InlineData(" debit ")]
    [InlineData("")]
    public void UnknownDirection_IsRejected(string direction)
    {
        var scenario = FilterCompletenessTests.Scenario(JsonSerializer.Serialize(new
        { type = "fieldValue", field = "accNum", @operator = "equals", value = "A", drCr = direction }));
        Assert.Contains(FilterScenarioValidator.Validate(scenario, new(false, true, true)), message => message.Contains("分錄方向"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("true")]
    public void NonStringDirection_IsNotSilentlyTreatedAsUnrestricted(string value)
    {
        var error = Assert.Throws<JetActionException>(() => FilterCompletenessTests.Scenario(
            $$"""{"type":"fieldValue","field":"accNum","operator":"equals","value":"A","drCr":{{value}}}"""));
        Assert.Equal(JetErrorCodes.InvalidScenario, error.Code);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task SameVoucher_AccountAndSideMustMatchTheSameEntry(string provider)
    {
        const string seed = """
            INSERT INTO target_gl_entry(batch_id,source_row_number,document_number,line_item,post_date,
                account_code,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_effective) VALUES
            ('side',1,'V1','1','2025-01-01','A',1000,1000,0,'DEBIT',1),
            ('side',2,'V1','2','2025-01-01','B',2000,2000,0,'DEBIT',1),
            ('side',3,'V1','3','2025-01-01','X',-3000,0,3000,'CREDIT',1),
            ('side',4,'V2','1','2025-01-01','A',1000,1000,0,'DEBIT',1),
            ('side',5,'V2','2','2025-01-01','B',-1000,0,1000,'CREDIT',1),
            ('side',6,'V3','1','2025-01-01','B',1000,1000,0,'DEBIT',1),
            ('side',7,'V3','2','2025-01-01','A',-1000,0,1000,'CREDIT',1),
            ('side',8,'V4','1','2025-01-01','A',1000,1000,0,'DEBIT',1),
            ('side',9,'V4','2','2025-01-01','B',-800,0,800,'CREDIT',1),
            ('side',10,'V4','3','2025-01-01','X',-200,0,200,'CREDIT',1),
            ('side',11,'V5','1','2025-01-01','A',0,0,0,'DEBIT',1),
            ('side',12,'V5','2','2025-01-01','B',-1000,0,1000,'CREDIT',0),
            ('side',13,'V5','3','2026-01-01','B',-1000,0,1000,'CREDIT',0);
            """;
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, seed);
        var context = new FilterRuleContext(100, null, "2025-01-01", "2025-12-31");
        var scenario = FilterScenarioPayloadParser.Parse(JsonElement.Parse("""
            {"name":"指定借貸科目","rationale":"固定答案","groups":[{"matchScope":"sameVoucher","rules":[
              {"type":"fieldValue","field":"accNum","operator":"in","values":["A"],"drCr":"debit"},
              {"type":"fieldValue","field":"accNum","operator":"in","values":["B"],"drCr":"credit"}]}]}
            """), 100);
        Assert.Empty(FilterScenarioValidator.Validate(scenario, new(false, true, true) { MoneyScale = 100 }));
        var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario, context, CancellationToken.None);
        Assert.Equal(["V2|1", "V4|1"], result.PreviewRows.Select(row => row.DocumentNumber + "|" + row.LineItem).Order().ToArray());
        // Negation still applies to the account value on this side, not to the presence of the other side.
        var negative = FilterCompletenessTests.Scenario("""
            {"type":"fieldValue","field":"accNum","operator":"notIn","values":["B"],"drCr":"credit"}
            """);
        var negativeResult = await fixture.Repository.PreviewAsync(fixture.ProjectId, negative, context, CancellationToken.None);
        Assert.Equal(["V1|3", "V3|2", "V4|3"], negativeResult.PreviewRows.Select(row => row.DocumentNumber + "|" + row.LineItem).Order().ToArray());
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task MonthWindows_UseEachSelectedDateMonthAndExplicitBlankPolicy(string provider)
    {
        const string extraId = "rde.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string seed = """
            INSERT INTO target_gl_entry(batch_id,source_row_number,document_number,post_date,approval_date,
                account_code,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_effective) VALUES
            ('month',1,'R1','2025-01-01','2024-02-27','A',100,100,0,'DEBIT',1),
            ('month',2,'R2','2025-01-01','2024-02-28','A',100,100,0,'DEBIT',1),
            ('month',3,'R3','2025-01-01','2024-02-29','A',100,100,0,'DEBIT',1),
            ('month',4,'R4','2025-01-01','2025-02-27','A',100,100,0,'DEBIT',1),
            ('month',5,'R5','2025-01-01','2025-02-28','A',100,100,0,'DEBIT',1),
            ('month',6,'R6','2025-01-01','2025-04-29','A',100,100,0,'DEBIT',1),
            ('month',7,'R7','2025-01-01','2025-05-29','A',100,100,0,'DEBIT',1),
            ('month',8,'R8','2025-01-01','2025-05-01','A',100,100,0,'DEBIT',1),
            ('month',9,'R9','2025-01-01',NULL,'A',-100,0,100,'CREDIT',1);
            INSERT INTO target_gl_rde_value(entry_id,field_id,value_type,date_value)
            SELECT entry_id,'rde.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','date',approval_date
            FROM target_gl_entry WHERE approval_date IS NOT NULL;
            """;
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, seed);
        var extra = new GlRdeFieldMetadata(extraId, "sourceDate", "合成日期", "date");
        var context = new FilterRuleContext(100, null, "2025-01-01", "2025-12-31") { RdeFields = [extra] };
        var cases = new (string Op, string[] Hits)[]
        {
            ("monthEndDays", ["R2", "R3", "R4", "R5", "R6"]),
            ("notMonthEndDays", ["R1", "R7", "R8"]),
            ("monthStartDays", ["R8"]),
            ("notMonthStartDays", ["R1", "R2", "R3", "R4", "R5", "R6", "R7"])
        };
        foreach (var useExtra in new[] { false, true })
        foreach (var blank in new[] { false, true })
        foreach (var item in cases)
        {
            var payload = new Dictionary<string, object?> { ["type"] = "fieldValue", ["operator"] = item.Op,
                ["value"] = "2", ["includeBlank"] = blank };
            payload[useExtra ? "fieldId" : "field"] = useExtra ? extraId : "docDate";
            var scenario = FilterCompletenessTests.Scenario(JsonSerializer.Serialize(payload));
            Assert.Empty(FilterScenarioValidator.Validate(scenario, new(false, true, true) { MoneyScale = 100, RdeFields = [extra] }));
            var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario, context, CancellationToken.None);
            Assert.Equal(blank ? item.Hits.Append("R9").Order().ToArray() : item.Hits,
                result.PreviewRows.Select(row => row.DocumentNumber).Order().ToArray());
        }
        foreach (var rule in new[]
        {
            """{"type":"fieldValue","field":"docDate","operator":"isBlank","drCr":"debit"}""",
            """{"type":"fieldValue","field":"docDate","operator":"monthEndDays","value":"2","includeBlank":true,"drCr":"debit"}"""
        })
        {
            var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, FilterCompletenessTests.Scenario(rule), context, CancellationToken.None);
            Assert.DoesNotContain(result.PreviewRows, row => row.DocumentNumber == "R9");
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("32")]
    [InlineData("1.5")]
    [InlineData("")]
    [InlineData("2025-01-01")]
    public void MonthWindow_InvalidDaysAreActionable(string days)
    {
        var scenario = FilterCompletenessTests.Scenario(JsonSerializer.Serialize(new
        { type = "fieldValue", field = "postDate", @operator = "monthEndDays", value = days }));
        Assert.Contains(FilterScenarioValidator.Validate(scenario, new(false, true, true)), message => message.Contains("1 到 31"));
    }
}
