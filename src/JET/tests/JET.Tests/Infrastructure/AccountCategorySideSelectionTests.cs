using JET.Application;
using JET.Domain;
using System.Text.Json;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class AccountCategorySideSelectionTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var provider in new[] { "sqlite" })
        foreach (var selection in new[] { "node", "subtree", "role" })
        foreach (var mode in new[] { "is", "isNot", "absent" })
            yield return [provider, selection, mode];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task AnySide_ThreeSelectionModes_HaveFixedRowAnswers(string provider, string selection, string mode)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, AccountCategorySideFixture.AllSql);
        var scenario = AccountCategorySideFixture.Scenario("any", selection, mode);
        var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario,
            AccountCategorySideFixture.Context, CancellationToken.None);
        Assert.Equal(Expected(selection, mode), result.PreviewRows
            .Select(row => $"{row.VoucherNumber}|{row.LineItem}").Order(StringComparer.Ordinal).ToArray());
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
            CategoryIds = AccountCategoryBuiltIns.All.Select(item => item.CategoryId)
                .Concat([AccountCategorySideFixture.B, AccountCategorySideFixture.C, AccountCategorySideFixture.D]).ToArray()
        };
        Assert.Empty(FilterScenarioValidator.Validate(AccountCategorySideFixture.Scenario("any", selection, mode), context));
    }

    [Theory]
    [InlineData("sqlite", "debit")]
    [InlineData("sqlite", "credit")]
    public async Task ExistingDebitAndCreditSide_KeepTheirFixedAnswers(string provider, string side)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, AccountCategorySideFixture.AllSql);
        var result = await fixture.Repository.PreviewAsync(fixture.ProjectId,
            AccountCategorySideFixture.Scenario(side, "node", "is"), AccountCategorySideFixture.Context, CancellationToken.None);
        Assert.Equal(side == "debit" ? ["V2|1", "ZERO|1"] : new[] { "V5|2" },
            result.PreviewRows.Select(row => $"{row.VoucherNumber}|{row.LineItem}").Order(StringComparer.Ordinal).ToArray());
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

internal static class AccountCategorySideFixture
{
    internal const string B = "custom.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    internal const string C = "custom.cccccccccccccccccccccccccccccccc";
    internal const string D = "custom.dddddddddddddddddddddddddddddddd";
    internal static readonly FilterConditionContext Context = new(100, null, "2025-01-01", "2025-12-31");

    internal static object ScenarioPayload(string side, string? selection, string mode = "is")
    {
        // First failure: 20261014-135805134-9060396119bc4e8f8dae2eecd29f1776. Keep fixed answers; express direction and absence explicitly.
        var rule = new Dictionary<string, object>
        {
            ["type"] = "accountSide", ["categoryMode"] = mode == "absent" ? "is" : mode, ["categoryIds"] = new[] { B }
        };
        if (selection is not null) rule["categorySelection"] = selection;
        var children = new List<object>();
        if (side != "any") children.Add(new { type = "drCrOnly", drCr = side });
        children.Add(rule);
        object condition = mode == "absent"
            ? new { type = "voucher", quantifier = "none", conditions = children }
            : side == "any" ? rule : new { type = "group", conditions = children };
        return new
        {
            name = "Synthetic category selection", rationale = "Fixed category membership",
            groups = new[] { new { conditions = new[] { condition } } }
        };
    }

    internal static FilterScenarioSpec Scenario(string side, string selection, string mode) =>
        FilterScenarioPayloadParser.Parse(JsonSerializer.SerializeToElement(ScenarioPayload(side, selection, mode)), 100);

    internal const string CategoryAndMappingSql = """
        INSERT INTO account_category (category_id, label, ordinal, semantic_role, is_builtin, revision, parent_category_id)
        VALUES ('custom.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', '子現金', 5, 'cash', 0, 2, 'builtin.cash'),
               ('custom.cccccccccccccccccccccccccccccccc', '次層其他', 6, 'others', 0, 2, 'custom.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'),
               ('custom.dddddddddddddddddddddddddddddddd', '另一現金', 7, 'cash', 0, 2, NULL);
        INSERT INTO account_category_path (ancestor_id,descendant_id)
        VALUES ('custom.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','custom.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'),
               ('custom.cccccccccccccccccccccccccccccccc','custom.cccccccccccccccccccccccccccccccc'),
               ('custom.dddddddddddddddddddddddddddddddd','custom.dddddddddddddddddddddddddddddddd'),
               ('builtin.cash','custom.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'),
               ('builtin.cash','custom.cccccccccccccccccccccccccccccccc'),
               ('custom.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','custom.cccccccccccccccccccccccccccccccc');
        INSERT INTO import_batch (batch_id,dataset_kind,source_file_path,source_file_name,imported_utc,row_count,columns_json)
        VALUES ('batch5-map','account_mapping','synthetic','synthetic.xlsx','2026-10-04T00:00:00Z',5,'[]');
        INSERT INTO account_mapping (batch_id,source_row_number,account_number,account_name,standardized_category,category_id)
        VALUES ('batch5-map',1,'A','A','Cash','builtin.cash'),
               ('batch5-map',2,'B','B','Cash','custom.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'),
               ('batch5-map',3,'C','C','Others','custom.cccccccccccccccccccccccccccccccc'),
               ('batch5-map',4,'D','D','Cash','custom.dddddddddddddddddddddddddddddddd'),
               ('batch5-map',5,'O','O','Others','builtin.others');
        """;

    internal const string GlSql = """
        INSERT INTO gl
            (batch_id,source_row_number,voucher_number,line_item,gl_date,account_number,account_name,description,
             is_excluded,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr)
        VALUES ('batch5-gl',1,'V1','1','2025-03-01','A','A','Synthetic',0,100,100,0,'DEBIT'),
               ('batch5-gl',2,'V1','2','2025-03-01','O','O','Synthetic',0,-100,0,100,'CREDIT'),
               ('batch5-gl',3,'V2','1','2025-03-01','B','B','Synthetic',0,100,100,0,'DEBIT'),
               ('batch5-gl',4,'V2','2','2025-03-01','O','O','Synthetic',0,-100,0,100,'CREDIT'),
               ('batch5-gl',5,'V3','1','2025-03-01','C','C','Synthetic',0,100,100,0,'DEBIT'),
               ('batch5-gl',6,'V3','2','2025-03-01','O','O','Synthetic',0,-100,0,100,'CREDIT'),
               ('batch5-gl',7,'V4','1','2025-03-01','D','D','Synthetic',0,100,100,0,'DEBIT'),
               ('batch5-gl',8,'V4','2','2025-03-01','O','O','Synthetic',0,-100,0,100,'CREDIT'),
               ('batch5-gl',9,'V5','1','2025-03-01','O','O','Synthetic',0,100,100,0,'DEBIT'),
               ('batch5-gl',10,'V5','2','2025-03-01','B','B','Synthetic',0,-100,0,100,'CREDIT'),
               ('batch5-gl',11,'V6','1','2025-03-01','U','U','Synthetic',0,100,100,0,'DEBIT'),
               ('batch5-gl',12,'V6','2','2025-03-01','O','O','Synthetic',0,-100,0,100,'CREDIT'),
               ('batch5-gl',13,'ZERO','1','2025-03-01','B','B','Synthetic',0,0,0,0,'DEBIT'),
               ('batch5-gl',14,'V6','3','2024-03-01','B','B','Outside period',1,100,100,0,'DEBIT');
        """;

    internal const string AllSql = CategoryAndMappingSql + GlSql;
}
