using System.Text.RegularExpressions;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class PrescreenExecutionInputTests
{
    private const string ThresholdFixtureSql =
        """
        INSERT INTO target_gl_entry
            (batch_id, source_row_number, document_number, line_item, post_date, approval_date,
             account_code, account_name, document_description, source_module, created_by, approved_by,
             is_manual, is_effective, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
        VALUES
            ('threshold', 1, 'Z3', '1', '2025-03-01', '2025-03-01',
             '7101', '三零命中', '一千點九九', NULL, 'Preparer', NULL,
             0, 1, 10009900, 10009900, 0, 'DEBIT'),
            ('threshold', 2, 'Z6', '1', '2025-03-02', '2025-03-02',
             '7102', '六零命中', '一百萬點一二三四', NULL, 'Preparer', NULL,
             0, 1, 10000001234, 10000001234, 0, 'DEBIT');
        """;

    [Fact]
    public void FromPlan_ConsumesAuditCoreGatesWithoutReDerivingRequestFlags()
    {
        var request = new PrescreenRequest(
            ProjectId: "p1",
            HasGlMapping: true,
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            MoneyScale: 10_000,
            SampleSeed: 48_271,
            RunId: "run",
            GeneratedUtc: DateTimeOffset.UnixEpoch,
            LastPeriodStart: "2025-12-31",
            // 預篩選收斂計畫階段四把核准日與建立人員兩個旗標換成可用欄位清單（2026-10-23）；不帶清單就不檢查欄位，
            // 等同原本兩個旗標都是 true，缺兩欄時改傳扣掉這兩欄的清單。
            // 第一次失敗：收據 20261023-031856475-04f811a69e3346428de91091f0d02655（編譯失敗）。
            AvailableGlFields: null,
            HasHolidays: true,
            HasAccountMapping: true,
            HasRevenue: true,
            HasCounterpart: true,
            HasAuthorizedPreparers: true,
            NonWorkingDays: [0, 6]);
        var reviewPlan = JetAuditProgram.Plan(
            new AuditCaseSnapshot(
                ProjectId: "p1",
                HasGlMapping: true,
                HasTbMapping: false,
                PeriodStart: "2025-01-01",
                PeriodEnd: "2025-12-31",
                MoneyScale: 10_000,
                SampleSeed: 48_271,
                LastPeriodStart: null,
                AvailableGlFields: WithoutApprovalDateAndCreatedBy,
                HasHolidays: false,
                HasAccountMapping: false,
                HasRevenue: false,
                HasCounterpart: false,
                HasAuthorizedPreparers: false,
                NonWorkingDays: [0, 6]),
            new AuditUserParameters(
                RunId: "review",
                GeneratedUtc: DateTimeOffset.UnixEpoch,
                SampleSize: 0,
                ActionName: "prescreen.run"));

        // 預篩選收斂計畫階段九：尾零門檻是規則定義的一部分，計畫與執行輸入不再帶它（第一次失敗：Focused 收據 20261024-195604731-a54ac30c8a994b46878d21f5b82967bf 之後的建置）。
        var execution = PrescreenExecutionInput.FromPlan(new PrescreenPlan(request, reviewPlan));

        Assert.False(execution.RunPostPeriodApproval);
        Assert.False(execution.RunCreatorSummary);
        Assert.False(execution.RunUnexpectedAccountPair);
        Assert.False(execution.RunWeekendApproval);
        Assert.False(execution.RunHolidayPosting);
        Assert.False(execution.RunHolidayApproval);
        Assert.False(execution.RunNonAuthorizedPreparer);
    }

    // 2026-10-22 技術債一輪：FromCompatibility 隨公開的相容入口刪除，原本檢查它的測試一併移除
    //（第一次失敗：Focused 收據 20261022-070441801-b717958da6bc461bb272ed374766bb78）。

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task LocalTypedExecution_UsesTheRegisteredSixZeroRule(string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider switch
        {
            "sqlite" => new SqliteProjectDatabase(folder),
            "duckdb" => new DuckDbProjectDatabase(folder),
            _ => throw new InvalidOperationException($"Unexpected provider '{provider}'.")
        };
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        await database.EnsureCreatedAsync(projectId, CancellationToken.None);

        await using (var connection = database.CreateConnection(projectId))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = ThresholdFixtureSql;
            await command.ExecuteNonQueryAsync();
        }

        IPrescreenFactsPort port = new LocalPrescreenRunRepository(database);
        await AssertTypedThresholdAsync(port, projectId);
    }

    [SqlServerFact]
    public async Task SqlServerTypedExecution_UsesTheRegisteredSixZeroRule()
    {
        await using var sql = Assert.IsType<TempSqlServerProject>(
            await TempSqlServerProject.TryCreateAsync());
        var transformed = Regex.Replace(
                ThresholdFixtureSql,
                "'[^']*'",
                match => "N" + match.Value)
            .Replace("INSERT INTO ", "INSERT INTO {s}.");

        await using (var connection = sql.Database.CreateConnection(sql.ProjectId))
        {
            await connection.OpenAsync();
            await using var command = sql.Database.CreateCommand(
                connection,
                sql.ProjectId,
                transformed);
            await command.ExecuteNonQueryAsync();
        }

        IPrescreenFactsPort port = new SqlServerPrescreenRunRepository(sql.Database);
        await AssertTypedThresholdAsync(port, sql.ProjectId);
    }

    private static async Task AssertTypedThresholdAsync(
        IPrescreenFactsPort port,
        string projectId)
    {
        var plan = JetAuditProgram.Plan(new PrescreenRequest(
            ProjectId: projectId,
            HasGlMapping: true,
            PeriodStart: "2025-01-01",
            PeriodEnd: "2025-12-31",
            MoneyScale: 10_000,
            SampleSeed: 48_271,
            RunId: "threshold-run",
            GeneratedUtc: DateTimeOffset.UnixEpoch,
            LastPeriodStart: null,
            AvailableGlFields: WithoutApprovalDateAndCreatedBy,
            HasHolidays: false,
            HasAccountMapping: false,
            HasRevenue: false,
            HasCounterpart: false,
            HasAuthorizedPreparers: false,
            NonWorkingDays: [0, 6]));

        var facts = await port.ExecuteAsync(plan, CancellationToken.None);

        // 預篩選收斂計畫階段九：尾零規則固定六個 0 且由登錄的述詞產生，計畫不再帶可改的門檻；夾具裡只有 Z6（1,000,000.1234）命中，
        // Z3（1,000.99）三個 0 不算。原本用門檻 3 期望兩筆都命中，第一次失敗：Focused 收據 20261024-195604731-a54ac30c8a994b46878d21f5b82967bf。
        Assert.Equal(1, facts.TrailingZerosCount);
    }

    private static readonly string[] WithoutApprovalDateAndCreatedBy = JetFieldCatalog.GlFields
        .Select(static field => field.SemanticIdentity)
        .Where(static field => field != JetFieldCatalog.GlDocDate && field != JetFieldCatalog.GlCreateBy)
        .ToArray();
}
