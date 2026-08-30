using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using Xunit;

namespace JET.Tests.Infrastructure;

/// <summary>
/// Legacy TableDef 等價欄位定義的 mapping-side oracle。
/// 來源欄位維持 import first-seen ordinal；Legacy 真正改名的欄位在原位置改名並保留
/// 原來源名為 description；numeric identifier 另保留 _Temp shadow 並 append canonical text；
/// 非直接金額模式則保留來源金額欄並把統一金額欄追加在尾端。
/// </summary>
public sealed class LegacyFieldDefinitionProjectionTests
{
    [Fact]
    public void GlNumericIdentifierProjection_PreservesTempShadowsAndAppendsCanonicalTextRows()
    {
        var source = new[]
        {
            new LegacyFieldDefinitionState(1, "doc-source", null, LegacyFieldKind.Number, 0, 0, 8, true),
            new LegacyFieldDefinitionState(2, "line-source", null, LegacyFieldKind.Number, 0, 0, 2, true),
            new LegacyFieldDefinitionState(3, "account-source", null, LegacyFieldKind.Number, 0, 0, 6, true),
            new LegacyFieldDefinitionState(4, "amount-source", null, LegacyFieldKind.Number, 0, 2, 7, true)
        };
        var spec = new GlMappingSpec(
            new Dictionary<string, string>
            {
                [GlMappingKeys.DocNum] = "doc-source",
                [GlMappingKeys.LineId] = "line-source",
                [GlMappingKeys.AccNum] = "account-source",
                [GlMappingKeys.Amount] = "amount-source"
            },
            GlAmountMode.SignedAmount);

        var definitions = LegacyFieldDefinitionProjector.ProjectGl(source, spec, moneyScale: 100);

        Assert.Equal(
            [
                "傳票號碼_JE_Temp",
                "傳票文件項次_JE_S",
                "會計科目編號_JE_Temp",
                "amount-source",
                "傳票號碼_JE",
                "會計科目編號_JE",
                "傳票金額_JE"
            ],
            definitions.Select(static definition => definition.FieldName));
        Assert.Equal(Enumerable.Range(1, definitions.Count), definitions.Select(static definition => definition.Ordinal));

        AssertTextProjection(definitions[0], "doc-source", textLength: 8);
        AssertTextProjection(definitions[4], "doc-source", textLength: 8);
        AssertTextProjection(definitions[2], "account-source", textLength: 6);
        AssertTextProjection(definitions[5], "account-source", textLength: 6);
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task GlDualProjection_PreservesCompleteSourceScopeAndAppliesActualLegacyNames(string provider)
    {
        await using var fixture = await ProjectionFixture.CreateAsync(provider);
        var batch = await fixture.ImportGlAsync();

        var result = await new LocalGlRepository(fixture.Database).ProjectStagingToTargetAsync(
            fixture.ProjectId,
            batch.BatchId,
            GlDualSpec(),
            moneyScale: 10_000,
            DateParseOptions.Default,
            CancellationToken.None);

        Assert.Empty(result.Errors);
        var definitions = await fixture.Facts.ReadAsync(
            fixture.ProjectId,
            DatasetKind.Gl,
            LegacyFieldDefinitionScope.Target,
            CancellationToken.None);

        Assert.Equal(
            [
                "傳票號碼_JE",
                "傳票文件項次_JE_S",
                "總帳日期_JE",
                "傳票核准日_JE",
                "傳票日期",
                "會計科目編號_JE_Temp",
                "會計科目名稱_JE",
                "傳票摘要_JE",
                "分錄來源模組_JE",
                "傳票建立人員_JE",
                "傳票核准人員_JE",
                "人工傳票否_JE_S",
                "借方",
                "貸方",
                "未對應欄",
                "錯誤金額",
                "金額",
                "借貸別",
                "會計科目編號_JE",
                "傳票金額_JE"
            ],
            definitions.Select(static definition => definition.FieldName));
        Assert.Equal(Enumerable.Range(1, definitions.Count), definitions.Select(static definition => definition.Ordinal));

        AssertRenamed(definitions[0], "文件號碼", LegacyFieldKind.Text);
        Assert.Equal(2, definitions[0].TextLength);
        Assert.Null(definitions[0].DecimalPlaces);
        AssertRenamed(definitions[1], "項次", LegacyFieldKind.Number);
        Assert.Equal(0, definitions[1].DecimalPlaces);
        AssertRenamed(definitions[2], "過帳日", LegacyFieldKind.Date);
        Assert.Null(definitions[2].TextLength);
        Assert.Null(definitions[2].DecimalPlaces);
        AssertRenamed(definitions[3], "核准日", LegacyFieldKind.Date);
        AssertRenamed(definitions[5], "科目代碼", LegacyFieldKind.Text);
        Assert.Equal(4, definitions[5].TextLength);
        Assert.Null(definitions[5].DecimalPlaces);
        AssertRenamed(definitions[6], "科目名稱", LegacyFieldKind.Text);
        // Z_renameFields 只改名並保留來源 TableDef；mix 欄不得被 catalog 型態覆寫。
        AssertRenamed(definitions[7], "摘要", LegacyFieldKind.Number);
        Assert.Equal(0, definitions[7].DecimalPlaces);
        AssertRenamed(definitions[8], "來源模組", LegacyFieldKind.Text);
        AssertRenamed(definitions[9], "建立人", LegacyFieldKind.Text);
        AssertRenamed(definitions[10], "核准人", LegacyFieldKind.Text);

        // Legacy 沒有 voucherDate 標準輸出；即使參與 mapping，也不加 _JE 或虛構 description。
        Assert.Equal("傳票日期", definitions[4].FieldName);
        Assert.True(string.IsNullOrEmpty(definitions[4].Description));
        Assert.Equal(LegacyFieldKind.Date, definitions[4].Kind);

        // manual 是實際 target flag，不沿用來源文字猜測；Legacy 以 number/0 decimals 表示。
        AssertRenamed(definitions[11], "人工", LegacyFieldKind.Number);
        Assert.Null(definitions[11].TextLength);
        Assert.Equal(0, definitions[11].DecimalPlaces);

        // dual 的兩個來源金額欄與未對應欄仍完整保留，統一 signed amount 另依 target schema 追加。
        AssertUnchanged(definitions[12], "借方", LegacyFieldKind.Number);
        Assert.Null(definitions[12].TextLength);
        Assert.Equal(2, definitions[12].DecimalPlaces);
        AssertUnchanged(definitions[13], "貸方", LegacyFieldKind.Number);
        Assert.Null(definitions[13].TextLength);
        Assert.Equal(0, definitions[13].DecimalPlaces);
        AssertUnchanged(definitions[14], "未對應欄", LegacyFieldKind.Text);
        Assert.Equal(4, definitions[14].TextLength);
        Assert.Null(definitions[14].DecimalPlaces);
        AssertUnchanged(definitions[15], "錯誤金額", LegacyFieldKind.Text);
        AssertUnchanged(definitions[16], "金額", LegacyFieldKind.Number);
        AssertUnchanged(definitions[17], "借貸別", LegacyFieldKind.Text);
        AssertRenamed(definitions[18], "科目代碼", LegacyFieldKind.Text);
        Assert.Equal(4, definitions[18].TextLength);
        Assert.Null(definitions[18].DecimalPlaces);
        var amount = definitions[19];
        Assert.Equal("傳票金額_JE", amount.FieldName);
        Assert.Equal("傳票金額_JE 由系統產生 : 借方-貸方", amount.Description);
        Assert.Equal(LegacyFieldKind.Number, amount.Kind);
        Assert.Null(amount.TextLength);
        Assert.Equal(4, amount.DecimalPlaces);
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider, TbChangeMode.DebitCredit)]
    [InlineData(ProjectDocument.DefaultDatabaseProvider, TbChangeMode.OpenClose)]
    [InlineData(ProjectDocument.DefaultDatabaseProvider, TbChangeMode.OpenCloseBySide)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider, TbChangeMode.DebitCredit)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider, TbChangeMode.OpenClose)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider, TbChangeMode.OpenCloseBySide)]
    public async Task TbNonDirectProjection_AppendsNormalizedChangeAmountAfterAllSourceFields(
        string provider,
        TbChangeMode changeMode)
    {
        await using var fixture = await ProjectionFixture.CreateAsync(provider);
        var batch = await fixture.ImportTbAsync();

        var result = await new LocalTbRepository(fixture.Database).ProjectStagingToTargetAsync(
            fixture.ProjectId,
            batch.BatchId,
            TbNonDirectSpec(changeMode),
            moneyScale: 10_000,
            CancellationToken.None);

        Assert.Empty(result.Errors);
        var definitions = await fixture.Facts.ReadAsync(
            fixture.ProjectId,
            DatasetKind.Tb,
            LegacyFieldDefinitionScope.Target,
            CancellationToken.None);

        Assert.Equal(
            [
                "會計科目編號_TB",
                "會計科目名稱_TB",
                "借方",
                "貸方",
                "期初",
                "期末",
                "期初借",
                "期初貸",
                "期末借",
                "期末貸",
                "未對應欄",
                "直接變動",
                "試算表變動金額_TB"
            ],
            definitions.Select(static definition => definition.FieldName));
        Assert.Equal(Enumerable.Range(1, definitions.Count), definitions.Select(static definition => definition.Ordinal));
        AssertRenamed(definitions[0], "科目代碼", LegacyFieldKind.Text);
        AssertRenamed(definitions[1], "科目名稱", LegacyFieldKind.Text);
        AssertUnchanged(definitions[2], "借方", LegacyFieldKind.Number);
        AssertUnchanged(definitions[3], "貸方", LegacyFieldKind.Number);
        AssertUnchanged(definitions[4], "期初", LegacyFieldKind.Number);
        AssertUnchanged(definitions[5], "期末", LegacyFieldKind.Number);
        AssertUnchanged(definitions[6], "期初借", LegacyFieldKind.Number);
        AssertUnchanged(definitions[7], "期初貸", LegacyFieldKind.Number);
        AssertUnchanged(definitions[8], "期末借", LegacyFieldKind.Number);
        AssertUnchanged(definitions[9], "期末貸", LegacyFieldKind.Number);
        AssertUnchanged(definitions[10], "未對應欄", LegacyFieldKind.Text);
        AssertUnchanged(definitions[11], "直接變動", LegacyFieldKind.Number);

        var amount = definitions[12];
        Assert.Equal("試算表變動金額_TB", amount.FieldName);
        Assert.Equal(TbDerivedDescription(changeMode), amount.Description);
        Assert.Equal(LegacyFieldKind.Number, amount.Kind);
        Assert.Null(amount.TextLength);
        Assert.Equal(4, amount.DecimalPlaces);
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task FailedGlProjection_PreservesPreviouslyCommittedTargetDefinitions(string provider)
    {
        await using var fixture = await ProjectionFixture.CreateAsync(provider);
        var batch = await fixture.ImportGlAsync();
        var repository = new LocalGlRepository(fixture.Database);

        var first = await repository.ProjectStagingToTargetAsync(
            fixture.ProjectId,
            batch.BatchId,
            GlDualSpec(),
            moneyScale: 10_000,
            DateParseOptions.Default,
            CancellationToken.None);
        Assert.Empty(first.Errors);
        var before = await fixture.Facts.ReadAsync(
            fixture.ProjectId,
            DatasetKind.Gl,
            LegacyFieldDefinitionScope.Target,
            CancellationToken.None);

        var invalidSignedSpec = new GlMappingSpec(
            new Dictionary<string, string>
            {
                [GlMappingKeys.DocNum] = "文件號碼",
                [GlMappingKeys.PostDate] = "過帳日",
                [GlMappingKeys.AccNum] = "科目代碼",
                [GlMappingKeys.AccName] = "科目名稱",
                [GlMappingKeys.Description] = "摘要",
                [GlMappingKeys.Amount] = "錯誤金額"
            },
            GlAmountMode.SignedAmount);

        var failed = await repository.ProjectStagingToTargetAsync(
            fixture.ProjectId,
            batch.BatchId,
            invalidSignedSpec,
            moneyScale: 10_000,
            DateParseOptions.Default,
            CancellationToken.None);

        Assert.NotEmpty(failed.Errors);
        var after = await fixture.Facts.ReadAsync(
            fixture.ProjectId,
            DatasetKind.Gl,
            LegacyFieldDefinitionScope.Target,
            CancellationToken.None);
        Assert.Equal(before, after);
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider, GlAmountMode.SignedAmount)]
    [InlineData(ProjectDocument.DefaultDatabaseProvider, GlAmountMode.AmountWithSide)]
    [InlineData(ProjectDocument.DefaultDatabaseProvider, GlAmountMode.AmountWithFlag)]
    [InlineData(ProjectDocument.DefaultDatabaseProvider, GlAmountMode.DualAmount)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider, GlAmountMode.SignedAmount)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider, GlAmountMode.AmountWithSide)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider, GlAmountMode.AmountWithFlag)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider, GlAmountMode.DualAmount)]
    public async Task GlAllAmountModes_PreserveOperandsAndAppendCanonicalAmount(
        string provider,
        GlAmountMode amountMode)
    {
        await using var fixture = await ProjectionFixture.CreateAsync(provider);
        var batch = await fixture.ImportGlAsync();
        var spec = GlSpec(amountMode);

        var result = await new LocalGlRepository(fixture.Database).ProjectStagingToTargetAsync(
            fixture.ProjectId,
            batch.BatchId,
            spec,
            moneyScale: 10_000,
            DateParseOptions.Default,
            CancellationToken.None);

        Assert.Empty(result.Errors);
        var definitions = await fixture.Facts.ReadAsync(
            fixture.ProjectId,
            DatasetKind.Gl,
            LegacyFieldDefinitionScope.Target,
            CancellationToken.None);

        var amount = Assert.Single(definitions, definition => definition.FieldName == "傳票金額_JE");
        Assert.Equal(definitions.Count, amount.Ordinal);
        Assert.Equal(GlDerivedDescription(spec), amount.Description);
        Assert.Equal(LegacyFieldKind.Number, amount.Kind);
        Assert.Equal(4, amount.DecimalPlaces);
        Assert.Contains(definitions, definition => definition.FieldName == "金額");
        if (amountMode == GlAmountMode.DualAmount)
        {
            Assert.Contains(definitions, definition => definition.FieldName == "借方");
            Assert.Contains(definitions, definition => definition.FieldName == "貸方");
        }
        else if (amountMode is GlAmountMode.AmountWithSide or GlAmountMode.AmountWithFlag)
        {
            Assert.Contains(definitions, definition => definition.FieldName == "借貸別");
        }
    }

    [Theory]
    [InlineData(ProjectDocument.DefaultDatabaseProvider)]
    [InlineData(ProjectDocument.DuckDbDatabaseProvider)]
    public async Task TbDirectProjection_RenamesSourceAndPreservesNativeMetadata(string provider)
    {
        await using var fixture = await ProjectionFixture.CreateAsync(provider);
        var batch = await fixture.ImportTbAsync();
        var spec = new TbMappingSpec(
            new Dictionary<string, string>
            {
                [TbMappingKeys.AccNum] = "科目代碼",
                [TbMappingKeys.AccName] = "科目名稱",
                [TbMappingKeys.Amount] = "直接變動"
            },
            TbChangeMode.DirectChange);

        var result = await new LocalTbRepository(fixture.Database).ProjectStagingToTargetAsync(
            fixture.ProjectId,
            batch.BatchId,
            spec,
            moneyScale: 10_000,
            CancellationToken.None);

        Assert.Empty(result.Errors);
        var definitions = await fixture.Facts.ReadAsync(
            fixture.ProjectId,
            DatasetKind.Tb,
            LegacyFieldDefinitionScope.Target,
            CancellationToken.None);
        var amount = Assert.Single(definitions, definition => definition.FieldName == "試算表變動金額_TB");
        Assert.Equal("直接變動", amount.Description);
        Assert.Equal(LegacyFieldKind.Number, amount.Kind);
        Assert.Equal(2, amount.DecimalPlaces);
        Assert.DoesNotContain(definitions, definition => definition.FieldName == "直接變動");
    }

    private static GlMappingSpec GlDualSpec() => new(
        new Dictionary<string, string>
        {
            [GlMappingKeys.DocNum] = "文件號碼",
            [GlMappingKeys.LineId] = "項次",
            [GlMappingKeys.PostDate] = "過帳日",
            [GlMappingKeys.DocDate] = "核准日",
            [GlMappingKeys.VoucherDate] = "傳票日期",
            [GlMappingKeys.AccNum] = "科目代碼",
            [GlMappingKeys.AccName] = "科目名稱",
            [GlMappingKeys.Description] = "摘要",
            [GlMappingKeys.JeSource] = "來源模組",
            [GlMappingKeys.CreateBy] = "建立人",
            [GlMappingKeys.ApproveBy] = "核准人",
            [GlMappingKeys.Manual] = "人工",
            [GlMappingKeys.DebitAmount] = "借方",
            [GlMappingKeys.CreditAmount] = "貸方"
        },
        GlAmountMode.DualAmount);

    private static GlMappingSpec GlSpec(GlAmountMode mode)
    {
        var mapping = GlDualSpec().Mapping
            .Where(pair => pair.Key is not GlMappingKeys.DebitAmount and not GlMappingKeys.CreditAmount)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);

        switch (mode)
        {
            case GlAmountMode.SignedAmount:
                mapping[GlMappingKeys.Amount] = "金額";
                break;
            case GlAmountMode.AmountWithSide:
            case GlAmountMode.AmountWithFlag:
                mapping[GlMappingKeys.Amount] = "金額";
                mapping[GlMappingKeys.DcField] = "借貸別";
                mapping[GlMappingKeys.DcDebitCode] = mode == GlAmountMode.AmountWithFlag ? "1" : "D";
                break;
            case GlAmountMode.DualAmount:
                mapping[GlMappingKeys.DebitAmount] = "借方";
                mapping[GlMappingKeys.CreditAmount] = "貸方";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }

        return new GlMappingSpec(mapping, mode);
    }

    private static string GlDerivedDescription(GlMappingSpec spec) => spec.AmountMode switch
    {
        GlAmountMode.SignedAmount => "傳票金額_JE 由系統產生 : 金額",
        GlAmountMode.DualAmount => "傳票金額_JE 由系統產生 : 借方-貸方",
        GlAmountMode.AmountWithSide => "傳票金額_JE 由系統產生 : 金額欄位【金額】、借貸方判斷欄位【借貸別】，借方為【D】",
        GlAmountMode.AmountWithFlag => "傳票金額_JE 由系統產生 : 金額欄位【金額】、借貸方判斷欄位【借貸別】，借方為【1】",
        _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.AmountMode, null)
    };

    private static string TbDerivedDescription(TbChangeMode mode) => mode switch
    {
        TbChangeMode.DebitCredit => "試算表變動金額_TB 由系統產生 : 借方 - 貸方",
        TbChangeMode.OpenClose => "試算表變動金額_TB 由系統產生 : 期末 - 期初",
        TbChangeMode.OpenCloseBySide => "試算表變動金額_TB 由系統產生 : (期末借 - 期末貸) - (期初借-期初貸)",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    private static TbMappingSpec TbNonDirectSpec(TbChangeMode changeMode)
    {
        var mapping = new Dictionary<string, string>
        {
            [TbMappingKeys.AccNum] = "科目代碼",
            [TbMappingKeys.AccName] = "科目名稱"
        };

        switch (changeMode)
        {
            case TbChangeMode.DebitCredit:
                mapping[TbMappingKeys.DebitAmt] = "借方";
                mapping[TbMappingKeys.CreditAmt] = "貸方";
                break;
            case TbChangeMode.OpenClose:
                mapping[TbMappingKeys.OpeningBalance] = "期初";
                mapping[TbMappingKeys.ClosingBalance] = "期末";
                break;
            case TbChangeMode.OpenCloseBySide:
                mapping[TbMappingKeys.OpeningDebit] = "期初借";
                mapping[TbMappingKeys.OpeningCredit] = "期初貸";
                mapping[TbMappingKeys.ClosingDebit] = "期末借";
                mapping[TbMappingKeys.ClosingCredit] = "期末貸";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(changeMode), changeMode, null);
        }

        return new TbMappingSpec(mapping, changeMode);
    }

    private static void AssertRenamed(
        LegacyFieldDefinition definition,
        string sourceName,
        LegacyFieldKind expectedKind)
    {
        Assert.Equal(sourceName, definition.Description);
        Assert.Equal(expectedKind, definition.Kind);
    }

    private static void AssertUnchanged(
        LegacyFieldDefinition definition,
        string sourceName,
        LegacyFieldKind expectedKind)
    {
        Assert.Equal(sourceName, definition.FieldName);
        Assert.True(string.IsNullOrEmpty(definition.Description));
        Assert.Equal(expectedKind, definition.Kind);
    }

    private static void AssertTextProjection(
        LegacyFieldDefinitionState definition,
        string sourceName,
        int textLength)
    {
        Assert.Equal(sourceName, definition.Description);
        Assert.Equal(LegacyFieldKind.Text, definition.Kind);
        Assert.Equal(textLength, definition.TextLength);
        Assert.Null(definition.DecimalPlaces);
        Assert.Equal(textLength, definition.MaxRenderedLength);
        Assert.True(definition.HasObservation);
    }

    private sealed class ProjectionFixture : IAsyncDisposable
    {
        private readonly TempProjectRoot _root;

        private ProjectionFixture(
            TempProjectRoot root,
            ILocalProjectDatabase database,
            string projectId)
        {
            _root = root;
            Database = database;
            ProjectId = projectId;
            Facts = new LocalFieldDefinitionFactsPort(database);
        }

        internal ILocalProjectDatabase Database { get; }

        internal string ProjectId { get; }

        internal LocalFieldDefinitionFactsPort Facts { get; }

        internal static async Task<ProjectionFixture> CreateAsync(string provider)
        {
            var root = new TempProjectRoot();
            var folder = new JetProjectFolder(root.Path);
            ILocalProjectDatabase database = provider == ProjectDocument.DuckDbDatabaseProvider
                ? new DuckDbProjectDatabase(folder)
                : new SqliteProjectDatabase(folder);
            var projectId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(folder.GetProjectDirectory(projectId));
            await database.EnsureCreatedAsync(projectId, CancellationToken.None);
            return new ProjectionFixture(root, database, projectId);
        }

        internal async Task<ImportBatchInfo> ImportGlAsync()
        {
            var cells = new[]
            {
                Cell("文件號碼", "J1", LegacyFieldKind.Text),
                Cell("項次", "10", LegacyFieldKind.Number, decimalPlaces: 0),
                Cell("過帳日", "2024-01-02", LegacyFieldKind.Date),
                Cell("核准日", "2024-01-03", LegacyFieldKind.Date),
                Cell("傳票日期", "2024-01-01", LegacyFieldKind.Date),
                Cell("科目代碼", "1101", LegacyFieldKind.Number, decimalPlaces: 0),
                Cell("科目名稱", "現金", LegacyFieldKind.Text),
                Cell("摘要", "42", LegacyFieldKind.Number, decimalPlaces: 0),
                Cell("來源模組", "ERP", LegacyFieldKind.Text),
                Cell("建立人", "maker", LegacyFieldKind.Text),
                Cell("核准人", "checker", LegacyFieldKind.Text),
                Cell("人工", "1", LegacyFieldKind.Text),
                Cell("借方", "123.45", LegacyFieldKind.Number, decimalPlaces: 2),
                Cell("貸方", "0", LegacyFieldKind.Number, decimalPlaces: 0),
                Cell("未對應欄", "keep", LegacyFieldKind.Text),
                Cell("錯誤金額", "not-a-number", LegacyFieldKind.Text),
                Cell("金額", "123.45", LegacyFieldKind.Number, decimalPlaces: 2),
                Cell("借貸別", "D", LegacyFieldKind.Text)
            };

            return (await new LocalImportRepository(Database).ReplaceBatchAsync(
                ProjectId,
                DatasetKind.Gl,
                Source("gl.xlsx"),
                cells.Select(static cell => cell.FieldName).ToArray(),
                ToAsync([ObservedRow(2, cells)]),
                CancellationToken.None)).Batch;
        }

        internal async Task<ImportBatchInfo> ImportTbAsync()
        {
            var cells = new[]
            {
                Cell("科目代碼", "1101", LegacyFieldKind.Text),
                Cell("科目名稱", "現金", LegacyFieldKind.Text),
                Cell("借方", "150.25", LegacyFieldKind.Number, decimalPlaces: 2),
                Cell("貸方", "50", LegacyFieldKind.Number, decimalPlaces: 0),
                Cell("期初", "50", LegacyFieldKind.Number, decimalPlaces: 0),
                Cell("期末", "150.25", LegacyFieldKind.Number, decimalPlaces: 2),
                Cell("期初借", "10", LegacyFieldKind.Number, decimalPlaces: 0),
                Cell("期初貸", "0", LegacyFieldKind.Number, decimalPlaces: 0),
                Cell("期末借", "110.25", LegacyFieldKind.Number, decimalPlaces: 2),
                Cell("期末貸", "0", LegacyFieldKind.Number, decimalPlaces: 0),
                Cell("未對應欄", "keep", LegacyFieldKind.Text),
                Cell("直接變動", "100.25", LegacyFieldKind.Number, decimalPlaces: 2)
            };

            return (await new LocalImportRepository(Database).ReplaceBatchAsync(
                ProjectId,
                DatasetKind.Tb,
                Source("tb.xlsx"),
                cells.Select(static cell => cell.FieldName).ToArray(),
                ToAsync([ObservedRow(2, cells)]),
                CancellationToken.None)).Batch;
        }

        public ValueTask DisposeAsync()
        {
            _root.Dispose();
            return ValueTask.CompletedTask;
        }

        private static ImportSourceDescriptor Source(string fileName) =>
            new($@"C:\{fileName}", fileName, null, null, null);

        private static CellEvidence Cell(
            string fieldName,
            string value,
            LegacyFieldKind kind,
            int? decimalPlaces = null) =>
            new(fieldName, value, kind, decimalPlaces);

        private static StagingRow ObservedRow(int sourceRowNumber, IReadOnlyList<CellEvidence> cells) =>
            new(
                sourceRowNumber,
                cells.ToDictionary(static cell => cell.FieldName, static cell => cell.Value, StringComparer.Ordinal))
            {
                FieldObservations = cells
                    .Select(static cell => new TabularCellObservation(
                        cell.FieldName,
                        cell.Kind,
                        cell.Value.Length,
                        cell.DecimalPlaces))
                    .ToArray()
            };

        private static async IAsyncEnumerable<StagingRow> ToAsync(IEnumerable<StagingRow> rows)
        {
            foreach (var row in rows)
            {
                yield return row;
            }

            await Task.CompletedTask;
        }

        private sealed record CellEvidence(
            string FieldName,
            string Value,
            LegacyFieldKind Kind,
            int? DecimalPlaces);
    }
}
