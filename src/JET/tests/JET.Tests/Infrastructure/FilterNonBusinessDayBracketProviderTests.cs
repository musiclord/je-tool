using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// 2026-10-02 使用者裁定移除「情境層級」：KCT I 改成組內的條件括號（週末過帳 或 假日過帳）。
/// 舊版把 I 存成獨立條件組，使用者裁定不轉換舊情境，計算照原條件樹。這裡用合成資料在 SQLite 與 DuckDB
/// 確認：只有一個條件組時，新形狀的命中與舊形狀相同，加上同組其他條件時也相同。
/// </summary>
public sealed class FilterNonBusinessDayBracketProviderTests
{
    private const string Seed = """
        INSERT INTO target_gl_entry(batch_id,source_row_number,document_number,post_date,approval_date,
            account_code,document_description,amount_scaled,debit_amount_scaled,credit_amount_scaled,dr_cr,is_effective) VALUES
        ('nbd',1,'SAT-BLANK','2025-03-01','2025-03-01','A','',100,100,0,'DEBIT',1),
        ('nbd',2,'SUN-TEXT','2025-03-02','2025-03-02','A','SYNTHETIC',100,100,0,'DEBIT',1),
        ('nbd',3,'HOL-BLANK','2025-03-03','2025-03-03','A',NULL,100,100,0,'DEBIT',1),
        ('nbd',4,'TUE-BLANK','2025-03-04','2025-03-04','A','',100,100,0,'DEBIT',1),
        ('nbd',5,'WED-TEXT','2025-03-05','2025-03-05','A','SYNTHETIC',100,100,0,'DEBIT',1);
        INSERT INTO staging_calendar_raw_day(day_type,date) VALUES ('holiday','2025-03-03');
        """;

    private const string NewBracket = """
        {"join":"AND","type":"group","rules":[
          {"join":"AND","type":"prescreen","prescreenKey":"weekendPosting"},
          {"join":"OR","type":"prescreen","prescreenKey":"holidayPosting"}]}
        """;

    // 舊版前端送出的形狀：I 自成一組、組 join 固定 AND、兩條預篩選 join 都是 OR，排在所有條件組之後。
    private const string OldPresetGroup = """
        {"join":"AND","rules":[
          {"join":"OR","type":"prescreen","prescreenKey":"weekendPosting"},
          {"join":"OR","type":"prescreen","prescreenKey":"holidayPosting"}]}
        """;

    private const string BlankDescription = """{"join":"AND","type":"prescreen","prescreenKey":"blankDescription"}""";

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task NewBracket_MatchesTheOldSeparateGroup(string provider)
    {
        await using var fixture = await FilterPredicateProviderFixture.CreateLocalAsync(provider, Seed);
        var context = new FilterRuleContext(100, null, "2025-01-01", "2025-12-31");

        async Task<string[]> Hits(string groups)
        {
            var scenario = FilterScenarioPayloadParser.Parse(JsonElement.Parse("{\"groups\":[" + groups + "]}"), 100);
            Assert.Empty(FilterScenarioValidator.Validate(scenario, new(false, true, true), forSave: false));
            var result = await fixture.Repository.PreviewAsync(fixture.ProjectId, scenario, context, CancellationToken.None);
            return result.PreviewRows.Select(row => row.DocumentNumber!).Order(StringComparer.Ordinal).ToArray();
        }

        // 只有非營業日：一個條件組裡只有 I。
        var newOnly = await Hits("{\"join\":\"AND\",\"rules\":[" + NewBracket + "]}");
        var oldOnly = await Hits(OldPresetGroup);
        Assert.Equal(new[] { "HOL-BLANK", "SAT-BLANK", "SUN-TEXT" }, newOnly);
        Assert.Equal(newOnly, oldOnly);

        // 空白摘要且非營業日：新形狀在同一組，舊形狀是第二組以「且」接上。
        var newWithBlank = await Hits("{\"join\":\"AND\",\"rules\":[" + BlankDescription + "," + NewBracket + "]}");
        var oldWithBlank = await Hits("{\"join\":\"OR\",\"rules\":[" + BlankDescription + "]}," + OldPresetGroup);
        Assert.Equal(new[] { "HOL-BLANK", "SAT-BLANK" }, newWithBlank);
        Assert.Equal(newWithBlank, oldWithBlank);
    }
}
