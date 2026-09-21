using System.Text.Json;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class MoneyTailFieldConditionsTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task MoneyTails_CoreAndExtraKeepLeadingZerosLengthAndBlankChoice(string provider)
    {
        const string extraId = "rde.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string seed = """
            INSERT INTO target_gl_entry(batch_id,source_row_number,document_number,post_date,
                account_code,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_effective)
            VALUES ('tail',1,'R1','2025-03-01','1000',100,100,0,'DEBIT',1),
                   ('tail',2,'R2','2025-03-01','1000',100145,100145,0,'DEBIT',1),
                   ('tail',3,'R3','2025-03-01','1000',-200190,0,200190,'CREDIT',1),
                   ('tail',4,'R4','2025-03-01','1000',50,50,0,'DEBIT',1),
                   ('tail',5,'R5','2025-03-01','1000',100012,100012,0,'DEBIT',1),
                   ('tail',6,'R6','2025-03-01','1000',0,0,0,'DEBIT',1);
            INSERT INTO target_gl_rde_value(entry_id,field_id,value_type,amount_scaled)
            SELECT entry_id,'rde.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','money',amount_scaled FROM target_gl_entry WHERE source_row_number <> 6;
            """;
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, seed);
        var extra = new GlRdeFieldMetadata(extraId, "sourceMoney", "原幣金額", "money");
        var context = new FilterRuleContext(100, null, "2025-01-01", "2025-12-31") { RdeFields = [extra] };
        var cases = new (string Operator, string Value, string[] Hits)[]
        {
            ("endsWithDigits", "001", ["R2", "R3"]), ("endsWithDigits", "000", ["R5"]),
            ("endsWithDigits", "1", ["R1", "R2", "R3"]), ("endsWithDigits", "0", ["R4", "R5", "R6"]),
            ("endsWithDigits", "001,000", ["R2", "R3", "R5"]),
            ("notEndsWithDigits", "001", ["R1", "R4", "R5", "R6"])
        };
        foreach (var useExtra in new[] { false, true })
        foreach (var blank in new[] { false, true })
        foreach (var item in cases)
        {
            var payload = new Dictionary<string, object?> { ["type"] = "fieldValue", ["operator"] = item.Operator,
                ["value"] = item.Value, ["includeBlank"] = blank, ["amountBasis"] = useExtra ? "signed" : "absolute" };
            payload[useExtra ? "fieldId" : "field"] = useExtra ? extraId : "amount";
            var scenario = FilterCompletenessTests.Scenario(JsonSerializer.Serialize(payload));
            Assert.Empty(FilterScenarioValidator.Validate(scenario, new(false, true, true) { MoneyScale = 100, RdeFields = [extra] }));
            var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario, context, CancellationToken.None);
            var expected = item.Hits.ToList();
            if (useExtra) { expected.Remove("R6"); if (blank) expected.Add("R6"); }
            Assert.Equal(expected.Order(), result.PreviewRows.Select(row => row.DocumentNumber).Order());
        }
    }
}
