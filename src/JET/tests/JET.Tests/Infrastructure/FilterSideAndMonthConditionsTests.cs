using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class FilterSideAndMonthConditionsTests
{
    [Theory]
    [InlineData("sqlite")]
    public async Task CalendarExceptions_BelongToEachDateField(string provider)
    {
        const string seed = """
            INSERT INTO gl(batch_id,source_row_number,voucher_number,gl_date,approval_date,
                account_number,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_excluded) VALUES
            ('exceptions',1,'R1','2025-03-01','2025-03-02','A',100,100,0,'DEBIT',0),
            ('exceptions',2,'R2','2025-03-02','2025-03-01','A',100,100,0,'DEBIT',0),
            ('exceptions',3,'R3','2025-03-02','2025-03-02','A',100,100,0,'DEBIT',0),
            ('exceptions',4,'R4','2025-03-03','2025-03-03','A',100,100,0,'DEBIT',0);
            INSERT INTO calendar_day(day_type,date) VALUES ('makeup','2025-03-01'),('holiday','2025-03-03');
            """;
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, seed);
        var context = new FilterConditionContext(100, null, "2025-01-01", "2025-12-31");
        var cases = new (string Rules, string[] Hits)[]
        {
            ("""{"type":"fieldValue","field":"glDate","operator":"isNonBusinessDay"},{"type":"fieldValue","field":"glDate","operator":"notIn","values":["2025-03-03"]}""", ["R2", "R3"]),
            ("""{"type":"fieldValue","field":"approvalDate","operator":"isNonBusinessDay"},{"type":"fieldValue","field":"approvalDate","operator":"notIn","values":["2025-03-02"]}""", ["R4"]),
            ("""{"type":"fieldValue","field":"glDate","operator":"isNonBusinessDay"},{"type":"fieldValue","field":"glDate","operator":"notMonthStartDays","value":"2"}""", ["R4"])
        };
        foreach (var item in cases)
        {
            var scenario = FilterScenarioPayloadParser.Parse(JsonElement.Parse($$"""{"groups":[{"conditions":[{{item.Rules}}]}]}"""), 100);
            var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario, context, CancellationToken.None);
            Assert.Equal(item.Hits, result.PreviewRows.Select(row => row.VoucherNumber).Order().ToArray());
        }
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task MonthWindows_SupportDateRangeLimitsAndWholeShortMonths(string provider)
    {
        const string seed = """
            INSERT INTO gl(batch_id,source_row_number,voucher_number,gl_date,approval_date,
                account_number,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_excluded) VALUES
            ('bounds',1,'MIN','2025-01-01','0001-01-01','A',100,100,0,'DEBIT',0),
            ('bounds',2,'MAX','2025-01-01','9999-12-31','A',100,100,0,'DEBIT',0),
            ('bounds',3,'FEB','2025-01-01','2025-02-01','A',100,100,0,'DEBIT',0);
            """;
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, seed);
        var context = new FilterConditionContext(100, null, "2025-01-01", "2025-12-31");
        foreach (var item in new[] { (Days: "1", Hits: new[] { "MAX" }), (Days: "31", Hits: new[] { "FEB", "MAX", "MIN" }) })
        {
            var scenario = FilterCompletenessTests.Scenario($$"""{"type":"fieldValue","field":"approvalDate","operator":"monthEndDays","value":"{{item.Days}}"}""");
            var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario, context, CancellationToken.None);
            Assert.Equal(item.Hits, result.PreviewRows.Select(row => row.VoucherNumber).Order().ToArray());
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
        { type = "fieldValue", field = "accountNumber", @operator = "equals", value = "A", drCr = direction }));
        // 分錄方向的值不在允許範圍只會是內容損毀，畫面改寫通用句，改鎖原因代號。
        var errors = FilterScenarioValidator.Validate(scenario, new(false, true, true));
        Assert.Contains("第 1 組第 1 條：此條件內容有誤，無法儲存。請刪除此條件後重新設定。", errors);
        Assert.Contains(new FilterScenarioCorruptReason(1, 1, "field_value_side_invalid"), FilterScenarioCorruption.ReasonsOf(errors));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("true")]
    public void NonStringDirection_IsNotSilentlyTreatedAsUnrestricted(string value)
    {
        var error = Assert.Throws<JetActionException>(() => FilterCompletenessTests.Scenario(
            $$"""{"type":"fieldValue","field":"accountNumber","operator":"equals","value":"A","drCr":{{value}}}"""));
        Assert.Equal(JetErrorCodes.InvalidScenario, error.Code);
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task SameVoucher_AccountAndSideMustMatchTheSameEntry(string provider)
    {
        const string seed = """
            INSERT INTO gl(batch_id,source_row_number,voucher_number,line_item,gl_date,
                account_number,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_excluded) VALUES
            ('side',1,'V1','1','2025-01-01','A',1000,1000,0,'DEBIT',0),
            ('side',2,'V1','2','2025-01-01','B',2000,2000,0,'DEBIT',0),
            ('side',3,'V1','3','2025-01-01','X',-3000,0,3000,'CREDIT',0),
            ('side',4,'V2','1','2025-01-01','A',1000,1000,0,'DEBIT',0),
            ('side',5,'V2','2','2025-01-01','B',-1000,0,1000,'CREDIT',0),
            ('side',6,'V3','1','2025-01-01','B',1000,1000,0,'DEBIT',0),
            ('side',7,'V3','2','2025-01-01','A',-1000,0,1000,'CREDIT',0),
            ('side',8,'V4','1','2025-01-01','A',1000,1000,0,'DEBIT',0),
            ('side',9,'V4','2','2025-01-01','B',-800,0,800,'CREDIT',0),
            ('side',10,'V4','3','2025-01-01','X',-200,0,200,'CREDIT',0),
            ('side',11,'V5','1','2025-01-01','A',0,0,0,'DEBIT',0),
            ('side',12,'V5','2','2025-01-01','B',-1000,0,1000,'CREDIT',1),
            ('side',13,'V5','3','2026-01-01','B',-1000,0,1000,'CREDIT',1);
            """;
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, seed);
        var context = new FilterConditionContext(100, null, "2025-01-01", "2025-12-31");
        var scenario = FilterScenarioPayloadParser.Parse(JsonElement.Parse("""
            {"name":"指定借貸科目","rationale":"固定答案","groups":[{"conditions":[
              {"type":"group","conditions":[{"type":"drCrOnly","drCr":"debit"},{"type":"fieldValue","field":"accountNumber","operator":"in","values":["A"]}]},
              {"type":"voucher","quantifier":"any","conditions":[{"type":"drCrOnly","drCr":"credit"},{"type":"fieldValue","field":"accountNumber","operator":"in","values":["B"]}]}]}]}
            """), 100);
        Assert.Empty(FilterScenarioValidator.Validate(scenario, new(false, true, true) { MoneyScale = 100 }));
        var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario, context, CancellationToken.None);
        Assert.Equal(["V2|1", "V4|1"], result.PreviewRows.Select(row => row.VoucherNumber + "|" + row.LineItem).Order().ToArray());
        // Negation still applies to the account value on this side, not to the presence of the other side.
        var negative = FilterCompletenessTests.Scenario("""
            {"type":"group","conditions":[{"type":"drCrOnly","drCr":"credit"},{"type":"fieldValue","field":"accountNumber","operator":"notIn","values":["B"]}]}
            """);
        var negativeResult = await fixture.Repository.PreviewAsync(fixture.ProjectId, negative, context, CancellationToken.None);
        Assert.Equal(["V1|3", "V3|2", "V4|3"], negativeResult.PreviewRows.Select(row => row.VoucherNumber + "|" + row.LineItem).Order().ToArray());
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task MonthWindows_UseEachSelectedDateMonthAndExplicitBlankPolicy(string provider)
    {
        const string extraId = "rde.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string seed = """
            INSERT INTO gl(batch_id,source_row_number,voucher_number,gl_date,approval_date,
                account_number,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_excluded) VALUES
            ('month',1,'R1','2025-01-01','2024-02-27','A',100,100,0,'DEBIT',0),
            ('month',2,'R2','2025-01-01','2024-02-28','A',100,100,0,'DEBIT',0),
            ('month',3,'R3','2025-01-01','2024-02-29','A',100,100,0,'DEBIT',0),
            ('month',4,'R4','2025-01-01','2025-02-27','A',100,100,0,'DEBIT',0),
            ('month',5,'R5','2025-01-01','2025-02-28','A',100,100,0,'DEBIT',0),
            ('month',6,'R6','2025-01-01','2025-04-29','A',100,100,0,'DEBIT',0),
            ('month',7,'R7','2025-01-01','2025-05-29','A',100,100,0,'DEBIT',0),
            ('month',8,'R8','2025-01-01','2025-05-01','A',100,100,0,'DEBIT',0),
            ('month',9,'R9','2025-01-01',NULL,'A',-100,0,100,'CREDIT',0);
            INSERT INTO gl_rde_value(entry_id,field_id,value_type,date_value)
            SELECT entry_id,'rde.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','date',approval_date
            FROM gl WHERE approval_date IS NOT NULL;
            """;
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, seed);
        var extra = new GlRdeFieldMetadata(extraId, "sourceDate", "合成日期", "date");
        var context = new FilterConditionContext(100, null, "2025-01-01", "2025-12-31") { RdeFields = [extra] };
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
            payload[useExtra ? "fieldId" : "field"] = useExtra ? extraId : "approvalDate";
            var scenario = FilterCompletenessTests.Scenario(JsonSerializer.Serialize(payload));
            Assert.Empty(FilterScenarioValidator.Validate(scenario, new(false, true, true) { MoneyScale = 100, RdeFields = [extra] }));
            var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario, context, CancellationToken.None);
            Assert.Equal(blank ? item.Hits.Append("R9").Order().ToArray() : item.Hits,
                result.PreviewRows.Select(row => row.VoucherNumber).Order().ToArray());
        }
        foreach (var rule in new[]
        {
            // 分錄方向用同一個子條件組裡的「借貸別」與欄位條件表達。
            """{"type":"group","conditions":[{"type":"drCrOnly","drCr":"debit"},{"type":"fieldValue","field":"approvalDate","operator":"isBlank"}]}""",
            """{"type":"group","conditions":[{"type":"drCrOnly","drCr":"debit"},{"type":"fieldValue","field":"approvalDate","operator":"monthEndDays","value":"2","includeBlank":true}]}"""
        })
        {
            var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, FilterCompletenessTests.Scenario(rule), context, CancellationToken.None);
            Assert.DoesNotContain(result.PreviewRows, row => row.VoucherNumber == "R9");
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
        { type = "fieldValue", field = "glDate", @operator = "monthEndDays", value = days }));
        Assert.Contains(FilterScenarioValidator.Validate(scenario, new(false, true, true)), message => message.Contains("1 到 31"));
    }
}
