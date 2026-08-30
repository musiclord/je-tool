using System.Data.Common;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class GlProjectionQualityProviderTests
{
    private static readonly DateOnly PeriodStart = new(2025, 1, 1);
    private static readonly DateOnly PeriodEnd = new(2025, 12, 31);
    private static readonly GlPostingStatusPolicy PostingPolicy = new(["POSTED"], IncludeBlank: false);

    [Fact]
    public void RdeSecondPassBuffer_IsCellBoundedAndDropsUnusedSourceColumns()
    {
        var spec = QualitySpec(includeManual: true, includeRde: true);
        var required = GlProjectionSourceBuffer.RequiredColumns(spec);
        var pageSize = GlProjectionSourceBuffer.EntryPageSize(required.Count);
        var selected = GlProjectionSourceBuffer.SelectRequiredValues(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["doc"] = "JE-1",
                ["customText"] = "value",
                ["unused"] = "must-not-be-buffered"
            },
            required);

        Assert.InRange(pageSize, 1, GlProjectionSourceBuffer.MaxEntryRows);
        Assert.True(pageSize * required.Count <= GlProjectionSourceBuffer.MaxBufferedCells);
        Assert.Equal("JE-1", selected["doc"]);
        Assert.Equal("value", selected["customText"]);
        Assert.DoesNotContain("unused", selected.Keys);
    }

    [Fact]
    public async Task Sqlite_TypedProjectionQuality_MatchesOracle()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        var database = new SqliteProjectDatabase(folder);
        await AssertTypedOracleAsync(
            new LocalImportRepository(database),
            new LocalGlRepository(database),
            new LocalSourceQualityPageRepository(database),
            projectId,
            () => database.CreateConnection(projectId),
            string.Empty);
    }

    [Fact]
    public async Task DuckDb_TypedProjectionQuality_MatchesOracle()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        var database = new DuckDbProjectDatabase(folder);
        await AssertTypedOracleAsync(
            new LocalImportRepository(database),
            new LocalGlRepository(database),
            new LocalSourceQualityPageRepository(database),
            projectId,
            () => database.CreateConnection(projectId),
            string.Empty);
    }

    [SqlServerFact]
    public async Task SqlServer_TypedProjectionQuality_MatchesOracle()
    {
        await using var project = Assert.IsType<TempSqlServerProject>(
            await TempSqlServerProject.TryCreateAsync());
        await AssertTypedOracleAsync(
            new SqlServerImportRepository(project.Database),
            new SqlServerGlRepository(project.Database),
            new SqlServerSourceQualityPageRepository(project.Database),
            project.ProjectId,
            () => project.Database.CreateConnection(project.ProjectId),
            SqlServerProjectSchema.QualifierFor(project.ProjectId));
    }

    [Fact]
    public async Task Sqlite_PostingExcludedBadManual_RollsBackWholeProjection()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        var database = new SqliteProjectDatabase(folder);
        await AssertExcludedBadRowRollbackAsync(
            new LocalImportRepository(database),
            new LocalGlRepository(database),
            projectId,
            () => database.CreateConnection(projectId),
            string.Empty);
    }

    [Fact]
    public async Task DuckDb_PostingExcludedBadManual_RollsBackWholeProjection()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        var database = new DuckDbProjectDatabase(folder);
        await AssertExcludedBadRowRollbackAsync(
            new LocalImportRepository(database),
            new LocalGlRepository(database),
            projectId,
            () => database.CreateConnection(projectId),
            string.Empty);
    }

    [SqlServerFact]
    public async Task SqlServer_PostingExcludedBadManual_RollsBackWholeProjection()
    {
        await using var project = Assert.IsType<TempSqlServerProject>(
            await TempSqlServerProject.TryCreateAsync());
        await AssertExcludedBadRowRollbackAsync(
            new SqlServerImportRepository(project.Database),
            new SqlServerGlRepository(project.Database),
            project.ProjectId,
            () => project.Database.CreateConnection(project.ProjectId),
            SqlServerProjectSchema.QualifierFor(project.ProjectId));
    }

    [Fact]
    public async Task Sqlite_PostingExcludedMalformedRde_RollsBackWholeProjection()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        var database = new SqliteProjectDatabase(folder);
        await AssertExcludedMalformedRdeRollbackAsync(
            new LocalImportRepository(database),
            new LocalGlRepository(database),
            projectId,
            () => database.CreateConnection(projectId),
            string.Empty);
    }

    [Fact]
    public async Task DuckDb_PostingExcludedMalformedRde_RollsBackWholeProjection()
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
        var database = new DuckDbProjectDatabase(folder);
        await AssertExcludedMalformedRdeRollbackAsync(
            new LocalImportRepository(database),
            new LocalGlRepository(database),
            projectId,
            () => database.CreateConnection(projectId),
            string.Empty);
    }

    [SqlServerFact]
    public async Task SqlServer_PostingExcludedMalformedRde_RollsBackWholeProjection()
    {
        await using var project = Assert.IsType<TempSqlServerProject>(
            await TempSqlServerProject.TryCreateAsync());
        await AssertExcludedMalformedRdeRollbackAsync(
            new SqlServerImportRepository(project.Database),
            new SqlServerGlRepository(project.Database),
            project.ProjectId,
            () => project.Database.CreateConnection(project.ProjectId),
            SqlServerProjectSchema.QualifierFor(project.ProjectId));
    }

    private static async Task AssertTypedOracleAsync(
        IImportRepository imports,
        IGlRepository repository,
        ISourceQualityPageRepository sourceQuality,
        string projectId,
        Func<DbConnection> connectionFactory,
        string qualifier)
    {
        var batch = (await imports.ReplaceBatchAsync(
            projectId,
            DatasetKind.Gl,
            Source(),
            Columns(),
            TypedRows(),
            CancellationToken.None)).Batch;
        var spec = QualitySpec(includeManual: true, includeRde: true);

        var result = await repository.ProjectStagingToTargetAsync(
            projectId,
            batch.BatchId,
            spec,
            ProjectDocument.DefaultMoneyScale,
            DateParseOptions.Default,
            PeriodStart,
            PeriodEnd,
            postingStatusMapped: true,
            PostingPolicy,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        Assert.Equal(2, result.ProjectedRowCount);
        Assert.Equal(0, result.TotalErrorCount);
        var findings = await sourceQuality.GetPageAsync(
            projectId,
            new PageRequest(null, 10),
            CancellationToken.None);
        var finding = Assert.Single(findings.Rows);
        Assert.Equal("nullPostDate", finding.Category);
        Assert.Equal(3, finding.SourceRowNumber);
        await using var connection = connectionFactory();
        await connection.OpenAsync();
        Assert.Equal(2, await ScalarAsync(
            connection,
            $"SELECT COUNT(*) FROM {Table(qualifier, "target_gl_entry")} "
            + "WHERE approval_date = post_date OR (approval_date IS NULL AND post_date IS NULL);"));
        Assert.Equal(1, await ScalarAsync(
            connection,
            $"SELECT COUNT(*) FROM {Table(qualifier, "target_gl_entry")} WHERE is_manual = 1;"));
        Assert.Equal(3, await ScalarAsync(
            connection,
            $"SELECT COUNT(*) FROM {Table(qualifier, "config_gl_rde_field")};"));
        Assert.Equal(3, await ScalarAsync(
            connection,
            $"SELECT COUNT(*) FROM {Table(qualifier, "target_gl_rde_value")};"));
        Assert.Equal(123_457, await ScalarAsync(
            connection,
            $"SELECT amount_scaled FROM {Table(qualifier, "target_gl_rde_value")} WHERE value_type = 'money';"));
    }

    private static async Task AssertExcludedBadRowRollbackAsync(
        IImportRepository imports,
        IGlRepository repository,
        string projectId,
        Func<DbConnection> connectionFactory,
        string qualifier)
    {
        var batch = (await imports.ReplaceBatchAsync(
            projectId,
            DatasetKind.Gl,
            Source(),
            Columns(),
            ExcludedBadRows(),
            CancellationToken.None)).Batch;

        var baseline = await repository.ProjectStagingToTargetAsync(
            projectId,
            batch.BatchId,
            QualitySpec(includeManual: false, includeRde: false),
            ProjectDocument.DefaultMoneyScale,
            DateParseOptions.Default,
            PeriodStart,
            PeriodEnd,
            postingStatusMapped: true,
            PostingPolicy,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);
        Assert.Equal(62, baseline.ProjectedRowCount);

        var failed = await repository.ProjectStagingToTargetAsync(
            projectId,
            batch.BatchId,
            QualitySpec(includeManual: true, includeRde: false),
            ProjectDocument.DefaultMoneyScale,
            DateParseOptions.Default,
            PeriodStart,
            PeriodEnd,
            postingStatusMapped: true,
            PostingPolicy,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        Assert.Equal(0, failed.ProjectedRowCount);
        Assert.Equal(60, failed.TotalErrorCount);
        Assert.Equal(50, failed.Errors.Count);
        Assert.Equal("manual", failed.Errors[0].Field);
        await using var connection = connectionFactory();
        await connection.OpenAsync();
        Assert.Equal(62, await ScalarAsync(
            connection,
            $"SELECT COUNT(*) FROM {Table(qualifier, "target_gl_entry")};"));
        Assert.Equal(0, await ScalarAsync(
            connection,
            $"SELECT COUNT(*) FROM {Table(qualifier, "config_gl_rde_field")};"));
    }

    private static async Task AssertExcludedMalformedRdeRollbackAsync(
        IImportRepository imports,
        IGlRepository repository,
        string projectId,
        Func<DbConnection> connectionFactory,
        string qualifier)
    {
        var batch = (await imports.ReplaceBatchAsync(
            projectId,
            DatasetKind.Gl,
            Source(),
            Columns(),
            ExcludedMalformedRdeRows(),
            CancellationToken.None)).Batch;

        var baseline = await repository.ProjectStagingToTargetAsync(
            projectId,
            batch.BatchId,
            QualitySpec(includeManual: false, includeRde: false),
            ProjectDocument.DefaultMoneyScale,
            DateParseOptions.Default,
            PeriodStart,
            PeriodEnd,
            postingStatusMapped: true,
            PostingPolicy,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);
        Assert.Equal(4, baseline.ProjectedRowCount);

        var failed = await repository.ProjectStagingToTargetAsync(
            projectId,
            batch.BatchId,
            QualitySpec(includeManual: false, includeRde: true),
            ProjectDocument.DefaultMoneyScale,
            DateParseOptions.Default,
            PeriodStart,
            PeriodEnd,
            postingStatusMapped: true,
            PostingPolicy,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        Assert.Equal(0, failed.ProjectedRowCount);
        Assert.Equal(2, failed.TotalErrorCount);
        Assert.Equal(["customDate", "customMoney"], failed.Errors.Select(static error => error.Field));
        await using var connection = connectionFactory();
        await connection.OpenAsync();
        Assert.Equal(4, await ScalarAsync(
            connection,
            $"SELECT COUNT(*) FROM {Table(qualifier, "target_gl_entry")};"));
        Assert.Equal(0, await ScalarAsync(
            connection,
            $"SELECT COUNT(*) FROM {Table(qualifier, "config_gl_rde_field")};"));
        Assert.Equal(0, await ScalarAsync(
            connection,
            $"SELECT COUNT(*) FROM {Table(qualifier, "target_gl_rde_value")};"));
    }

    private static GlMappingSpec QualitySpec(bool includeManual, bool includeRde)
    {
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GlMappingKeys.DocNum] = "doc",
            [GlMappingKeys.PostDate] = "post",
            [GlMappingKeys.AccNum] = "account",
            [GlMappingKeys.AccName] = "accountName",
            [GlMappingKeys.Description] = "description",
            [GlMappingKeys.Amount] = "amount",
            [GlMappingKeys.PostingStatus] = "status"
        };
        if (includeManual)
        {
            mapping[GlMappingKeys.Manual] = "manual";
        }

        var options = GlMappingOptions.NormalizeLegacy(mapping) with
        {
            ApprovalDateMode = ApprovalDateModeNames.SameAsPostDate,
            PostingStatusPolicy = PostingPolicy,
            ManualAutoPolicy = new GlManualAutoPolicy(["M"], ["A"]),
            RdeFields = includeRde
                ?
                [
                    new GlRdeFieldMetadata("rde.00000000000000000000000000000001", "customText", "Custom Text", "text"),
                    new GlRdeFieldMetadata("rde.00000000000000000000000000000002", "customDate", "Custom Date", "date"),
                    new GlRdeFieldMetadata("rde.00000000000000000000000000000003", "customMoney", "Custom Money", "money")
                ]
                : []
        };
        return new GlMappingSpec(mapping, GlAmountMode.SignedAmount) { Options = options };
    }

    private static IReadOnlyList<string> Columns() =>
        [
            "doc", "post", "account", "accountName", "description", "amount", "status", "manual",
            "customText", "customDate", "customMoney"
        ];

    private static ImportSourceDescriptor Source() => new(
        @"C:\synthetic-projection-quality.xlsx",
        "synthetic-projection-quality.xlsx",
        null,
        null,
        null);

    private static async IAsyncEnumerable<StagingRow> TypedRows()
    {
        yield return Row(2, "2025-01-15", "100", "POSTED", " M ", "  raw text  ", "2025/01/17", "12.34567");
        yield return Row(3, "", "-100", "VOID", "a", "   ", "", "");
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<StagingRow> ExcludedBadRows()
    {
        yield return Row(2, "2025-01-15", "100", "POSTED", "M", "", "", "");
        yield return Row(3, "2025-01-15", "-100", "POSTED", "A", "", "", "");
        for (var index = 0; index < 60; index++)
        {
            yield return Row(
                index + 4,
                "2025-01-15",
                index % 2 == 0 ? "1" : "-1",
                "VOID",
                "UNKNOWN",
                "",
                "",
                "");
        }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<StagingRow> ExcludedMalformedRdeRows()
    {
        yield return Row(2, "2025-01-15", "100", "POSTED", "", "", "", "");
        yield return Row(3, "2025-01-15", "-100", "POSTED", "", "", "", "");
        yield return Row(4, "2025-01-15", "1", "VOID", "", "", "not-a-date", "");
        yield return Row(5, "2025-01-15", "-1", "VOID", "", "", "", "not-money");
        await Task.CompletedTask;
    }

    private static StagingRow Row(
        int sourceRow,
        string postDate,
        string amount,
        string status,
        string manual,
        string customText,
        string customDate,
        string customMoney) =>
        new(sourceRow, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["doc"] = $"JE-{sourceRow}",
            ["post"] = postDate,
            ["account"] = sourceRow == 2 ? "1000" : "2000",
            ["accountName"] = sourceRow == 2 ? "Debit" : "Credit",
            ["description"] = "synthetic",
            ["amount"] = amount,
            ["status"] = status,
            ["manual"] = manual,
            ["customText"] = customText,
            ["customDate"] = customDate,
            ["customMoney"] = customMoney
        });

    private static string Table(string qualifier, string name) =>
        string.IsNullOrEmpty(qualifier) ? name : qualifier + name;

    private static async Task<long> ScalarAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
