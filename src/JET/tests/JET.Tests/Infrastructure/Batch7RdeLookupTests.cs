using System.Text.RegularExpressions;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class Batch7RdeLookupTests
{
    private const string TextId = "rde.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly FilterRuleContext Context = new(100, null, "2025-01-01", "2025-12-31")
    { RdeFields = [new(TextId, "Extra", "Synthetic text", "text")] };
    private static readonly string[] Keywords = ["alpha", "%_[", .. Enumerable.Range(1, 98).Select(index => $"unused-{index}")];

    [Theory]
    [InlineData("sqlite", 1)]
    [InlineData("duckdb", 1)]
    [InlineData("sqlServer", 1)]
    [InlineData("sqlite", 100)]
    [InlineData("duckdb", 100)]
    [InlineData("sqlServer", 100)]
    public void RdeContains_BuildsOneLookupForTheWholePredicate(string provider, int keywordCount)
    {
        IProviderSqlDialect dialect = provider switch
        { "sqlite" => SqliteDialect.Instance, "duckdb" => DuckDbDialect.Instance, _ => SqlServerDialect.Instance };
        var scenario = Batch7SqlSemanticsTests.Scenario(new
        { type = "fieldValue", fieldId = TextId, @operator = "contains", values = Keywords.Take(keywordCount).ToArray() });
        var builder = new GlFilterWhereBuilder(dialect, new GlRulePredicates(dialect, GlPopulationScopeSql.Predicate));
        var plan = builder.BuildPlan(scenario, Context, 1_000_000);
        Assert.Equal(1, Regex.Matches(plan.Sql, "FROM target_gl_rde_value", RegexOptions.CultureInvariant).Count);
    }

    [Theory]
    [InlineData("sqlite", "contains", false)]
    [InlineData("duckdb", "contains", false)]
    [InlineData("sqlite", "contains", true)]
    [InlineData("duckdb", "contains", true)]
    [InlineData("sqlite", "notContains", false)]
    [InlineData("duckdb", "notContains", false)]
    [InlineData("sqlite", "notContains", true)]
    [InlineData("duckdb", "notContains", true)]
    public async Task HundredKeywords_PreserveLiteralWildcardsCaseAndBlankSemantics(string provider, string op, bool includeBlank)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Seed);
        string[] expected = (op, includeBlank) switch
        {
            ("contains", false) => ["ALPHA|1", "LITERAL|1"],
            ("contains", true) => ["ALPHA|1", "EMPTY|1", "LITERAL|1", "MISSING|1", "NULL|1"],
            ("notContains", false) => ["OTHER|1"],
            _ => ["EMPTY|1", "MISSING|1", "NULL|1", "OTHER|1"]
        };
        Assert.Equal(expected, await Batch7SqlSemanticsTests.HitsAsync(fixture, new
        { type = "fieldValue", fieldId = TextId, @operator = op, values = Keywords, includeBlank }, Context));
    }

    [Theory]
    [InlineData("sqlite", "isBlank")]
    [InlineData("duckdb", "isBlank")]
    [InlineData("sqlite", "isNotBlank")]
    [InlineData("duckdb", "isNotBlank")]
    public async Task MissingAndStoredBlankRdeValues_KeepTheSameExplicitBlankAnswers(string provider, string op)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Seed);
        string[] expected = op == "isBlank" ? ["EMPTY|1", "MISSING|1", "NULL|1"] : ["ALPHA|1", "LITERAL|1", "OTHER|1"];
        Assert.Equal(expected, await Batch7SqlSemanticsTests.HitsAsync(fixture,
            new { type = "fieldValue", fieldId = TextId, @operator = op }, Context));
    }

    private const string Seed = """
        INSERT INTO target_gl_entry(batch_id,source_row_number,document_number,line_item,post_date,account_code,
            amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_effective) VALUES
        ('b7',1,'ALPHA','1','2025-03-01','A',100,100,0,'DEBIT',1),
        ('b7',2,'LITERAL','1','2025-03-01','A',100,100,0,'DEBIT',1),
        ('b7',3,'OTHER','1','2025-03-01','A',100,100,0,'DEBIT',1),
        ('b7',4,'EMPTY','1','2025-03-01','A',100,100,0,'DEBIT',1),
        ('b7',5,'NULL','1','2025-03-01','A',100,100,0,'DEBIT',1),
        ('b7',6,'MISSING','1','2025-03-01','A',100,100,0,'DEBIT',1),
        ('b7',7,'OUT','1','2024-03-01','A',100,100,0,'DEBIT',0);
        INSERT INTO target_gl_rde_value(entry_id,field_id,value_type,text_value)
        SELECT entry_id,'rde.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','text',
            CASE source_row_number WHEN 1 THEN ' ALPHA ' WHEN 2 THEN 'literal%_[' WHEN 3 THEN 'other'
                 WHEN 4 THEN '  ' WHEN 5 THEN '' ELSE 'alpha' END
        FROM target_gl_entry WHERE source_row_number <> 6;
        """;
}
