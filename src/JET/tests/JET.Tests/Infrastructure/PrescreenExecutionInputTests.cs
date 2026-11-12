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
        INSERT INTO gl
            (batch_id, source_row_number, voucher_number, line_item, gl_date, approval_date,
             account_number, account_name, description, source_module, created_by, approved_by,
             is_manual, is_excluded, amount_scaled, debit_amount_scaled, credit_amount_scaled, dr_cr)
        VALUES
            ('threshold', 1, 'Z3', '1', '2025-03-01', '2025-03-01',
             '7101', '三零命中', '一千點九九', NULL, 'Preparer', NULL,
             0, 0, 10009900, 10009900, 0, 'DEBIT'),
            ('threshold', 2, 'Z6', '1', '2025-03-02', '2025-03-02',
             '7102', '六零命中', '一百萬點一二三四', NULL, 'Preparer', NULL,
             0, 0, 10000001234, 10000001234, 0, 'DEBIT');
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
            ResultId: "run",
            GeneratedUtc: DateTimeOffset.UnixEpoch,
            PrepStartDate: "2025-12-31",
            // 不帶可用欄位清單就不檢查欄位，等同核准日與建立人員兩欄都可用；
            // 要表示缺這兩欄時，傳扣掉這兩欄的清單。
            AvailableGlFields: null,
            HasHolidays: true,
            HasAccountMapping: true,
            HasRevenue: true,
            HasCounterpart: true,
            HasAuthorizedPreparers: true,
            NonWorkingDays: [0, 6])
        {
            // 0 的個數照 IDEA 版由 GL 算出，請求要帶 0 的個數；這裡用 5，下面核對執行輸入照請求帶出。
            ZerosThreshold = 5
        };
        var reviewPlan = JetAuditProgram.Plan(
            new AuditProjectSnapshot(
                ProjectId: "p1",
                HasGlMapping: true,
                HasTbMapping: false,
                PeriodStart: "2025-01-01",
                PeriodEnd: "2025-12-31",
                MoneyScale: 10_000,
                SampleSeed: 48_271,
                PrepStartDate: null,
                AvailableGlFields: WithoutApprovalDateAndCreatedBy,
                HasHolidays: false,
                HasAccountMapping: false,
                HasRevenue: false,
                HasCounterpart: false,
                HasAuthorizedPreparers: false,
                NonWorkingDays: [0, 6]),
            new AuditUserParameters(
                ResultId: "review",
                GeneratedUtc: DateTimeOffset.UnixEpoch,
                SampleSize: 0,
                ActionName: "prescreen.run"));

        var execution = PrescreenExecutionInput.FromPlan(new PrescreenPlan(request, reviewPlan));

        Assert.False(execution.RunPrepPeriodApproval);
        Assert.False(execution.RunPreparerSummary);
        Assert.False(execution.RunUnexpectedAccountPair);
        Assert.False(execution.RunWeekendApproval);
        Assert.False(execution.RunHolidayPosting);
        Assert.False(execution.RunHolidayApproval);
        Assert.False(execution.RunNonAuthorizedPreparer);
        Assert.Equal(5, execution.ZerosThreshold);
    }

    [Theory]
    [InlineData("sqlite")]
    public async Task LocalTypedExecution_UsesTheRegisteredSixZeroRule(string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        ILocalProjectDatabase database = provider switch
        {
            "sqlite" => new SqliteProjectDatabase(folder),
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
            ResultId: "threshold-run",
            GeneratedUtc: DateTimeOffset.UnixEpoch,
            PrepStartDate: null,
            AvailableGlFields: WithoutApprovalDateAndCreatedBy,
            HasHolidays: false,
            HasAccountMapping: false,
            HasRevenue: false,
            HasCounterpart: false,
            HasAuthorizedPreparers: false,
            NonWorkingDays: [0, 6])
        {
            // 0 的個數照 IDEA 版由 GL 算出，請求要帶 0 的個數。
            ZerosThreshold = 6
        });

        var facts = await port.ExecuteAsync(plan, CancellationToken.None);

        // 一條規則只有一個定義，尾零規則由登錄的述詞產生，這裡的請求帶六個 0；夾具裡只有 Z6（1,000,000.1234）命中，
        // Z3（1,000.99）三個 0 不算。
        Assert.Equal(1, facts.TrailingZerosCount);
    }

    private static readonly string[] WithoutApprovalDateAndCreatedBy = JetFieldCatalog.GlFields
        .Select(static field => field.SemanticIdentity)
        .Where(static field => field != JetFieldCatalog.GlApprovalDate && field != JetFieldCatalog.GlCreatedBy)
        .ToArray();
}
