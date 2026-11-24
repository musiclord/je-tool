using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 文字條件的值一律照字面比對：值裡的符號不是語法，值中間的空白不會被刪掉；
/// 只有值與欄位頭尾的空白、以及英文大小寫不計。合成資料每一個值都配一個「如果把符號當成語法、
/// 或把空白刪掉就會被誤選」的對照值，所以每個答案都分得出兩種做法。
/// </summary>
public sealed class TextConditionLiteralMatchingTests
{
    private const string Seed =
        """
        INSERT INTO gl (batch_id,source_row_number,voucher_number,line_item,gl_date,approval_date,
          account_number,description,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_excluded)
        VALUES
          ('t',1,'T01','1','2025-01-01','2025-01-01','001','PETTY CASH',100,100,0,'DEBIT',0),
          ('t',2,'T02','1','2025-01-01','2025-01-01','001','PETTYCASH',100,100,0,'DEBIT',0),
          ('t',3,'T03','1','2025-01-01','2025-01-01','001','  petty cash  ',100,100,0,'DEBIT',0),
          ('t',4,'T04','1','2025-01-01','2025-01-01','001','A.B',100,100,0,'DEBIT',0),
          ('t',5,'T05','1','2025-01-01','2025-01-01','001','AXB',100,100,0,'DEBIT',0),
          ('t',6,'T06','1','2025-01-01','2025-01-01','001','COST(TW)',100,100,0,'DEBIT',0),
          ('t',7,'T07','1','2025-01-01','2025-01-01','001','COSTTW',100,100,0,'DEBIT',0),
          ('t',8,'T08','1','2025-01-01','2025-01-01','001','A|B',100,100,0,'DEBIT',0),
          ('t',9,'T09','1','2025-01-01','2025-01-01','001','A',100,100,0,'DEBIT',0),
          ('t',10,'T10','1','2025-01-01','2025-01-01','001','B',100,100,0,'DEBIT',0),
          ('t',11,'T11','1','2025-01-01','2025-01-01','001','100%',100,100,0,'DEBIT',0),
          ('t',12,'T12','1','2025-01-01','2025-01-01','001','1000',100,100,0,'DEBIT',0),
          ('t',13,'T13','1','2025-01-01','2025-01-01','001','A_C',100,100,0,'DEBIT',0),
          ('t',14,'T14','1','2025-01-01','2025-01-01','001','ABC',100,100,0,'DEBIT',0);
        """;

    private static readonly FilterConditionContext Context = new(100, null, "2025-01-01", "2025-12-31");

    public static TheoryData<string, string, string[]> Cases() => new()
    {
        // 值中間的空白保留：「PETTY CASH」不會變成「PETTYCASH」；頭尾空白與大小寫不計。
        { "contains", "PETTY CASH", ["T01", "T03"] },
        { "contains", "PETTYCASH", ["T02"] },
        { "in", "PETTY CASH", ["T01", "T03"] },
        { "in", " petty cash ", ["T01", "T03"] },
        { "in", "PETTYCASH", ["T02"] },
        // 「.」不是「任何一個字元」。
        { "contains", "A.B", ["T04"] },
        { "in", "A.B", ["T04"] },
        // 括號不是分組。
        { "contains", "COST(TW)", ["T06"] },
        { "startsWith", "COST(", ["T06"] },
        // 「|」不是「或」。
        { "contains", "A|B", ["T08"] },
        { "in", "A|B", ["T08"] },
        // 「%」「_」不是萬用字元。
        { "contains", "100%", ["T11"] },
        { "startsWith", "100%", ["T11"] },
        { "endsWith", "0%", ["T11"] },
        { "contains", "A_C", ["T13"] },
        { "startsWith", "A_", ["T13"] },
        { "endsWith", "_C", ["T13"] }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TextValue_IsComparedLiterally(string op, string value, string[] expectedVouchers)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync("sqlite", Seed);
        var rule = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "fieldValue", ["field"] = "description", ["operator"] = op, ["values"] = new[] { value }
        });
        var scenario = FilterCompletenessTests.Scenario(rule);

        Assert.Empty(FilterScenarioValidator.Validate(scenario, new(false, true, false) { MoneyScale = 100 }));
        var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario, Context, CancellationToken.None);

        Assert.Equal(expectedVouchers, result.PreviewRows.Select(row => row.VoucherNumber).Order(StringComparer.Ordinal).ToArray());
    }

    // 多個關鍵字是「有任一個就選入」，每一個各自照字面比對；排除時每一個都不能出現。
    [Fact]
    public async Task SeveralKeywords_EachMatchLiterally_AndExclusionRequiresNoneOfThem()
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync("sqlite", Seed);
        async Task<string[]> Hits(string op)
        {
            var rule = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "fieldValue", ["field"] = "description", ["operator"] = op, ["values"] = new[] { "PETTY CASH", "A|B", "100%" }
            });
            var result = await fixture.Repository.PreviewAsync(
                fixture.ProjectId, FilterCompletenessTests.Scenario(rule), Context, CancellationToken.None);
            return result.PreviewRows.Select(row => row.VoucherNumber).Order(StringComparer.Ordinal).ToArray();
        }

        Assert.Equal(["T01", "T03", "T08", "T11"], await Hits("contains"));
        Assert.Equal(["T02", "T04", "T05", "T06", "T07", "T09", "T10", "T12", "T13", "T14"], await Hits("notContains"));
    }
}
