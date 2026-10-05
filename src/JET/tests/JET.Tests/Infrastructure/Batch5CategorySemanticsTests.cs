using JET.Application;
using JET.Domain;
using System.Text.Json;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class Batch5CategorySemanticsTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var provider in new[] { "sqlite", "duckdb" })
        foreach (var selection in new[] { "node", "subtree", "role" })
        foreach (var mode in new[] { "is", "isNot", "absent" })
            yield return [provider, selection, mode];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task AnySide_ThreeSelectionModes_HaveFixedRowAnswers(string provider, string selection, string mode)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Batch5CategoryFixture.AllSql);
        var scenario = Batch5CategoryFixture.Scenario("any", selection, mode);
        var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario,
            Batch5CategoryFixture.Context, CancellationToken.None);
        Assert.Equal(Expected(selection, mode), result.PreviewRows
            .Select(row => $"{row.DocumentNumber}|{row.LineItem}").Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(Expected(selection, mode).Length, result.Count);
    }

    [Theory]
    [InlineData("node", "is")]
    [InlineData("node", "isNot")]
    [InlineData("node", "absent")]
    [InlineData("subtree", "is")]
    [InlineData("subtree", "isNot")]
    [InlineData("subtree", "absent")]
    [InlineData("role", "is")]
    [InlineData("role", "isNot")]
    [InlineData("role", "absent")]
    public void Validator_AcceptsAnySide(string selection, string mode)
    {
        var context = new FilterValidationContext(false, true, false, HasAnyAccountCategory: true)
        {
            TaxonomyCategoryIds = AccountTaxonomyBuiltIns.All.Select(item => item.CategoryId)
                .Concat([Batch5CategoryFixture.B, Batch5CategoryFixture.C, Batch5CategoryFixture.D]).ToArray()
        };
        Assert.Empty(FilterScenarioValidator.Validate(Batch5CategoryFixture.Scenario("any", selection, mode), context));
    }

    [Theory]
    [InlineData("sqlite", "debit")]
    [InlineData("duckdb", "debit")]
    [InlineData("sqlite", "credit")]
    [InlineData("duckdb", "credit")]
    public async Task ExistingDebitAndCreditSide_KeepTheirFixedAnswers(string provider, string side)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Batch5CategoryFixture.AllSql);
        var result = await fixture.Repository.PreviewAsync(fixture.ProjectId,
            Batch5CategoryFixture.Scenario(side, "node", "is"), Batch5CategoryFixture.Context, CancellationToken.None);
        Assert.Equal(side == "debit" ? ["V2|1", "ZERO|1"] : new[] { "V5|2" },
            result.PreviewRows.Select(row => $"{row.DocumentNumber}|{row.LineItem}").Order(StringComparer.Ordinal).ToArray());
    }

    private static string[] Expected(string selection, string mode) => (selection, mode) switch
    {
        ("node", "is") => ["V2|1", "V5|2", "ZERO|1"],
        ("subtree", "is") => ["V2|1", "V3|1", "V5|2", "ZERO|1"],
        ("role", "is") => ["V1|1", "V2|1", "V4|1", "V5|2", "ZERO|1"],
        ("node", "isNot") => ["V1|1", "V1|2", "V2|2", "V3|1", "V3|2", "V4|1", "V4|2", "V5|1", "V6|1", "V6|2"],
        ("subtree", "isNot") => ["V1|1", "V1|2", "V2|2", "V3|2", "V4|1", "V4|2", "V5|1", "V6|1", "V6|2"],
        ("role", "isNot") => ["V1|2", "V2|2", "V3|1", "V3|2", "V4|2", "V5|1", "V6|1", "V6|2"],
        ("node", "absent") => ["V1|1", "V1|2", "V3|1", "V3|2", "V4|1", "V4|2", "V6|1", "V6|2"],
        ("subtree", "absent") => ["V1|1", "V1|2", "V4|1", "V4|2", "V6|1", "V6|2"],
        ("role", "absent") => ["V3|1", "V3|2", "V6|1", "V6|2"],
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}

internal static class Batch5CategoryFixture
{
    internal const string B = "custom.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    internal const string C = "custom.cccccccccccccccccccccccccccccccc";
    internal const string D = "custom.dddddddddddddddddddddddddddddddd";
    internal static readonly FilterRuleContext Context = new(100, null, "2025-01-01", "2025-12-31");

    internal static object ScenarioPayload(string side, string? selection, string mode = "is")
    {
        var rule = new Dictionary<string, object>
        {
            ["type"] = "accountSide", ["drCr"] = side, ["categoryMode"] = mode, ["categoryIds"] = new[] { B }
        };
        if (selection is not null) rule["categorySelection"] = selection;
        return new
        {
            name = "Synthetic category selection", rationale = "Fixed category membership",
            groups = new[] { new { rules = new[] { rule } } }
        };
    }

    internal static FilterScenarioSpec Scenario(string side, string selection, string mode) =>
        FilterScenarioPayloadParser.Parse(JsonSerializer.SerializeToElement(ScenarioPayload(side, selection, mode)), 100);

    internal const string TaxonomyAndMappingSql = """
        INSERT INTO config_account_taxonomy (category_id, label, ordinal, semantic_role, is_builtin, revision, parent_category_id)
        VALUES ('custom.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', '子現金', 5, 'cash', 0, 2, 'builtin.cash'),
               ('custom.cccccccccccccccccccccccccccccccc', '次層其他', 6, 'others', 0, 2, 'custom.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'),
               ('custom.dddddddddddddddddddddddddddddddd', '另一現金', 7, 'cash', 0, 2, NULL);
        INSERT INTO config_account_taxonomy_path (ancestor_id,descendant_id)
        VALUES ('custom.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','custom.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'),
               ('custom.cccccccccccccccccccccccccccccccc','custom.cccccccccccccccccccccccccccccccc'),
               ('custom.dddddddddddddddddddddddddddddddd','custom.dddddddddddddddddddddddddddddddd'),
               ('builtin.cash','custom.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'),
               ('builtin.cash','custom.cccccccccccccccccccccccccccccccc'),
               ('custom.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','custom.cccccccccccccccccccccccccccccccc');
        INSERT INTO import_batch (batch_id,dataset_kind,source_file_path,source_file_name,imported_utc,row_count,columns_json)
        VALUES ('batch5-map','account_mapping','synthetic','synthetic.xlsx','2026-10-04T00:00:00Z',5,'[]');
        INSERT INTO target_account_mapping (batch_id,source_row_number,account_code,account_name,standardized_category,category_id)
        VALUES ('batch5-map',1,'A','A','Cash','builtin.cash'),
               ('batch5-map',2,'B','B','Cash','custom.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'),
               ('batch5-map',3,'C','C','Others','custom.cccccccccccccccccccccccccccccccc'),
               ('batch5-map',4,'D','D','Cash','custom.dddddddddddddddddddddddddddddddd'),
               ('batch5-map',5,'O','O','Others','builtin.others');
        """;

    internal const string GlSql = """
        INSERT INTO target_gl_entry
            (batch_id,source_row_number,document_number,line_item,post_date,account_code,account_name,document_description,
             is_effective,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr)
        VALUES ('batch5-gl',1,'V1','1','2025-03-01','A','A','Synthetic',1,100,100,0,'DEBIT'),
               ('batch5-gl',2,'V1','2','2025-03-01','O','O','Synthetic',1,-100,0,100,'CREDIT'),
               ('batch5-gl',3,'V2','1','2025-03-01','B','B','Synthetic',1,100,100,0,'DEBIT'),
               ('batch5-gl',4,'V2','2','2025-03-01','O','O','Synthetic',1,-100,0,100,'CREDIT'),
               ('batch5-gl',5,'V3','1','2025-03-01','C','C','Synthetic',1,100,100,0,'DEBIT'),
               ('batch5-gl',6,'V3','2','2025-03-01','O','O','Synthetic',1,-100,0,100,'CREDIT'),
               ('batch5-gl',7,'V4','1','2025-03-01','D','D','Synthetic',1,100,100,0,'DEBIT'),
               ('batch5-gl',8,'V4','2','2025-03-01','O','O','Synthetic',1,-100,0,100,'CREDIT'),
               ('batch5-gl',9,'V5','1','2025-03-01','O','O','Synthetic',1,100,100,0,'DEBIT'),
               ('batch5-gl',10,'V5','2','2025-03-01','B','B','Synthetic',1,-100,0,100,'CREDIT'),
               ('batch5-gl',11,'V6','1','2025-03-01','U','U','Synthetic',1,100,100,0,'DEBIT'),
               ('batch5-gl',12,'V6','2','2025-03-01','O','O','Synthetic',1,-100,0,100,'CREDIT'),
               ('batch5-gl',13,'ZERO','1','2025-03-01','B','B','Synthetic',1,0,0,0,'DEBIT'),
               ('batch5-gl',14,'V6','3','2024-03-01','B','B','Outside period',0,100,100,0,'DEBIT');
        """;

    internal const string AllSql = TaxonomyAndMappingSql + GlSql;
}
