using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>Legacy 欄位定義在 fresh SQL Server project schema 的真實持久化／reopen gate。</summary>
public sealed class SqlServerLegacyFieldDefinitionTests
{
    [SqlServerFact]
    public async Task ReplaceAppendAndReopen_MatchSourceDefinitionContract()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);

        var repository = new SqlServerImportRepository(project.Database);
        var source = new ImportSourceDescriptor(@"C:\first.xlsx", "first.xlsx", "Data", null, null);
        var longHeader = new string('長', 512);
        var columns = new[] { "Code", "Amount", "Empty", longHeader };

        await repository.ReplaceBatchAsync(
            project.ProjectId,
            DatasetKind.Gl,
            source,
            columns,
            Rows(
                ObservedRow(2, ("Code", "A", LegacyFieldKind.Text, 1, null),
                    ("Amount", "1.25", LegacyFieldKind.Number, 4, 2),
                    (longHeader, "long-name", LegacyFieldKind.Text, 9, null))),
            CancellationToken.None);

        await repository.AppendToBatchAsync(
            project.ProjectId,
            DatasetKind.Gl,
            source with { FilePath = @"C:\second.xlsx", FileName = "second.xlsx" },
            columns,
            Rows(
                ObservedRow(2, ("Code", "LONG", LegacyFieldKind.Text, 4, null),
                    ("Amount", "text", LegacyFieldKind.Text, 4, null),
                    (longHeader, "x", LegacyFieldKind.Text, 1, null))),
            CancellationToken.None);

        ILegacyFieldDefinitionFactsPort reopened = new SqlServerFieldDefinitionFactsPort(project.Database);
        var definitions = await reopened.ReadAsync(
            project.ProjectId,
            DatasetKind.Gl,
            LegacyFieldDefinitionScope.Source,
            CancellationToken.None);

        Assert.Collection(
            definitions,
            item => Assert.Equal(new LegacyFieldDefinition(1, "Code", null, LegacyFieldKind.Text, 4, null), item),
            item => Assert.Equal(new LegacyFieldDefinition(2, "Amount", null, LegacyFieldKind.Text, 4, null), item),
            item => Assert.Equal(new LegacyFieldDefinition(3, "Empty", null, LegacyFieldKind.Text, 0, null), item),
            item => Assert.Equal(new LegacyFieldDefinition(4, longHeader, null, LegacyFieldKind.Text, 9, null), item));
    }

    [SqlServerFact]
    public async Task GlAndTbProjection_PersistActualTargetAndDerivedDefinitions()
    {
        await using var project = await TempSqlServerProject.TryCreateAsync();
        Assert.NotNull(project);

        var imports = new SqlServerImportRepository(project.Database);
        var longDescriptionSource = new string('摘', 512);
        var glColumns = new[]
        {
            "文件號碼", "過帳日", "科目代碼", "科目名稱", longDescriptionSource, "人工", "借方", "貸方", "未對應"
        };
        var glBatch = await imports.ReplaceBatchAsync(
            project.ProjectId,
            DatasetKind.Gl,
            new ImportSourceDescriptor(@"C:\gl.xlsx", "gl.xlsx", "Data", null, null),
            glColumns,
            Rows(ObservedRow(
                2,
                ("文件號碼", "J1", LegacyFieldKind.Text, 2, null),
                ("過帳日", "2024-01-02", LegacyFieldKind.Date, 10, null),
                ("科目代碼", "1101", LegacyFieldKind.Text, 4, null),
                ("科目名稱", "現金", LegacyFieldKind.Text, 2, null),
                (longDescriptionSource, "測試", LegacyFieldKind.Text, 2, null),
                ("人工", "1", LegacyFieldKind.Number, 1, 0),
                ("借方", "100.25", LegacyFieldKind.Number, 6, 2),
                ("貸方", "0", LegacyFieldKind.Number, 1, 0),
                ("未對應", "keep", LegacyFieldKind.Text, 4, null))),
            CancellationToken.None);

        var glSpec = new GlMappingSpec(
            new Dictionary<string, string>
            {
                [GlMappingKeys.DocNum] = "文件號碼",
                [GlMappingKeys.PostDate] = "過帳日",
                [GlMappingKeys.AccNum] = "科目代碼",
                [GlMappingKeys.AccName] = "科目名稱",
                [GlMappingKeys.Description] = longDescriptionSource,
                [GlMappingKeys.Manual] = "人工",
                [GlMappingKeys.DebitAmount] = "借方",
                [GlMappingKeys.CreditAmount] = "貸方"
            },
            GlAmountMode.DualAmount);
        var glResult = await new SqlServerGlRepository(project.Database).ProjectStagingToTargetAsync(
            project.ProjectId,
            glBatch.Batch.BatchId,
            glSpec,
            10_000,
            DateParseOptions.Default,
            CancellationToken.None);
        Assert.Empty(glResult.Errors);

        var tbColumns = new[] { "科目代碼", "科目名稱", "借方", "貸方", "未對應" };
        var tbBatch = await imports.ReplaceBatchAsync(
            project.ProjectId,
            DatasetKind.Tb,
            new ImportSourceDescriptor(@"C:\tb.xlsx", "tb.xlsx", "Data", null, null),
            tbColumns,
            Rows(ObservedRow(
                2,
                ("科目代碼", "1101", LegacyFieldKind.Text, 4, null),
                ("科目名稱", "現金", LegacyFieldKind.Text, 2, null),
                ("借方", "100.25", LegacyFieldKind.Number, 6, 2),
                ("貸方", "0", LegacyFieldKind.Number, 1, 0),
                ("未對應", "keep", LegacyFieldKind.Text, 4, null))),
            CancellationToken.None);
        var tbResult = await new SqlServerTbRepository(project.Database).ProjectStagingToTargetAsync(
            project.ProjectId,
            tbBatch.Batch.BatchId,
            new TbMappingSpec(
                new Dictionary<string, string>
                {
                    [TbMappingKeys.AccNum] = "科目代碼",
                    [TbMappingKeys.AccName] = "科目名稱",
                    [TbMappingKeys.DebitAmt] = "借方",
                    [TbMappingKeys.CreditAmt] = "貸方"
                },
                TbChangeMode.DebitCredit),
            10_000,
            CancellationToken.None);
        Assert.Empty(tbResult.Errors);

        ILegacyFieldDefinitionFactsPort facts = new SqlServerFieldDefinitionFactsPort(project.Database);
        var glDefinitions = await facts.ReadAsync(
            project.ProjectId, DatasetKind.Gl, LegacyFieldDefinitionScope.Target, CancellationToken.None);
        var tbDefinitions = await facts.ReadAsync(
            project.ProjectId, DatasetKind.Tb, LegacyFieldDefinitionScope.Target, CancellationToken.None);

        Assert.Equal("文件號碼", glDefinitions.Single(item => item.FieldName == "傳票號碼_JE").Description);
        Assert.Equal(longDescriptionSource, glDefinitions.Single(item => item.FieldName == "傳票摘要_JE").Description);
        Assert.Equal(new[] { LegacyFieldKind.Number, LegacyFieldKind.Number },
            glDefinitions
                .Where(item => item.FieldName is "傳票文件項次_JE_S" or "傳票金額_JE")
                .Select(item => item.Kind));
        Assert.All(
            glDefinitions.Where(item => item.FieldName is "人工傳票否_JE_S" or "傳票文件項次_JE_S"),
            item => Assert.Equal(0, item.DecimalPlaces));
        Assert.Equal(4, glDefinitions.Single(item => item.FieldName == "傳票金額_JE").DecimalPlaces);
        Assert.Contains(glDefinitions, item => item.FieldName == "未對應" && item.Description is null);

        Assert.Equal("科目代碼", tbDefinitions.Single(item => item.FieldName == "會計科目編號_TB").Description);
        Assert.Equal(4, tbDefinitions.Single(item => item.FieldName == "試算表變動金額_TB").DecimalPlaces);
        Assert.Contains(tbDefinitions, item => item.FieldName == "未對應" && item.Description is null);
    }

    private static StagingRow ObservedRow(
        int rowNumber,
        params (string Name, string Value, LegacyFieldKind Kind, int Length, int? Decimals)[] cells)
    {
        var values = cells.ToDictionary(cell => cell.Name, cell => cell.Value, StringComparer.Ordinal);
        var observations = cells
            .Select(cell => new TabularCellObservation(cell.Name, cell.Kind, cell.Length, cell.Decimals))
            .ToArray();
        return new StagingRow(rowNumber, values) { FieldObservations = observations };
    }

    private static async IAsyncEnumerable<StagingRow> Rows(params StagingRow[] rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }
}
