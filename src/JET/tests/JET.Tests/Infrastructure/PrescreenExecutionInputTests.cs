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
            HasApprovalDate: true,
            HasCreatedBy: true,
            HasHolidays: true,
            HasAccountMapping: true,
            HasRevenue: true,
            HasCounterpart: true,
            HasAuthorizedPreparers: true,
            NonWorkingDays: [0, 6], HasVoucherDate: true);
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
                HasApprovalDate: false,
                HasCreatedBy: false,
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

        var execution = PrescreenExecutionInput.FromPlan(
            new PrescreenPlan(
                request,
                reviewPlan,
                ZerosThreshold: 3));

        Assert.Equal(3, execution.ZerosThreshold);
        Assert.False(execution.RunPostPeriodApproval);
        Assert.False(execution.RunCreatorSummary);
        Assert.False(execution.RunUnexpectedAccountPair);
        Assert.False(execution.RunWeekendApproval);
        Assert.False(execution.RunHolidayPosting);
        Assert.False(execution.RunHolidayApproval);
        Assert.False(execution.RunNonAuthorizedPreparer);
    }

    [Fact]
    public void FromCompatibility_PreservesLegacyGateSemantics()
    {
        var execution = PrescreenExecutionInput.FromCompatibility(
            new PrescreenRunInput(
                LastPeriodStart: "2025-12-31",
                PeriodStart: "2025-01-01",
                PeriodEnd: "2025-12-31",
                HasApprovalDate: true,
                HasCreatedBy: false,
                HasHolidays: true,
                RunUnexpectedAccountPair: false,
                HasAuthorizedPreparers: true,
                MoneyScale: 10_000,
                NonWorkingDays: [0, 6]));

        Assert.True(execution.RunPostPeriodApproval);
        Assert.False(execution.RunCreatorSummary);
        Assert.False(execution.RunUnexpectedAccountPair);
        Assert.True(execution.RunWeekendApproval);
        Assert.True(execution.RunHolidayPosting);
        Assert.True(execution.RunHolidayApproval);
        Assert.True(execution.RunNonAuthorizedPreparer);
        Assert.Equal(TrailingZeroThreshold.DefaultZerosThreshold, execution.ZerosThreshold);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("duckdb")]
    public async Task LocalTypedExecution_ConsumesThresholdFromPlan(string provider)
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
    public async Task SqlServerTypedExecution_ConsumesThresholdFromPlan()
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
            HasApprovalDate: false,
            HasCreatedBy: false,
            HasHolidays: false,
            HasAccountMapping: false,
            HasRevenue: false,
            HasCounterpart: false,
            HasAuthorizedPreparers: false,
            NonWorkingDays: [0, 6], HasVoucherDate: true)) with
        {
            ZerosThreshold = 3
        };

        var facts = await port.ExecuteAsync(plan, CancellationToken.None);

        Assert.Equal(2, facts.TrailingZerosCount);
    }
}
