using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Legacy TableDef 等價欄位定義的本地 provider 持久化。
/// Oracle：master spec「Legacy 等價欄位定義持久化」的小型手算資料集；
/// reader 如何產生 cell evidence 由 reader-side tests 獨立驗證。
/// </summary>
public sealed class LegacyFieldDefinitionPersistenceTests
{
    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task FreshSchema_Replace_PersistsSourceDefinitionsAcrossReopen(string provider)
    {
        using var root = new TempProjectRoot();
        var folder = new JetProjectFolder(root.Path);
        var projectId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder.GetProjectDirectory(projectId));

        var database = CreateDatabase(provider, folder);
        IImportRepository repository = new LocalImportRepository(database);

        await repository.ReplaceBatchAsync(
            projectId,
            DatasetKind.Gl,
            Source("first.xlsx"),
            ["TextField", "NumberField", "DateField", "TimeField", "NamedEmpty"],
            Rows(
                ObservedRow(
                    2,
                    [("TextField", "hello"), ("NumberField", "12.34"),
                     ("DateField", "2024-01-01"), ("TimeField", "12:30:00")],
                    new("TextField", LegacyFieldKind.Text, 5, null),
                    new("NumberField", LegacyFieldKind.Number, 5, 2),
                    new("DateField", LegacyFieldKind.Date, 10, null),
                    new("TimeField", LegacyFieldKind.Time, 8, null))),
            CancellationToken.None);

        // 以新 database / facts-port instance 重開同一案件，證明不是 process memory cache。
        var reopenedDatabase = CreateDatabase(provider, folder);
        ILegacyFieldDefinitionFactsPort factsPort = new LocalFieldDefinitionFactsPort(reopenedDatabase);
        var definitions = await factsPort.ReadAsync(
            projectId,
            DatasetKind.Gl,
            LegacyFieldDefinitionScope.Source,
            CancellationToken.None);

        Assert.Collection(
            definitions,
            definition => AssertDefinition(definition, 1, "TextField", LegacyFieldKind.Text, 5, null),
            definition => AssertDefinition(definition, 2, "NumberField", LegacyFieldKind.Number, null, 2),
            definition => AssertDefinition(definition, 3, "DateField", LegacyFieldKind.Date, null, null),
            definition => AssertDefinition(definition, 4, "TimeField", LegacyFieldKind.Time, null, null),
            definition => AssertDefinition(definition, 5, "NamedEmpty", LegacyFieldKind.Text, 0, null));
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task Append_PreservesFirstOrdinalsAndMergesMaximaAndTypeConflict(string provider)
    {
        using var env = new Env(provider);

        await env.Repository.ReplaceBatchAsync(
            env.ProjectId,
            DatasetKind.Tb,
            Source("first.xlsx"),
            ["Label", "Amount", "Mixed", "NamedEmpty"],
            Rows(
                ObservedRow(
                    2,
                    [("Label", "abc"), ("Amount", "12.3"), ("Mixed", "2024-01-01")],
                    new("Label", LegacyFieldKind.Text, 3, null),
                    new("Amount", LegacyFieldKind.Number, 4, 1),
                    new("Mixed", LegacyFieldKind.Date, 10, null))),
            CancellationToken.None);

        // 第二來源刻意換欄序：定義 ordinal 仍必須沿用第一來源，而非 Append 的欄序。
        await env.Repository.AppendToBatchAsync(
            env.ProjectId,
            DatasetKind.Tb,
            Source("second.xlsx"),
            ["NamedEmpty", "Mixed", "Amount", "Label"],
            Rows(
                ObservedRow(
                    2,
                    [("Mixed", "alpha"), ("Amount", "1234.567"), ("Label", "long label")],
                    new("Mixed", LegacyFieldKind.Text, 5, null),
                    new("Amount", LegacyFieldKind.Number, 8, 3),
                    new("Label", LegacyFieldKind.Text, 10, null))),
            CancellationToken.None);

        var definitions = await env.FactsPort.ReadAsync(
            env.ProjectId,
            DatasetKind.Tb,
            LegacyFieldDefinitionScope.Source,
            CancellationToken.None);

        Assert.Collection(
            definitions,
            definition => AssertDefinition(definition, 1, "Label", LegacyFieldKind.Text, 10, null),
            definition => AssertDefinition(definition, 2, "Amount", LegacyFieldKind.Number, null, 3),
            definition => AssertDefinition(definition, 3, "Mixed", LegacyFieldKind.Text, 10, null),
            definition => AssertDefinition(definition, 4, "NamedEmpty", LegacyFieldKind.Text, 0, null));
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task Replace_EnumeratesRowsOnceAndRemovesPriorSourceDefinitions(string provider)
    {
        using var env = new Env(provider);

        await env.Repository.ReplaceBatchAsync(
            env.ProjectId,
            DatasetKind.Gl,
            Source("old.csv"),
            ["OldField"],
            Rows(ObservedRow(
                2,
                [("OldField", "old")],
                new TabularCellObservation("OldField", LegacyFieldKind.Text, 3, null))),
            CancellationToken.None);

        var singlePassRows = new ThrowOnSecondEnumerationRows(
            [ObservedRow(
                2,
                [("Replacement", "7.25")],
                new TabularCellObservation("Replacement", LegacyFieldKind.Number, 4, 2))]);

        await env.Repository.ReplaceBatchAsync(
            env.ProjectId,
            DatasetKind.Gl,
            Source("replacement.xlsx"),
            ["Replacement", "StillEmpty"],
            singlePassRows,
            CancellationToken.None);

        Assert.Equal(1, singlePassRows.EnumerationCount);

        var definitions = await env.FactsPort.ReadAsync(
            env.ProjectId,
            DatasetKind.Gl,
            LegacyFieldDefinitionScope.Source,
            CancellationToken.None);

        Assert.Collection(
            definitions,
            definition => AssertDefinition(definition, 1, "Replacement", LegacyFieldKind.Number, null, 2),
            definition => AssertDefinition(definition, 2, "StillEmpty", LegacyFieldKind.Text, 0, null));
        Assert.DoesNotContain(definitions, definition => definition.FieldName == "OldField");
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task Replace_LazyPlaceholderOutsideHeader_PreservesNativeObservation(string provider)
    {
        using var env = new Env(provider);

        await env.Repository.ReplaceBatchAsync(
            env.ProjectId,
            DatasetKind.Gl,
            Source("ragged.xlsx"),
            ["Named"],
            Rows(ObservedRow(
                2,
                [("Named", "x"), ("COL_3", "12.50")],
                new TabularCellObservation("Named", LegacyFieldKind.Text, 1, null),
                new TabularCellObservation("COL_3", LegacyFieldKind.Number, 4, 1))),
            CancellationToken.None);

        var definitions = await env.FactsPort.ReadAsync(
            env.ProjectId,
            DatasetKind.Gl,
            LegacyFieldDefinitionScope.Source,
            CancellationToken.None);

        Assert.Collection(
            definitions,
            definition => AssertDefinition(definition, 1, "Named", LegacyFieldKind.Text, 1, null),
            definition => AssertDefinition(definition, 2, "COL_3", LegacyFieldKind.Number, null, 1));
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task Append_LateColumnMismatch_RollsBackSourceDefinitions(string provider)
    {
        using var env = new Env(provider);

        await env.Repository.ReplaceBatchAsync(
            env.ProjectId,
            DatasetKind.Gl,
            Source("first.xlsx"),
            ["Named"],
            Rows(ObservedRow(
                2,
                [("Named", "before")],
                new TabularCellObservation("Named", LegacyFieldKind.Text, 6, null))),
            CancellationToken.None);
        var before = await env.FactsPort.ReadAsync(
            env.ProjectId, DatasetKind.Gl, LegacyFieldDefinitionScope.Source, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<JetActionException>(() => env.Repository.AppendToBatchAsync(
            env.ProjectId,
            DatasetKind.Gl,
            Source("ragged-append.xlsx"),
            ["Named"],
            Rows(ObservedRow(
                2,
                [("Named", "after"), ("COL_3", "12.50")],
                new TabularCellObservation("Named", LegacyFieldKind.Text, 5, null),
                new TabularCellObservation("COL_3", LegacyFieldKind.Number, 5, 2))),
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.ColumnMismatch, exception.Code);
        var after = await env.FactsPort.ReadAsync(
            env.ProjectId, DatasetKind.Gl, LegacyFieldDefinitionScope.Source, CancellationToken.None);
        Assert.Equal(before, after);
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task Append_WhenSourceDefinitionsAreMissing_RejectsInsteadOfBackfilling(string provider)
    {
        using var env = new Env(provider);
        var imported = await env.Repository.ReplaceBatchAsync(
            env.ProjectId,
            DatasetKind.Gl,
            Source("first.xlsx"),
            ["Named"],
            Rows(ObservedRow(
                2,
                [("Named", "before")],
                new TabularCellObservation("Named", LegacyFieldKind.Text, 6, null))),
            CancellationToken.None);

        await using (var connection = env.Database.CreateConnection(env.ProjectId))
        {
            await connection.OpenAsync();
            await using var delete = connection.CreateCommand();
            delete.CommandText =
                "DELETE FROM import_field_definition WHERE batch_id = @batchId AND definition_scope = 'source';";
            delete.AddWithValue("@batchId", imported.Batch.BatchId);
            await delete.ExecuteNonQueryAsync();
        }

        var rows = new ThrowOnSecondEnumerationRows(
            [ObservedRow(
                2,
                [("Named", "after")],
                new TabularCellObservation("Named", LegacyFieldKind.Text, 5, null))]);
        var exception = await Assert.ThrowsAsync<JetActionException>(() => env.Repository.AppendToBatchAsync(
            env.ProjectId,
            DatasetKind.Gl,
            Source("append.xlsx"),
            ["Named"],
            rows,
            CancellationToken.None));

        Assert.Equal(JetErrorCodes.InvalidProjectSchema, exception.Code);
        Assert.Equal(0, rows.EnumerationCount);
    }

    private static ILocalProjectDatabase CreateDatabase(string provider, JetProjectFolder folder) =>
        provider == ProjectDocument.DuckDbDatabaseProvider
            ? new DuckDbProjectDatabase(folder)
            : new SqliteProjectDatabase(folder);

    private static ImportSourceDescriptor Source(string fileName) =>
        new($@"C:\fixtures\{fileName}", fileName, null, null, null);

    private static StagingRow ObservedRow(
        int sourceRowNumber,
        IEnumerable<(string FieldName, string Value)> values,
        params TabularCellObservation[] observations) =>
        new(
            sourceRowNumber,
            values.ToDictionary(pair => pair.FieldName, pair => pair.Value, StringComparer.Ordinal))
        {
            FieldObservations = observations
        };

    private static async IAsyncEnumerable<StagingRow> Rows(params StagingRow[] rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }

    private static void AssertDefinition(
        LegacyFieldDefinition definition,
        int ordinal,
        string fieldName,
        LegacyFieldKind kind,
        int? textLength,
        int? decimalPlaces)
    {
        Assert.Equal(ordinal, definition.Ordinal);
        Assert.Equal(fieldName, definition.FieldName);
        Assert.Null(definition.Description);
        Assert.Equal(kind, definition.Kind);
        Assert.Equal(textLength, definition.TextLength);
        Assert.Equal(decimalPlaces, definition.DecimalPlaces);
    }

    private sealed class Env : IDisposable
    {
        private readonly TempProjectRoot _root = new();

        public Env(string provider)
        {
            var folder = new JetProjectFolder(_root.Path);
            var database = CreateDatabase(provider, folder);
            ProjectId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(folder.GetProjectDirectory(ProjectId));
            Repository = new LocalImportRepository(database);
            FactsPort = new LocalFieldDefinitionFactsPort(database);
            Database = database;
        }

        public string ProjectId { get; }
        public LocalImportRepository Repository { get; }
        public ILegacyFieldDefinitionFactsPort FactsPort { get; }
        public ILocalProjectDatabase Database { get; }

        public void Dispose() => _root.Dispose();
    }

    /// <summary>
    /// 任何企圖以第二遍來源列掃描計算欄位定義的實作都會直接失敗；
    /// 正確實作須在 staging writer 原本的 await foreach 中同步彙總 observation。
    /// </summary>
    private sealed class ThrowOnSecondEnumerationRows(IReadOnlyList<StagingRow> rows)
        : IAsyncEnumerable<StagingRow>
    {
        private int _enumerationCount;

        public int EnumerationCount => _enumerationCount;

        public IAsyncEnumerator<StagingRow> GetAsyncEnumerator(
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _enumerationCount) != 1)
            {
                throw new InvalidOperationException("來源列不得被第二次列舉。");
            }

            return Enumerate(rows).GetAsyncEnumerator(cancellationToken);
        }

        private static async IAsyncEnumerable<StagingRow> Enumerate(
            IReadOnlyList<StagingRow> sourceRows)
        {
            foreach (var row in sourceRows)
            {
                yield return row;
            }

            await Task.CompletedTask;
        }
    }
}
