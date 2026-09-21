using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class CalendarFieldConditionsTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task CalendarConditions_CoreAndExtraDatesRespectMakeupDaysNegationAndBlankChoice(string provider)
    {
        const string extraId = "rde.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string seed = """
            INSERT INTO target_gl_entry(batch_id,source_row_number,document_number,post_date,approval_date,
                account_code,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_effective)
            VALUES ('calendar',1,'R1','2025-03-01','2025-03-01','1000',100,100,0,'DEBIT',1),
                   ('calendar',2,'R2','2025-03-01','2025-03-02','1000',100,100,0,'DEBIT',1),
                   ('calendar',3,'R3','2025-03-01','2025-03-03','1000',100,100,0,'DEBIT',1),
                   ('calendar',4,'R4','2025-03-01','2025-03-04','1000',100,100,0,'DEBIT',1),
                   ('calendar',5,'R5','2025-03-01','2025-03-05','1000',100,100,0,'DEBIT',1),
                   ('calendar',6,'R6','2025-03-01',NULL,'1000',100,100,0,'DEBIT',1);
            INSERT INTO staging_calendar_raw_day(day_type,date) VALUES ('makeup','2025-03-01'),
                ('holiday','2025-03-03'),('holiday','2025-03-05'),('makeup','2025-03-05');
            INSERT INTO target_gl_rde_value(entry_id,field_id,value_type,date_value)
            SELECT entry_id,'rde.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','date',approval_date FROM target_gl_entry WHERE approval_date IS NOT NULL;
            """;
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, seed);
        var extra = new GlRdeFieldMetadata(extraId, "sourceDate", "合成日期", "date");
        var context = new FilterRuleContext(100, null, "2025-01-01", "2025-12-31") { RdeFields = [extra] };
        var cases = new (string Operator, string[] Hits)[]
        {
            ("isWeekend", ["R1", "R2"]), ("isNotWeekend", ["R3", "R4", "R5"]),
            ("isHoliday", ["R3", "R5"]), ("isNotHoliday", ["R1", "R2", "R4"]),
            ("isMakeupDay", ["R1", "R5"]), ("isNotMakeupDay", ["R2", "R3", "R4"]),
            ("isNonBusinessDay", ["R2", "R3"]), ("isNotNonBusinessDay", ["R1", "R4", "R5"])
        };
        foreach (var useExtra in new[] { false, true })
        foreach (var blank in new[] { false, true })
        foreach (var item in cases)
        {
            var payload = new Dictionary<string, object?> { ["type"] = "fieldValue", ["operator"] = item.Operator, ["includeBlank"] = blank };
            payload[useExtra ? "fieldId" : "field"] = useExtra ? extraId : "docDate";
            var scenario = FilterCompletenessTests.Scenario(JsonSerializer.Serialize(payload));
            Assert.Empty(FilterScenarioValidator.Validate(scenario, new(false, true, true) { MoneyScale = 100, RdeFields = [extra] }));
            var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario, context, CancellationToken.None);
            var expected = blank ? item.Hits.Append("R6").Order().ToArray() : item.Hits;
            Assert.Equal(expected, result.PreviewRows.Select(row => row.DocumentNumber).Order().ToArray());
        }
    }
}
