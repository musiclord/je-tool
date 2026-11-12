using System.Text.Json;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 借貸科目分類與通用值條件的固定答案（兩值語意）：分類留白視為 Others、未在配對檔的科目不屬於任何分類、
/// 欄位空白的分錄預設不列入。預期值逐張手算，不由被測程式反算。
/// </summary>
public sealed class FilterCompletenessTests
{
    [Theory]
    [InlineData("sqlite")]
    public async Task UnicodeTextCase_MatchesBothSpellings(string provider)
    {
        const string seed = """
          INSERT INTO gl(batch_id,source_row_number,voucher_number,line_item,gl_date,
            account_number,description,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_excluded)
          VALUES ('unicode',1,'U1','1','2025-01-01','001','CAFÉ',100,100,0,'DEBIT',0),
                 ('unicode',2,'U2','1','2025-01-01','001','café',100,100,0,'DEBIT',0),
                 ('unicode',3,'U3','1','2025-01-01','001','其他',100,100,0,'DEBIT',0);
          """;
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, seed);
        var mismatches = new List<string>();
        foreach (var op in new[] { "equals", "contains", "startsWith", "endsWith", "notEquals", "notContains" })
        {
            var rule = JsonSerializer.Serialize(new { type="fieldValue", field="description", @operator=op, value="café" });
            var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, Scenario(rule), Context, CancellationToken.None);
            string[] expected = op.StartsWith("not", StringComparison.Ordinal) ? ["U3"] : ["U1","U2"];
            if (!expected.SequenceEqual(result.PreviewRows.Select(r=>r.VoucherNumber).Order()))
                mismatches.Add($"{provider}/{op}: expected {string.Join(',',expected)}; actual {string.Join(',',result.PreviewRows.Select(r=>r.VoucherNumber))}");
        }
        Assert.True(mismatches.Count == 0, string.Join("; ", mismatches));
    }

    // Answers are literal row numbers from the fixture, not a replay of production predicates.
    [Theory]
    [InlineData("sqlite")]
    public async Task UnicodeTextComparison_ExtraLegacyAndPersonRulesAgree(string provider)
    {
        const string fieldId = "rde.aaaa0000aaaa0000aaaa0000aaaa0000";
        const string seed = """
          INSERT INTO gl(batch_id,source_row_number,voucher_number,line_item,gl_date,
            account_number,description,created_by,approved_by,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_excluded)
          VALUES ('unicode',1,'U1','1','2025-01-01','001','CAFÉ','élise','ÉLISE',100,100,0,'DEBIT',0),
                 ('unicode',2,'U2','1','2025-01-01','001','café','ÉLISE','élise',100,100,0,'DEBIT',0),
                 ('unicode',3,'U3','1','2025-01-01','001','其他','élise','other',100,100,0,'DEBIT',0);
          INSERT INTO gl_rde_value(entry_id,field_id,value_type,text_value,date_value,amount_scaled)
          SELECT entry_id,'rde.aaaa0000aaaa0000aaaa0000aaaa0000','text',description,NULL,NULL FROM gl;
          """;
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, seed);
        var context = Context with { RdeFields = [new(fieldId,"extra","extra","text")] };
        (string Json, string[] Expected)[] cases =
        [
            ("""{"type":"fieldValue","field":"description","operator":"in","values":["café"]}""", ["U1","U2"]),
            ("""{"type":"fieldValue","field":"description","operator":"notIn","values":["café"]}""", ["U3"]),
            ("""{"type":"fieldValue","fieldId":"rde.aaaa0000aaaa0000aaaa0000aaaa0000","operator":"equals","value":"café"}""", ["U1","U2"]),
            ("""{"type":"fieldValue","fieldId":"rde.aaaa0000aaaa0000aaaa0000aaaa0000","operator":"notContains","value":"café"}""", ["U3"]),
            ("""{"type":"typed","fieldId":"rde.aaaa0000aaaa0000aaaa0000aaaa0000","operator":"in","values":["café"]}""", ["U1","U2"]),
            ("""{"type":"typed","fieldId":"rde.aaaa0000aaaa0000aaaa0000aaaa0000","operator":"notEquals","value":"café"}""", ["U3"]),
            ("""{"type":"text","field":"description","mode":"exact","keywords":"café"}""", ["U1","U2"]),
            ("""{"type":"textSet","field":"description","mode":"notContains","values":["café"]}""", ["U3"]),
            // 「傳票摘要」的「包含任一文字」。
            ("""{"type":"fieldValue","field":"description","operator":"contains","values":["café"]}""", ["U1","U2"]),
            ("""{"type":"preparerEqualsApprover"}""", ["U1","U2"])
        ];
        foreach (var c in cases)
        {
            var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, Scenario(c.Json), context, CancellationToken.None);
            Assert.Equal(c.Expected, result.PreviewRows.Select(r=>r.VoucherNumber).Order());
            Assert.Equal(c.Expected.Length, result.Count);
        }
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task AllValueOperatorsMatchIndependentOracle(string provider)
    {
        const string textId = "rde.aaaa0000aaaa0000aaaa0000aaaa0000";
        const string dateId = "rde.bbbb0000bbbb0000bbbb0000bbbb0000";
        const string moneyId = "rde.cccc0000cccc0000cccc0000cccc0000";
        const string seed = """
          INSERT INTO gl(batch_id,source_row_number,voucher_number,line_item,gl_date,approval_date,
            account_number,description,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_excluded)
          VALUES
          ('oracle',1,'R1','1','2025-01-01','2024-02-28','001',' alpha ',-1001,0,1001,'CREDIT',0),
          ('oracle',2,'R2','1','2025-01-01','2024-02-29','001','ALPHA beta',-1000,0,1000,'CREDIT',0),
          ('oracle',3,'R3','1','2025-01-01','2024-03-01','001','%_[',0,0,0,'DEBIT',0),
          ('oracle',4,'R4','1','2025-01-01','2024-03-31','001','',1000,1000,0,'DEBIT',0),
          ('oracle',5,'R5','1','2025-01-01','2024-04-30','001',NULL,1001,1001,0,'DEBIT',0),
          ('oracle',6,'R6','1','2025-01-01',NULL,'001','beta',2000,2000,0,'DEBIT',0),
          ('oracle',7,'R7','1','2025-01-01',NULL,'001','   ',0,0,0,'DEBIT',0),
          ('oracle',8,'OUT','1','2024-12-31','2024-02-29','001','alpha',1000,1000,0,'DEBIT',1);
          INSERT INTO calendar_day(day_type,date) VALUES ('holiday','2024-02-29'),
            ('holiday','2024-03-01'),('makeup','2024-02-29'),('makeup','2024-03-31');
          """;
        var extraSeed = $"""
          INSERT INTO gl_rde_value(entry_id,field_id,value_type,text_value,date_value,amount_scaled)
          SELECT entry_id,'{textId}','text',description,NULL,NULL FROM gl WHERE source_row_number<>7 AND description IS NOT NULL;
          INSERT INTO gl_rde_value(entry_id,field_id,value_type,text_value,date_value,amount_scaled)
          SELECT entry_id,'{dateId}','date',NULL,approval_date,NULL FROM gl WHERE source_row_number<>7 AND approval_date IS NOT NULL;
          INSERT INTO gl_rde_value(entry_id,field_id,value_type,text_value,date_value,amount_scaled)
          SELECT entry_id,'{moneyId}','amount',NULL,NULL,amount_scaled FROM gl WHERE source_row_number<>7;
          """;
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, seed + extraSeed);
        var context = Context with { RdeFields = [new(textId,"text","text","text"),new(dateId,"date","date","date"),new(moneyId,"money","money","amount")] };
        (string Op, string Operand, int[] Hits)[] textCases =
        [
            ("equals", "\"value\":\"ALPHA\"", [1]), ("notEquals", "\"value\":\"alpha\"", [2,3,6]),
            ("contains", "\"values\":[\"alpha\",\"%_[\"]", [1,2,3]),
            ("notContains", "\"values\":[\"alpha\",\"%_[\"]", [6]),
            ("startsWith", "\"value\":\"alpha\"", [1,2]), ("endsWith", "\"value\":\"beta\"", [2,6]),
            ("notStartsWith", "\"value\":\"alpha\"", [3,6]), ("notEndsWith", "\"value\":\"beta\"", [1,3]),            ("in", "\"values\":[\"alpha\",\"BETA\",\"alpha\"]", [1,6]),
            ("notIn", "\"values\":[\"alpha\",\"beta\"]", [2,3]),
            ("isBlank", "", [4,5,7]), ("isNotBlank", "", [1,2,3,6])
        ];
        (string Op, string Operand, int[] Hits)[] dateCases =
        [
            ("on", "\"value\":\"2024-02-29\"", [2]), ("notEquals", "\"value\":\"2024-02-29\"", [1,3,4,5]),
            ("before", "\"value\":\"2024-02-29\"", [1]), ("onOrBefore", "\"value\":\"2024-02-29\"", [1,2]),
            ("after", "\"value\":\"2024-02-29\"", [3,4,5]), ("onOrAfter", "\"value\":\"2024-02-29\"", [2,3,4,5]),
            ("between", "\"from\":\"2024-02-29\",\"to\":\"2024-03-31\"", [2,3,4]),
            ("notBetween", "\"from\":\"2024-02-29\",\"to\":\"2024-03-31\"", [1,5]),
            ("in", "\"values\":[\"2024-02-29\",\"2024-03-31\",\"2024-02-29\"]", [2,4]),
            ("notIn", "\"values\":[\"2024-02-29\",\"2024-03-31\"]", [1,3,5]),
            ("dayOfMonthIn", "\"values\":[\"28\",\"31\"]", [1,4]),
            ("dayOfMonthNotIn", "\"values\":[\"28\",\"31\"]", [2,3,5]),
            ("monthStartDays", "\"value\":\"2\"", [3]),
            ("notMonthStartDays", "\"value\":\"2\"", [1,2,4,5]),
            ("monthEndDays", "\"value\":\"2\"", [1,2,4,5]),
            ("notMonthEndDays", "\"value\":\"2\"", [3]),
            ("isWeekend", "", [4]), ("isNotWeekend", "", [1,2,3,5]),
            ("isHoliday", "", [2,3]), ("isNotHoliday", "", [1,4,5]),
            ("isMakeupDay", "", [2,4]), ("isNotMakeupDay", "", [1,3,5]),
            ("isNonBusinessDay", "", [3]), ("isNotNonBusinessDay", "", [1,2,4,5]),
            ("isBlank", "", [6,7]), ("isNotBlank", "", [1,2,3,4,5])
        ];
        (string Op, string Operand, int[] Hits)[] signedCases =
        [
            ("equals", "\"value\":\"10\"", [4]), ("notEquals", "\"value\":\"10\"", [1,2,3,5,6]),
            ("greaterThan", "\"value\":\"10\"", [5,6]), ("greaterThanOrEqual", "\"value\":\"10\"", [4,5,6]),
            ("lessThan", "\"value\":\"10\"", [1,2,3]), ("lessThanOrEqual", "\"value\":\"10\"", [1,2,3,4]),
            ("between", "\"from\":\"-10\",\"to\":\"10\"", [2,3,4]),
            ("notBetween", "\"from\":\"-10\",\"to\":\"10\"", [1,5,6]),
            ("in", "\"values\":[\"-10\",\"10\",\"10.00\"]", [2,4]),
            ("notIn", "\"values\":[\"-10\",\"10\"]", [1,3,5,6]),
            ("endsWithDigits", "\"value\":\"10\"", [1,2,4,5]),
            ("notEndsWithDigits", "\"value\":\"10\"", [3,6]),
            // 整數部分的特定數字：整數部分 10 包含 1 但不以 1 結尾，20 包含 2；R3 的整數部分 0 兩者都不含。
            // （運算子清單多了這兩個，表格還沒有答案）。
            ("containsDigits", "\"value\":\"1,2\"", [1,2,4,5,6]),
            ("notContainsDigits", "\"value\":\"1,2\"", [3]),
            // 尾數連續多個 0：整數部分 10 與 20 有一個 0；R3 的整數部分是 0，不算。
            ("endsWithZeros", "\"value\":\"1\"", [1,2,4,5,6]),
            ("notEndsWithZeros", "\"value\":\"1\"", [3]),
            ("isBlank", "", [7]), ("isNotBlank", "", [1,2,3,4,5,6])
        ];
        (string Op, string Operand, int[] Hits)[] absoluteCases =
        [
            ("equals", "\"value\":\"10\"", [2,4]), ("notEquals", "\"value\":\"10\"", [1,3,5,6]),
            ("greaterThan", "\"value\":\"10\"", [1,5,6]), ("greaterThanOrEqual", "\"value\":\"10\"", [1,2,4,5,6]),
            ("lessThan", "\"value\":\"10\"", [3]), ("lessThanOrEqual", "\"value\":\"10\"", [2,3,4]),
            ("between", "\"from\":\"0\",\"to\":\"10\"", [2,3,4]),
            ("notBetween", "\"from\":\"0\",\"to\":\"10\"", [1,5,6]),
            ("in", "\"values\":[\"10\",\"10.00\"]", [2,4]), ("notIn", "\"values\":[\"10\"]", [1,3,5,6]),
            ("endsWithDigits", "\"value\":\"10\"", [1,2,4,5]),
            ("notEndsWithDigits", "\"value\":\"10\"", [3,6]),
            // 整數部分的特定數字：整數部分 10 包含 1 但不以 1 結尾，20 包含 2；R3 的整數部分 0 兩者都不含。
            // （運算子清單多了這兩個，表格還沒有答案）。
            ("containsDigits", "\"value\":\"1,2\"", [1,2,4,5,6]),
            ("notContainsDigits", "\"value\":\"1,2\"", [3]),
            // 尾數連續多個 0：整數部分 10 與 20 有一個 0；R3 的整數部分是 0，不算。
            ("endsWithZeros", "\"value\":\"1\"", [1,2,4,5,6]),
            ("notEndsWithZeros", "\"value\":\"1\"", [3]),
            ("isBlank", "", [7]), ("isNotBlank", "", [1,2,3,4,5,6])
        ];
        foreach (var (type, core, extra, cases, blanks, basis) in new[]
        {
            ("text","description",textId,textCases,new[]{4,5,7}, (string?)null),
            ("date","approvalDate",dateId,dateCases,new[]{6,7}, (string?)null),
            ("amount","amount",moneyId,signedCases,new[]{7}, "signed"),
            ("amount","amount",moneyId,absoluteCases,new[]{7}, "absolute")
        })
        {
            // Fail if a new UI/backend operator is added without an independently specified answer.
            Assert.Equal(FieldValueConditions.Operators(type).Order(), cases.Select(c=>c.Op).Order());
            foreach (var isExtra in new[] { false, true })
            foreach (var includeBlank in new[] { false, true })
            foreach (var c in cases)
            {
                var expected = c.Hits.ToList();
                // Core amount has a real zero in R7; the extra amount has no row there.
                if (type == "amount" && !isExtra)
                {
                    expected.Remove(7);
                    if (c.Op is "notEquals" or "lessThan" or "lessThanOrEqual" or "between" or "notIn" or "notEndsWithDigits" or "notContainsDigits" or "notEndsWithZeros" or "isNotBlank") expected.Add(7);
                }
                else if (includeBlank && c.Op is not "isBlank" and not "isNotBlank") expected.AddRange(blanks);
                var rule = $$"""
                    {"type":"fieldValue","{{(isExtra ? "fieldId" : "field")}}":"{{(isExtra ? extra : core)}}","operator":"{{c.Op}}","includeBlank":{{includeBlank.ToString().ToLowerInvariant()}}{{(basis is null ? "" : $",\"amountBasis\":\"{basis}\"")}}{{(c.Operand.Length==0 ? "" : ","+c.Operand)}}}
                    """;
                var scenario = Scenario(rule);
                var errors = FilterScenarioValidator.Validate(scenario, new(false,true,false) { MoneyScale=100, RdeFields=context.RdeFields });
                // Core signed amounts are rejected by validation; the compiler answers are still checked and the rejection is validated exactly.
                // 核心金額的「含正負號」當成無法讀取的內容。
                if (basis == "signed" && !isExtra)
                    Assert.Equal("第 1 組第 1 條：" + FilterScenarioCorruption.RuleMessage, Assert.Single(errors));
                else Assert.Empty(errors);
                var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario, context, CancellationToken.None);
                var ids = expected.Distinct().Order().Select(i=>"R"+i).ToArray();
                Assert.True(ids.SequenceEqual(result.PreviewRows.Select(r=>r.VoucherNumber).Order()), $"{provider}/{type}/{basis}/{isExtra}/{includeBlank}/{c.Op}");
                Assert.Equal(ids.Length, result.Count);
                Assert.Equal(ids.Length, result.VoucherCount);
            }
        }
    }

    // A: Cash debit / Others credit. B: both kinds on both sides. C: unmapped (999) credit.
    // D: Cash credit plus unmapped credit. E: custom category with Cash role, no credit.
    private const string Seed = """
        INSERT INTO account_category (category_id,label,ordinal,semantic_role,is_builtin,revision)
        VALUES ('custom.0123456789abcdef0123456789abcdef','Petty cash',5,'cash',0,2);
        INSERT INTO account_mapping (batch_id,source_row_number,account_number,account_name,standardized_category,category_id)
        VALUES ('m',1,'001','Cash','Cash','builtin.cash'),('m',2,'002','Other','Others','builtin.others'),
               ('m',3,'003','Petty','Cash','custom.0123456789abcdef0123456789abcdef');
        UPDATE account_mapping SET classification_explicit = 1;
        INSERT INTO gl (batch_id,source_row_number,voucher_number,line_item,gl_date,approval_date,
          account_number,description,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_excluded)
        VALUES
          ('f',1,'A','1','2025-01-01','2024-02-29','001','start 100%_[ end',10000,10000,0,'DEBIT',0),
          ('f',2,'A','2','2025-02-01',NULL,'002',NULL,-10000,0,10000,'CREDIT',0),
          ('f',3,'B','1','2025-01-01','2025-01-01','001','literal',10000,10000,0,'DEBIT',0),
          ('f',4,'B','2','2025-02-01','2025-01-01','002','literal',-10000,0,10000,'CREDIT',0),
          ('f',5,'B','3','2025-02-01','2025-01-01','002','literal',5000,5000,0,'DEBIT',0),
          ('f',6,'B','4','2025-03-01','2025-01-01','001','literal',-5000,0,5000,'CREDIT',0),
          ('f',7,'C','1','2025-01-01','2025-01-01','001','literal',10000,10000,0,'DEBIT',0),
          ('f',8,'C','2','2025-02-01','2025-01-01','999','literal',-10000,0,10000,'CREDIT',0),
          ('f',9,'D','1','2025-01-01','2025-01-01','001','literal',10000,10000,0,'DEBIT',0),
          ('f',10,'D','2','2025-03-01','2025-01-01','001','literal',-5000,0,5000,'CREDIT',0),
          ('f',11,'D','3','2025-02-01','2025-01-01','999','literal',-5000,0,5000,'CREDIT',0),
          ('f',12,'E','1','2025-01-01','2025-01-01','003','literal',0,0,0,'DEBIT',0);
        """;
    private static readonly FilterConditionContext Context = new(100, null, "2025-01-01", "2025-12-31");
    private const string CashDebit = """{"type":"group","conditions":[{"type":"drCrOnly","drCr":"debit"},{"type":"accountSide","categoryMode":"is","categoryIds":["builtin.cash"]}]}""";
    private const string OtherCredit = """{"type":"group","conditions":[{"type":"drCrOnly","drCr":"credit"},{"type":"accountSide","categoryMode":"isNot","categoryIds":["builtin.cash"]}]}""";
    private const string NoCashCredit = """{"type":"voucher","quantifier":"none","conditions":[{"type":"drCrOnly","drCr":"credit"},{"type":"accountSide","categoryMode":"is","categoryIds":["builtin.cash"]}]}""";
    private static string Witness(string rule) => $$"""{"type":"voucher","quantifier":"any","conditions":[{{rule}}]}""";

    internal static FilterScenarioSpec Scenario(string rules, string scope = "row", string? secondGroupRules = null) =>
        FilterScenarioPayloadParser.Parse(JsonDocument.Parse($$"""
        {"name":"Synthetic selection","rationale":"Fixed expected identities","groups":[{"matchScope":"{{scope}}","conditions":[{{rules}}]}
        {{(secondGroupRules is null ? "" : $$""",{"join":"AND","conditions":[{{secondGroupRules}}]}""")}}]}
        """).RootElement, 100);

    [Theory]
    [InlineData("sqlite")]
    public async Task CashSidesAndAbsence_HaveFixedTwoValuedResults(string provider)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Seed);
        async Task Check(FilterScenarioSpec scenario, string[] rows)
        {
            Assert.Empty(FilterScenarioValidator.Validate(scenario, new(false, true, false, HasAnyAccountCategory: true) { MoneyScale = 100 }));
            var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario, Context, CancellationToken.None);
            Assert.Equal(rows, result.PreviewRows.Select(row => $"{row.VoucherNumber}|{row.LineItem}").Order().ToArray());
            Assert.Equal(rows.Length, result.Count);
            Assert.Equal(rows.Select(row => row.Split('|')[0]).Distinct().Count(), result.VoucherCount);
        }
        // 借 Cash 且同傳票有貸方不屬於 Cash：未配對科目 999 也算不屬於 Cash，E 沒有貸方所以不命中。
        await Check(Scenario(CashDebit + "," + Witness(OtherCredit)), ["A|1", "B|1", "C|1", "D|1"]);
        // 借 Cash 且整張傳票的貸方都不屬於 Cash：B、D 各有一列 Cash 貸方。
        await Check(Scenario(CashDebit + "," + NoCashCredit), ["A|1", "C|1", "E|1"]);
        // 傳票層條件單獨使用時輸出整張傳票的分錄。
        await Check(Scenario(NoCashCredit), ["A|1", "A|2", "C|1", "C|2", "E|1"]);
        await Check(Scenario(OtherCredit), ["A|2", "B|2", "C|2", "D|3"]);
        await Check(Scenario("""{"join":"OR","type":"drCrOnly","drCr":"debit"},{"join":"AND","type":"drCrOnly","drCr":"credit"}"""), []);
        await Check(Scenario("""{"join":"or","type":"drCrOnly","drCr":"debit"},{"join":"or","type":"drCrOnly","drCr":"credit"}"""),
            ["A|1", "A|2", "B|1", "B|2", "B|3", "B|4", "C|1", "C|2", "D|1", "D|2", "D|3", "E|1"]);
        var four = CashDebit + "," + Witness(OtherCredit) + "," +
            """{"type":"voucher","quantifier":"any","conditions":[{"type":"drCrOnly","drCr":"debit"},{"type":"accountSide","categoryMode":"isNot","categoryIds":["builtin.cash"]}]},{"type":"voucher","quantifier":"any","conditions":[{"type":"drCrOnly","drCr":"credit"},{"type":"accountSide","categoryMode":"is","categoryIds":["builtin.cash"]}]}""";
        await Check(Scenario(four), ["B|1"]);
        await Check(Scenario(OtherCredit + "," + Witness(CashDebit)), ["A|2", "B|2", "C|2", "D|3"]);
        // 排除日期用一般條件的「不在清單中」放在另一組（列層級 AND），傳票層判斷不受影響。
        const string notMarch = """{"type":"fieldValue","field":"glDate","operator":"notIn","values":["2025-03-01"]}""";
        await Check(Scenario(CashDebit + "," + NoCashCredit, secondGroupRules: notMarch), ["A|1", "C|1", "E|1"]);
        const string notFebruary = """{"type":"fieldValue","field":"glDate","operator":"notIn","values":["2025-02-01"]}""";
        await Check(Scenario("""{"type":"drCrOnly","drCr":"debit"}""", secondGroupRules: notFebruary), ["A|1", "B|1", "C|1", "D|1", "E|1"]);
        var onlyA = """{"type":"fieldValue","field":"voucherNumber","operator":"equals","value":"A"}""";
        await Check(Scenario(NoCashCredit + "," + onlyA), ["A|1", "A|2"]);
        var onlyCOr = """{"join":"OR","type":"fieldValue","field":"voucherNumber","operator":"equals","value":"C"}""";
        await Check(Scenario(NoCashCredit + "," + onlyCOr), ["A|1", "A|2", "C|1", "C|2", "E|1"]);
    }

    [Fact]
    public void LongExcelDescriptions_KeepUnicodeAndLineBreaksAcrossContinuationRows()
    {
        var original = new string('x', 499) + "🙂" + new string('界', 33_000)
            + string.Concat(Enumerable.Repeat("\r\n", 300));
        var parts = FilterConditionExcelText.Parts(original);
        Assert.Equal(original, string.Concat(parts));
        var strictUtf8 = new System.Text.UTF8Encoding(false, true);
        Assert.All(parts, part =>
        {
            Assert.InRange(part.Length, 1, 32_767);
            Assert.True(part.Count(character => character == '\n') <= 253);
            Assert.NotEmpty(strictUtf8.GetBytes(part));
        });
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task NegativeOperators_LeaveBlankValuesOutByDefault(string provider)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Seed);
        var definition = JsonDocument.Parse("""
          {"name":"Blank default","rationale":"Synthetic","groups":[{"conditions":[
            {"type":"fieldValue","field":"description","operator":"notIn","values":["literal"]}]}]}
          """).RootElement;
        var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, FilterScenarioPayloadParser.Parse(definition, 100), Context, CancellationToken.None);
        Assert.Equal("A|1", $"{Assert.Single(result.PreviewRows).VoucherNumber}|{result.PreviewRows[0].LineItem}");
        Assert.Contains("空白不列入", FilterConditionRenderer.Render(definition), StringComparison.Ordinal);
        var withBlank = JsonDocument.Parse("""
          {"name":"Blank included","rationale":"Synthetic","groups":[{"conditions":[
            {"type":"fieldValue","field":"description","operator":"notIn","values":["literal"],"includeBlank":true}]}]}
          """).RootElement;
        var included = await fixture.Repository.PreviewAsync(fixture.ProjectId, FilterScenarioPayloadParser.Parse(withBlank, 100), Context, CancellationToken.None);
        Assert.Equal(["A|1", "A|2"], included.PreviewRows.Select(row => $"{row.VoucherNumber}|{row.LineItem}").Order());
        Assert.Contains("空白也符合", FilterConditionRenderer.Render(withBlank), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task ValueComparisons_PreserveDatesBlanksLiteralsAndMoney(string provider)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Seed);
        async Task Check(string rule, string[] rows)
        {
            var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, Scenario(rule), Context, CancellationToken.None);
            Assert.Equal(rows, result.PreviewRows.Select(row => $"{row.VoucherNumber}|{row.LineItem}").Order().ToArray());
        }
        await Check("""{"type":"fieldValue","field":"approvalDate","operator":"in","values":["2024-02-29","2024-02-29"]}""", ["A|1"]);
        await Check("""{"type":"fieldValue","field":"approvalDate","operator":"notIn","values":["2025-01-01"]}""", ["A|1"]);
        await Check("""{"type":"fieldValue","field":"approvalDate","operator":"notIn","values":["2025-01-01"],"includeBlank":true}""", ["A|1", "A|2"]);
        await Check("""{"type":"fieldValue","field":"description","operator":"startsWith","value":"start 100%_["}""", ["A|1"]);
        await Check("""{"type":"fieldValue","field":"description","operator":"endsWith","value":"_[ end"}""", ["A|1"]);
        await Check("""{"type":"fieldValue","field":"description","operator":"isBlank"}""", ["A|2"]);
        await Check("""{"type":"fieldValue","field":"amount","amountBasis":"signed","operator":"in","values":["-50","0"]}""", ["B|4", "D|2", "D|3", "E|1"]);
        await Check("""{"type":"fieldValue","field":"amount","amountBasis":"absolute","operator":"notBetween","from":"1","to":"100"}""", ["E|1"]);
        await Check("""{"type":"fieldValue","field":"accountNumber","operator":"equals","value":"1"}""", []);
        // 每月幾日：核准日 29 日只有 A|1；排除 1 日時核准日空白的 A|2 預設不列入。
        await Check("""{"type":"fieldValue","field":"approvalDate","operator":"dayOfMonthIn","values":["29"]}""", ["A|1"]);
        await Check("""{"type":"fieldValue","field":"approvalDate","operator":"dayOfMonthNotIn","values":["1","01"]}""", ["A|1"]);
        await Check("""{"type":"fieldValue","field":"approvalDate","operator":"dayOfMonthNotIn","values":["1"],"includeBlank":true}""", ["A|1", "A|2"]);
        await Check("""{"type":"fieldValue","field":"glDate","operator":"dayOfMonthIn","values":["28","31"]}""", []);
        // 包含任一文字接受多個關鍵字；不包含任何文字是它的否定，摘要空白的 A|2 預設不列入。
        await Check("""{"type":"fieldValue","field":"description","operator":"contains","values":["START","nothing"]}""", ["A|1"]);
        await Check("""{"type":"fieldValue","field":"description","operator":"notContains","values":["start","LITERAL"]}""", []);
        await Check("""{"type":"fieldValue","field":"description","operator":"notContains","value":"literal"}""", ["A|1"]);
        // 開頭符合與結尾符合也收多個值，符合任一個就算，萬用字元照字面比對；
        // 排除時每一個都不能符合，所以開頭各是 start 與 lit 的分錄全部排除，空白的 A|2 只在勾選空白時列入。
        await Check("""{"type":"fieldValue","field":"description","operator":"startsWith","values":["nothing","start 100%_["]}""", ["A|1"]);
        await Check("""{"type":"fieldValue","field":"description","operator":"endsWith","values":["xyz","_[ end"]}""", ["A|1"]);
        await Check("""{"type":"fieldValue","field":"description","operator":"notStartsWith","values":["start","LIT"]}""", []);
        await Check("""{"type":"fieldValue","field":"description","operator":"notStartsWith","values":["start","LIT"],"includeBlank":true}""", ["A|2"]);
        await Check("""{"type":"fieldValue","field":"description","operator":"notEndsWith","values":["end","RAL"]}""", []);
        await Check("""{"type":"fieldValue","field":"description","operator":"notEndsWith","values":["end"]}""",
            ["B|1", "B|2", "B|3", "B|4", "C|1", "C|2", "D|1", "D|2", "D|3", "E|1"]);
    }

    [Fact]
    public async Task VoucherPages_ListHitVouchersAndTheirReferenceRows()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase db = new SqliteProjectDatabase(folder);
        const string id = "filter-voucher-pages";
        Directory.CreateDirectory(folder.GetProjectDirectory(id));
        await db.EnsureCreatedAsync(id, CancellationToken.None);
        await using (var connection = db.CreateConnection(id))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand(); seed.CommandText = Seed; await seed.ExecuteNonQueryAsync();
        }
        var repository = new LocalFilterVoucherRepository(db);
        var scenario = Scenario(CashDebit);
        var first = await repository.ReadAsync(id, scenario, Context, null, new PageRequest(null, 1), countWholeVoucherRows: false, CancellationToken.None);
        Assert.Equal(new FilterVoucherSummary("A", "2025-01-01", 1, 2, 10000), Assert.Single(first.Vouchers));
        // 傳票摘要頁走共用 keyset 分頁，下一頁鍵是 opaque 游標，內容是末列的傳票號碼。
        Assert.True(PageCursor.TryDecode(first.NextKey, out var firstKey));
        Assert.Equal("A", firstKey);
        var second = await repository.ReadAsync(id, scenario, Context, null, new PageRequest(first.NextKey, 1), countWholeVoucherRows: false, CancellationToken.None);
        Assert.Equal(new FilterVoucherSummary("B", "2025-01-01", 1, 4, 15000), Assert.Single(second.Vouchers));
        var detail = await repository.ReadAsync(id, scenario, Context, "A", new PageRequest(null, 1), countWholeVoucherRows: false, CancellationToken.None);
        var hit = Assert.Single(detail.Details);
        Assert.True(hit.IsHit);
        Assert.Equal([new FilterConditionPosition(1, 1)], hit.PrimaryConditions);
        var nextDetail = await repository.ReadAsync(id, scenario, Context, "A", new PageRequest(detail.NextKey, 1), countWholeVoucherRows: false, CancellationToken.None);
        var reference = Assert.Single(nextDetail.Details);
        Assert.Equal("2", reference.LineItem);
        Assert.False(reference.IsHit);
        Assert.Empty(reference.PrimaryConditions);
        Assert.Null(nextDetail.NextKey);
        var noMatch = await repository.ReadAsync(id, Scenario(OtherCredit), Context, "E", new PageRequest(null, 10), countWholeVoucherRows: false, CancellationToken.None);
        Assert.Empty(noMatch.Details);
        // 排序與搜尋：借方 Cash 命中 A(2 列)、B(4 列)、C(2 列)、D(3 列)、E(1 列)。依期間內全部分錄降冪、每頁一張走到底，
        // 固定答案 B、D、C、A、E（A 與 C 同值時穩定鍵也降冪）；搜尋 a 不分大小寫只留 A。
        var order = new List<string>();
        string? sortCursor = null;
        do
        {
            var sortedPage = await repository.ReadAsync(id, scenario, Context, null,
                new PageRequest(sortCursor, 1, new PageSort("totalRowCount", PageSortDirection.Descending)), countWholeVoucherRows: false, CancellationToken.None);
            order.Add(Assert.Single(sortedPage.Vouchers).VoucherNumber);
            sortCursor = sortedPage.NextKey;
        } while (sortCursor is not null);
        Assert.Equal(["B", "D", "C", "A", "E"], order);
        var searched = await repository.ReadAsync(id, scenario, Context, null, new PageRequest(null, 10, null, "a"), countWholeVoucherRows: false, CancellationToken.None);
        Assert.Equal("A", Assert.Single(searched.Vouchers).VoucherNumber);
        // 傳票總額（借方總額）升冪：E 0、A 10000、C 10000、D 10000、B 15000；同值時穩定鍵升冪。
        var byTotal = await repository.ReadAsync(id, scenario, Context, null,
            new PageRequest(null, 10, new PageSort("voucherTotal", PageSortDirection.Ascending)), countWholeVoucherRows: false, CancellationToken.None);
        Assert.Equal(["E", "A", "C", "D", "B"], byTotal.Vouchers.Select(row => row.VoucherNumber).ToArray());
        Assert.Equal([0L, 10000L, 10000L, 10000L, 15000L], byTotal.Vouchers.Select(row => row.VoucherTotalScaled).ToArray());
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task ExtraFields_SupportListsPrefixesAndExplicitBlankChoices(string provider)
    {
        const string textId = "rde.aaaa0000aaaa0000aaaa0000aaaa0000";
        const string dateId = "rde.bbbb0000bbbb0000bbbb0000bbbb0000";
        const string moneyId = "rde.cccc0000cccc0000cccc0000cccc0000";
        var sql = Seed + $"""
          INSERT INTO gl_rde_value(entry_id,field_id,value_type,text_value,date_value,amount_scaled)
          SELECT entry_id,'{textId}','text','BEGIN%_END',NULL,NULL FROM gl WHERE source_row_number=1;
          INSERT INTO gl_rde_value(entry_id,field_id,value_type,text_value,date_value,amount_scaled)
          SELECT entry_id,'{textId}','text','other',NULL,NULL FROM gl WHERE source_row_number=2;
          INSERT INTO gl_rde_value(entry_id,field_id,value_type,text_value,date_value,amount_scaled)
          SELECT entry_id,'{dateId}','date',NULL,'2024-02-29',NULL FROM gl WHERE source_row_number=1;
          INSERT INTO gl_rde_value(entry_id,field_id,value_type,text_value,date_value,amount_scaled)
          SELECT entry_id,'{dateId}','date',NULL,'2025-12-31',NULL FROM gl WHERE source_row_number=2;
          INSERT INTO gl_rde_value(entry_id,field_id,value_type,text_value,date_value,amount_scaled)
          SELECT entry_id,'{moneyId}','amount',NULL,NULL,5000 FROM gl WHERE source_row_number=1;
          INSERT INTO gl_rde_value(entry_id,field_id,value_type,text_value,date_value,amount_scaled)
          SELECT entry_id,'{moneyId}','amount',NULL,NULL,-5000 FROM gl WHERE source_row_number=2;
          """;
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, sql);
        var context = Context with { RdeFields = [new(textId, "Extra text", "Extra text", "text"),
            new(dateId, "Extra date", "Extra date", "date"), new(moneyId, "Extra amount", "Extra amount", "amount")] };
        async Task Check(object rule, long expected)
        {
            var scenario = Scenario(JsonSerializer.Serialize(rule));
            Assert.Empty(FilterScenarioValidator.Validate(scenario, new(false, true, false)
                { MoneyScale = 100, RdeFields = context.RdeFields }));
            var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario, context, CancellationToken.None);
            Assert.Equal(expected, result.Count);
        }
        await Check(new { type = "fieldValue", fieldId = textId, @operator = "startsWith", value = "BEGIN%_" }, 1);
        await Check(new { type = "fieldValue", fieldId = textId, @operator = "endsWith", value = "_END" }, 1);
        await Check(new { type = "fieldValue", fieldId = textId, @operator = "notIn", values = new[] { "BEGIN%_END" } }, 1);
        await Check(new { type = "fieldValue", fieldId = textId, @operator = "notIn", values = new[] { "BEGIN%_END" }, includeBlank = true }, 11);
        await Check(new { type = "fieldValue", fieldId = dateId, @operator = "in", values = new[] { "2024-02-29" } }, 1);
        await Check(new { type = "fieldValue", fieldId = dateId, @operator = "notIn", values = new[] { "2024-02-29" } }, 1);
        await Check(new { type = "fieldValue", fieldId = dateId, @operator = "notIn", values = new[] { "2024-02-29" }, includeBlank = true }, 11);
        await Check(new { type = "fieldValue", fieldId = moneyId, @operator = "in", values = new[] { "-50" }, amountBasis = "signed" }, 1);
        await Check(new { type = "fieldValue", fieldId = moneyId, @operator = "in", values = new[] { "50" }, amountBasis = "absolute" }, 2);
        await Check(new { type = "fieldValue", fieldId = moneyId, @operator = "notBetween", from = "50", to = "50", amountBasis = "absolute" }, 0);
        await Check(new { type = "fieldValue", fieldId = moneyId, @operator = "notBetween", from = "50", to = "50", amountBasis = "absolute", includeBlank = true }, 10);
        await Check(new { type = "fieldValue", field = "amount", @operator = "in", values = Enumerable.Repeat("50", 101).ToArray(), amountBasis = "absolute" }, 4);
    }

    [Theory]
    [InlineData("""{"type":"fieldValue","field":"glDate","operator":"in","values":["2025-02-29"]}""")]
    [InlineData("""{"type":"fieldValue","field":"glDate","operator":"between","from":"2025-03-01","to":"2025-01-01"}""")]
    [InlineData("""{"type":"fieldValue","field":"amount","operator":"in","values":["bad"],"amountBasis":"signed"}""")]
    [InlineData("""{"type":"fieldValue","field":"amount","operator":"startsWith","value":"10","amountBasis":"signed"}""")]
    [InlineData("""{"type":"accountSide","drCr":"credit","categoryMode":"absent","categoryIds":[]}""")]
    [InlineData("""{"type":"accountSide","drCr":"credit","categoryMode":"unclassified","categoryIds":["builtin.cash"]}""")]
    [InlineData("""{"type":"fieldValue","field":"glDate","operator":"dayOfMonthIn","values":["32"]}""")]
    [InlineData("""{"type":"fieldValue","field":"glDate","operator":"dayOfMonthNotIn","values":["0","5"]}""")]
    [InlineData("""{"type":"fieldValue","field":"glDate","operator":"dayOfMonthIn","values":[]}""")]
    [InlineData("""{"type":"fieldValue","field":"amount","operator":"dayOfMonthIn","values":["5"],"amountBasis":"signed"}""")]
    [InlineData("""{"type":"fieldValue","field":"description","operator":"contains","values":["", "x"]}""")]
    public void InvalidConditions_ExplainWhatMustBeCorrected(string rule) =>
        Assert.NotEmpty(FilterScenarioValidator.Validate(Scenario(rule), new(false, true, false, HasAnyAccountCategory: true) { MoneyScale = 100 }));

    // 核心金額只比較絕對值。舊版 IDEA JET 的數字條件一律用 @Abs 比較，
    // 所以 amountBasis 只接受 absolute，沒有帶或帶 signed 都當成內容損毀；畫面送出時一律帶上 absolute。
    [Theory]
    [InlineData("""{"type":"fieldValue","field":"amount","operator":"greaterThan","value":"10"}""", "field_value_amount_basis_missing")]
    [InlineData("""{"type":"fieldValue","field":"amount","operator":"greaterThan","value":"10","amountBasis":"signed"}""", "field_value_core_amount_signed")]
    public void CoreAmountBasis_AcceptsOnlyAbsolute(string rule, string reason)
    {
        var context = new FilterValidationContext(false, true, false, HasAnyAccountCategory: true) { MoneyScale = 100 };
        Assert.Contains(
            FilterScenarioCorruption.ReasonsOf(FilterScenarioValidator.Validate(Scenario(rule), context)),
            item => item.Code == reason);
        Assert.Empty(FilterScenarioValidator.Validate(
            Scenario("""{"type":"fieldValue","field":"amount","operator":"greaterThan","value":"10","amountBasis":"absolute"}"""),
            context));
    }

}
