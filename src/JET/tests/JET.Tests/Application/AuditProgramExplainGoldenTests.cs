using System.Text.Json;
using JET.AuditCore;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// Golden oracle：以正式 demo 匯入／配對後的真 SQLite 母體，直接走
/// Plan → ExecuteAsync → Finalize → Explain，釘住資料驗證家族的 IDEA-log 式敘述。
/// </summary>
public sealed class AuditProgramExplainGoldenTests(DemoProjectFixture fixture)
    : IClassFixture<DemoProjectFixture>
{
    [Fact]
    public async Task Explain_DemoValidationRun_MatchesGoldenText()
    {
        var projectPath = Path.Combine(fixture.Host.ProjectsRoot, fixture.ProjectId, "project.json");
        using var projectJson = JsonDocument.Parse(await File.ReadAllTextAsync(projectPath));
        var project = projectJson.RootElement;

        var plan = JetAuditProgram.Plan(new ValidationRequest(
            fixture.ProjectId,
            HasGlMapping: true,
            HasTbMapping: true,
            project.GetProperty("periodStart").GetString()!,
            project.GetProperty("periodEnd").GetString()!,
            project.GetProperty("moneyScale").GetInt32(),
            project.GetProperty("sampleSeed").GetInt64(),
            "audit-core-explain-golden",
            new DateTimeOffset(2025, 12, 31, 12, 0, 0, TimeSpan.Zero),
            SampleSize: 59));
        IValidationFactsPort factsPort = new LocalValidationRunRepository(
            new SqliteProjectDatabase(new JetProjectFolder(fixture.Host.ProjectsRoot)));

        var facts = await JetAuditProgram.ExecuteAsync(
            plan,
            factsPort,
            CancellationToken.None);
        var result = JetAuditProgram.Finalize(plan, facts);
        var explanation = JetAuditProgram.Explain(result);

        var expected = string.Join('\n',
            "查核期間參數：@periodStart=2025-01-01；@periodEnd=2025-12-31。",
            "完整性測試（completeness_test）：na；計數 0；N/A 原因：無（已執行但未發現差異）。",
            "借貸不平測試（doc_balance_test）：na；計數 0；N/A 原因：無（已執行但未發現不平傳票）。",
            "INF 抽樣測試（inf_sampling_test）：V；計數 59；N/A 原因：無。",
            "空值紀錄測試（null_records_test）：V；子計數合計 58；N/A 原因：無。",
            "完整性 SQL 要點：JE 僅取查核期間並依科目彙總；TB 取全部本期變動額；以 LEFT JOIN 加 UNION ALL 模擬 FULL OUTER JOIN；差異為 TB 減 GL，並標記 GL 有而 TB 無的科目。");

        Assert.Equal(expected, explanation);
    }

    [Fact]
    public async Task Explain_DemoPrescreenRun_MatchesGoldenText()
    {
        var projectPath = Path.Combine(fixture.Host.ProjectsRoot, fixture.ProjectId, "project.json");
        using var projectJson = JsonDocument.Parse(await File.ReadAllTextAsync(projectPath));
        var project = projectJson.RootElement;
        var demoProject = fixture.Demo.GetProperty("project");

        var plan = JetAuditProgram.Plan(
            new PrescreenRequest(
                fixture.ProjectId,
                HasGlMapping: true,
                project.GetProperty("periodStart").GetString()!,
                project.GetProperty("periodEnd").GetString()!,
                project.GetProperty("moneyScale").GetInt32(),
                project.GetProperty("sampleSeed").GetInt64(),
                RunId: "audit-core-prescreen-explain-golden",
                GeneratedUtc: new DateTimeOffset(2025, 12, 31, 12, 0, 0, TimeSpan.Zero),
                LastPeriodStart: demoProject.GetProperty("lastPeriodStart").GetString(),
                HasApprovalDate: true,
                HasCreatedBy: true,
                HasHolidays: true,
                HasAccountMapping: true,
                HasRevenue: true,
                HasCounterpart: true,
                HasAuthorizedPreparers: true,
                NonWorkingDays: [0, 6]));
        IPrescreenFactsPort factsPort =
            new LocalPrescreenRunRepository(
                new SqliteProjectDatabase(new JetProjectFolder(fixture.Host.ProjectsRoot)));

        var facts = await JetAuditProgram.ExecuteAsync(plan, factsPort, CancellationToken.None);
        var result = JetAuditProgram.Finalize(plan, facts);
        var explanation = JetAuditProgram.Explain(result);

        var expected = string.Join('\n',
            "預篩選查核期間：@periodStart=2025-01-01；@periodEnd=2025-12-31。",
            "期末財報準備日後核准之分錄（post_period_approval）：V；計數 40；N/A 原因：無。",
            "分錄摘要出現特定描述（suspicious_keywords）：V；計數 25；N/A 原因：無。",
            "未預期出現之特定借貸組合（unexpected_account_pair）：V；計數 30；N/A 原因：無。",
            "分錄金額中有連續零的尾數（trailing_zeros）：V；計數 30；N/A 原因：無。",
            "依分錄編製者彙總（creator_summary）：V；彙總列數 8；N/A 原因：無。",
            "較少使用之科目（rare_accounts）：V；科目數 17；N/A 原因：無。",
            "週末過帳（weekend_posting）：V；計數 24；N/A 原因：無。",
            "週末核准（weekend_approval）：V；計數 20；N/A 原因：無。",
            "假日過帳（holiday_posting）：V；計數 28；N/A 原因：無。",
            "假日核准（holiday_approval）：V；計數 16；N/A 原因：無。",
            "摘要空白（blank_description）：V；計數 18；N/A 原因：無。",
            "回溯過帳(過帳日早於傳票日)（backdated_posting）：V；計數 44；N/A 原因：無。",
            "非授權編製人員（non_authorized_preparer）：V；計數 32；N/A 原因：無。",
            "低頻編製者（low_frequency_preparer）：V；計數 10；N/A 原因：無。",
            "低頻科目（low_frequency_account）：V；計數 6；N/A 原因：無。",
            "預篩選 SQL 要點：JE 母體限查核期間；比較值以參數綁定；非工作日採驗證後的 0–6 白名單展開；科目組合使用 EXISTS／NOT EXISTS；週末與假日分別依非工作日與日曆判定；低頻規則以 GROUP BY／HAVING 計數。");

        Assert.Equal(expected, explanation);
    }
}
