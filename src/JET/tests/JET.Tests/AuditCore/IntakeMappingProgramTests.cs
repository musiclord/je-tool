using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.AuditCore;

public sealed class IntakeMappingProgramTests
{
    [Theory]
    [MemberData(nameof(JET.Tests.Domain.AccountMappingColumnResolverTests.K7HeaderOrders), MemberType = typeof(JET.Tests.Domain.AccountMappingColumnResolverTests))]
    public void K7_AccountMappingProjection_UsesTheSameNamedColumnsRegardlessOfOrder(
        string[] columns, string code, string name, string category)
    {
        var projection = JetAuditProgram.PrepareAccountMappingProjection(columns);
        projection.Observe(Row(2, (code, "　A1　"), (name, "合成科目"), (category, "Cash")));
        var actual = Assert.Single(projection.Complete(1, "synthetic.xlsx", AccountMappingFailureStyle.Local));
        Assert.Equal("A1", actual.AccountCode);
        Assert.Equal("合成科目", actual.AccountName);
        Assert.Equal(AccountTaxonomyBuiltIns.CashId, actual.CategoryId);
    }

    [Fact]
    public void IntakePlan_InvalidModeKeepsExistingErrorPrecedenceBeforeMissingFile()
    {
        var error = Assert.Throws<JetActionException>(() => JetAuditProgram.Plan(
            new IntakeRequest(
                "import.gl.fromFile",
                "project",
                DatasetKind.Gl,
                "missing.xlsx",
                FileExists: false,
                ReaderSupports: true,
                Mode: "merge")));

        Assert.Equal(JetErrorCodes.UnsupportedMode, error.Code);
        // 使用者 2026-10-14 第三輪勾選頁確認 R3-65（第一次失敗：收據 20261014-012448805-68a8e8ea71bc4d6392585a586ab6b91e）。 畫面只送 replace 或 append，
        // 其他值只有程式送錯資料才會出現；錯誤代碼與先後順序不變，訊息改成非預期錯誤那一句。
        Assert.Equal(JetPayloadRejection.UnexpectedErrorMessage, error.Message);
        Assert.Equal("import_mode_invalid", JetPayloadRejection.CheckOf(error));
    }

    [Theory]
    [InlineData(DatasetKind.Gl, IntakeOperation.Replace)]
    [InlineData(DatasetKind.Tb, IntakeOperation.Append)]
    internal void IntakePlan_BindsTypedOperationAndExactMutation(
        DatasetKind kind,
        IntakeOperation operation)
    {
        var action = kind == DatasetKind.Gl
            ? "import.gl.fromFile"
            : "import.tb.fromFile";
        var plan = JetAuditProgram.Plan(new IntakeRequest(
            action,
            "project",
            kind,
            "source.xlsx",
            FileExists: true,
            ReaderSupports: true,
            Mode: operation == IntakeOperation.Append ? "append" : "replace"));

        Assert.Equal(operation, plan.Operation);
        Assert.True(plan.Effects.InvalidatePrescreen);
        Assert.True(plan.Effects.InvalidateFilterHits);
        Assert.True(plan.Effects.InvalidateFilterScenarioDefinitions);
        Assert.True(plan.Effects.InvalidateValidation);
    }

    [Fact]
    public void GlMappingPlan_OwnsRequirednessAndKeepsExactError()
    {
        var error = Assert.Throws<JetActionException>(() => JetAuditProgram.Plan(
            new GlMappingRequest(
                "project",
                "batch",
                new Dictionary<string, string>(),
                GlAmountMode.SignedAmount,
                ["傳票號碼"],
                ProjectDocument.DefaultMoneyScale,
                DateParseOptions.Default)));

        Assert.Equal(JetErrorCodes.MissingRequiredMapping, error.Code);
        // 使用者 2026-10-13 勾選頁確認改字，預期值換成確認後的句子（第一次失敗：收據 20261013-130645716-968482adb634494a97e2594c7e73decf）。
        Assert.Equal("尚有必填欄位未選擇來源欄：傳票號碼、總帳日期、會計科目編號、會計科目名稱、傳票摘要、金額。", error.Message);
    }

    [Fact]
    public void MappingFinalize_OwnsProjectionFailureText()
    {
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GlMappingKeys.DocNum] = "doc",
            [GlMappingKeys.PostDate] = "post",
            [GlMappingKeys.AccNum] = "code",
            [GlMappingKeys.AccName] = "name",
            [GlMappingKeys.Description] = "desc",
            [GlMappingKeys.Amount] = "amount"
        };
        var plan = JetAuditProgram.Plan(new GlMappingRequest(
            "project",
            "batch",
            mapping,
            GlAmountMode.SignedAmount,
            mapping.Values.ToArray(),
            ProjectDocument.DefaultMoneyScale,
            DateParseOptions.Default));

        var error = Assert.Throws<JetActionException>(() => JetAuditProgram.Finalize(
            plan,
            new ProjectionResult(
                0,
                [new RowProjectionError(7, "amount", "bad", "不是有效金額", "JE.csv")])));

        Assert.Equal(JetErrorCodes.ProjectionFailed, error.Code);
        // 2026-10-03 用語統一 W10：審計員會看到的「保存」改為「儲存」（第一次失敗：收據 20261003-023349721-0ccefea0a80c412aa8460624eaae563a）。
        // 2026-10-03 O1、O2：同一欄同一種問題只寫一次，再依值列出列號，不再每列一整句；
        // 每組另放進 details 並帶來源欄（第一次失敗：收據 20261003-065529947-9b08f413915748fe812bdead0a6d6d89）。
        Assert.Equal(
// 使用者 2026-10-13 勾選頁確認改字，預期值換成確認後的句子（第一次失敗：收據 20261013-130645716-968482adb634494a97e2594c7e73decf）。
                        "有 1 列的值無法讀取，本次配對未儲存。來源欄「amount」有 1 列不是有效金額：「bad」在 JE.csv 第 7 列。",
            error.Message);
        var detail = Assert.Single(error.Details!);
        Assert.Equal("來源欄「amount」有 1 列不是有效金額：「bad」在 JE.csv 第 7 列。", detail.Message);
        Assert.Equal("amount", detail.SourceColumn);
        Assert.Null(detail.Group);
        Assert.Null(detail.Rule);
    }

    [Fact]
    public void AccountMappingPlan_PreservesModeFileAndExtensionPrecedence()
    {
        var invalidMode = Assert.Throws<JetActionException>(() => JetAuditProgram.Plan(
            new AccountMappingRequest(
                "project",
                "missing.txt",
                FileExists: false,
                Extension: ".txt",
                Mode: "append")));
        Assert.Equal(JetErrorCodes.UnsupportedMode, invalidMode.Code);

        var missingFile = Assert.Throws<JetActionException>(() => JetAuditProgram.Plan(
            new AccountMappingRequest(
                "project",
                "missing.txt",
                FileExists: false,
                Extension: ".txt",
                Mode: "replace")));
        Assert.Equal(JetErrorCodes.FileNotFound, missingFile.Code);
    }

    [Fact]
    public void UnsupportedFileTypeMessages_UseTheSamePunctuationAsTheOtherFileMessages()
    {
        // 使用者 2026-10-19 裁定「改成和其他訊息相同」：副檔名用「」包住、用全形逗號，比照 GL、TB 選檔的同類訊息
        // （修正前失敗：收據 20261019-151229950-9c3df916191d478296f1f8f146d3ec62）。
        var accountMapping = Assert.Throws<JetActionException>(() => JetAuditProgram.Plan(
            new AccountMappingRequest("project", "mapping.pdf", FileExists: true, Extension: ".pdf", Mode: "replace")));
        Assert.Equal(JetErrorCodes.UnsupportedFileType, accountMapping.Code);
        Assert.Equal("不支援的檔案類型「.pdf」，科目配對檔支援 .xlsx、.csv。", accountMapping.Message);

        var authorizedPreparer = Assert.Throws<JetActionException>(() => JetAuditProgram.Plan(
            new AuthorizedPreparerRequest("project", "preparers.csv", true, ".csv", "replace", "Code")));
        Assert.Equal(JetErrorCodes.UnsupportedFileType, authorizedPreparer.Code);
        Assert.Equal("不支援的檔案類型「.csv」，授權編製人員清單僅支援 .xlsx。", authorizedPreparer.Message);

        var calendar = Assert.Throws<JetActionException>(() => JetAuditProgram.Plan(
            new CalendarFileRequest("import.holiday.fromFile", "project", CalendarDayType.Holiday, "calendar.csv", FileExists: true, Extension: ".csv")));
        Assert.Equal(JetErrorCodes.UnsupportedFileType, calendar.Code);
        Assert.Equal("不支援的檔案類型「.csv」，行事曆檔僅支援 .xlsx。", calendar.Message);
    }

    [Fact]
    public void InlineCalendarPlan_NormalizesAndDeduplicatesDates()
    {
        var plan = JetAuditProgram.Plan(new CalendarInlineRequest(
            "import.holiday",
            "project",
            CalendarDayType.Holiday,
            ["2025-01-01", "2025-01-01", "2025-02-28"]));

        Assert.Equal(["2025-01-01", "2025-02-28"], plan.Dates);
        Assert.True(plan.Effects.InvalidatePrescreen);
        Assert.True(plan.Effects.InvalidateFilterHits);
    }

    [Fact]
    public async Task CalendarFileProjection_OwnsResolutionFilteringDedupAndTrim()
    {
        var plan = JetAuditProgram.Plan(new CalendarFileRequest(
            "import.holiday.fromFile",
            "project",
            CalendarDayType.Holiday,
            "calendar.xlsx",
            FileExists: true,
            Extension: ".xlsx"));

        var entries = await JetAuditProgram.ProjectCalendarFileAsync(
            plan,
            ["Date_of_Holiday", "Holiday_Name", "IS_Holiday"],
            Rows(
                Row(3, ("Date_of_Holiday", " 2025-01-01 "), ("Holiday_Name", " 元旦 "), ("IS_Holiday", " y ")),
                Row(4, ("Date_of_Holiday", "2025-01-01"), ("Holiday_Name", "後列重複"), ("IS_Holiday", "Y")),
                Row(5, ("Date_of_Holiday", "2025-02-28"), ("Holiday_Name", "非假日"), ("IS_Holiday", "N"))),
            CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal(new CalendarDayEntry("2025-01-01", "元旦"), entry);
    }

    [Fact]
    public async Task CalendarFileProjection_AggregatesFirstTenErrorsWithExistingText()
    {
        var plan = JetAuditProgram.Plan(new CalendarFileRequest(
            "import.makeupDay.fromFile",
            "project",
            CalendarDayType.Makeup,
            "calendar.xlsx",
            FileExists: true,
            Extension: ".xlsx"));

        var error = await Assert.ThrowsAsync<JetActionException>(() =>
            JetAuditProgram.ProjectCalendarFileAsync(
                plan,
                ["Date_of_MakeUpday"],
                Rows(
                    Row(3, ("Date_of_MakeUpday", "2025/01/01")),
                    Row(4, ("Date_of_MakeUpday", " "))),
                CancellationToken.None));

        Assert.Equal(JetErrorCodes.ProjectionFailed, error.Code);
        Assert.Equal(
// 使用者 2026-10-13 勾選頁確認改字，預期值換成確認後的句子（第一次失敗：收據 20261013-130645716-968482adb634494a97e2594c7e73decf）。
                        "檔案有 2 列無法讀取：第 3 列：日期「2025/01/01」不是 2026-12-31 這種格式；第 4 列：日期空白",
            error.Message);
    }

    [Fact]
    public void AccountMappingProjection_UsesProjectTaxonomy_BlankDefaultsToOthers_AndLastWins()
    {
        var custom = new AccountTaxonomyCategory(
            "custom.0123456789abcdef0123456789abcdef",
            "Contract assets",
            5,
            AccountTaxonomyBuiltIns.ReceivablesRole,
            false);
        var projection = JetAuditProgram.PrepareAccountMappingProjection(
            ["GL_NUMBER", "GL_NAME", "STANDARDIZED_ACCOUNT_NAME"],
            new AccountTaxonomySnapshot(2, [.. AccountTaxonomyBuiltIns.All, custom]));

        projection.Observe(Row(
            2,
            ("GL_NUMBER", " 1101 "),
            ("GL_NAME", " 現金 "),
            ("STANDARDIZED_ACCOUNT_NAME", " cash ")));
        projection.Observe(Row(
            3,
            ("GL_NUMBER", "1101"),
            ("GL_NAME", "銷貨收入"),
            ("STANDARDIZED_ACCOUNT_NAME", "Contract assets")));
        projection.Observe(Row(
            4,
            ("GL_NUMBER", "2000"),
            ("GL_NAME", "未分類"),
            ("STANDARDIZED_ACCOUNT_NAME", " ")));

        var rows = projection.Complete(
            rowCount: 3,
            fileName: "mapping.xlsx",
            AccountMappingFailureStyle.Local);

        Assert.Collection(
            rows,
            row =>
            {
                Assert.Equal("1101", row.AccountCode);
                Assert.Equal(custom.CategoryId, row.CategoryId);
                Assert.Equal("Contract assets", row.Category);
            },
            row =>
            {
                Assert.Equal("2000", row.AccountCode);
                Assert.Equal(AccountTaxonomyBuiltIns.OthersId, row.CategoryId);
                Assert.Equal(AccountMappingCategories.Others, row.Category);
            });
    }

    [Theory]
    // 使用者 2026-10-13 勾選頁確認改字，預期值換成確認後的句子（第一次失敗：收據 20261013-130645716-968482adb634494a97e2594c7e73decf）。
    // SQL Server 與本機改用同一句全形標點的句子，兩種 style 的預期值因此相同。
    // 使用者 2026-10-14 第三輪勾選頁確認 R3-60（第一次失敗：收據 20261014-012448805-68a8e8ea71bc4d6392585a586ab6b91e）。
    [InlineData(
        AccountMappingFailureStyle.Local,
        "科目配對檔有 1 列無法讀取，本次匯入未儲存：第 7 列：分類「bad」不在本案件的科目分類中（Revenue、Receivables、Cash、Receipt in advance、Others），請改用清單中的分類名稱。")]
    [InlineData(
        AccountMappingFailureStyle.SqlServer,
        "科目配對檔有 1 列無法讀取，本次匯入未儲存：第 7 列：分類「bad」不在本案件的科目分類中（Revenue、Receivables、Cash、Receipt in advance、Others），請改用清單中的分類名稱。")]
    internal void AccountMappingProjection_PreservesProviderSpecificFailureText(
        AccountMappingFailureStyle style,
        string expected)
    {
        var projection = JetAuditProgram.PrepareAccountMappingProjection(
            ["code", "name", "category"]);
        projection.Observe(Row(
            7,
            ("code", "1000"),
            ("name", "Test"),
            ("category", "bad")));

        var error = Assert.Throws<JetActionException>(() =>
            projection.Complete(1, "mapping.csv", style));

        Assert.Equal(JetErrorCodes.ProjectionFailed, error.Code);
        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public void AuthorizedPreparerProjection_OwnsResolutionTrimBlankSkipAndDedup()
    {
        // 2026-10-04 第 3 批 L12 裁定 sourceColumn 必填；保留正規化、略空白與去重的原斷言。
        var projection = JetAuditProgram.PrepareAuthorizedPreparerProjection(
            ["unused", "AUTHORIZED_PREPARER"], "AUTHORIZED_PREPARER");

        projection.Observe(Row(2, ("AUTHORIZED_PREPARER", " 王小明 ")));
        projection.Observe(Row(3, ("AUTHORIZED_PREPARER", "王小明")));
        projection.Observe(Row(4, ("AUTHORIZED_PREPARER", "   ")));
        projection.Observe(Row(5, ("AUTHORIZED_PREPARER", "李大華")));

        Assert.Equal(
            ["王小明", "李大華"],
            projection.Complete(4, "authorized.xlsx"));
    }

    // 跨資料庫一致性計畫第三節 C（2026-10-19 使用者裁定「文字欄長度三方一致」）：上限照 SQL Server 欄寬，
    // 科目編號與人員識別值 450 字、科目名稱 400 字、假日名稱 256 字；剛好等於上限照常保存，超過就整批不儲存。
    // 第一次失敗：20261019-020852386-c03d4566bbee453ca11b4781aafad514，超過上限的三個案例在修改前都照常匯入。
    [Fact]
    public void AccountMappingProjection_TextOverStorageLimit_FailsWithRowNumbers()
    {
        var projection = JetAuditProgram.PrepareAccountMappingProjection(["code", "name", "category"]);
        projection.Observe(Row(2, ("code", new string('A', 450)), ("name", new string('名', 400)), ("category", "Cash")));
        projection.Observe(Row(3, ("code", new string('B', 451)), ("name", "現金"), ("category", "Cash")));
        projection.Observe(Row(4, ("code", "1101"), ("name", new string('名', 401)), ("category", "Cash")));

        var error = Assert.Throws<JetActionException>(() =>
            projection.Complete(3, "mapping.xlsx", AccountMappingFailureStyle.Local));

        Assert.Equal(JetErrorCodes.ProjectionFailed, error.Code);
        Assert.Equal(
            "科目配對檔有 2 列無法讀取，本次匯入未儲存：第 3 列：科目編號超過 450 個字，請縮短來源資料。 第 4 列：科目名稱超過 400 個字，請縮短來源資料。",
            error.Message);
    }

    [Fact]
    public void AccountMappingProjection_TextAtStorageLimit_KeepsFullText()
    {
        var projection = JetAuditProgram.PrepareAccountMappingProjection(["code", "name", "category"]);
        projection.Observe(Row(2, ("code", new string('A', 450)), ("name", new string('名', 400)), ("category", "Cash")));

        var row = Assert.Single(projection.Complete(1, "mapping.xlsx", AccountMappingFailureStyle.Local));

        Assert.Equal(new string('A', 450), row.AccountCode);
        Assert.Equal(new string('名', 400), row.AccountName);
    }

    [Fact]
    public void AuthorizedPreparerProjection_NameOverStorageLimit_FailsWithRowNumber()
    {
        var projection = JetAuditProgram.PrepareAuthorizedPreparerProjection(
            ["AUTHORIZED_PREPARER"], "AUTHORIZED_PREPARER");
        projection.Observe(Row(2, ("AUTHORIZED_PREPARER", new string('王', 450))));
        projection.Observe(Row(3, ("AUTHORIZED_PREPARER", new string('李', 451))));

        var error = Assert.Throws<JetActionException>(() => projection.Complete(2, "authorized.xlsx"));

        Assert.Equal(JetErrorCodes.ProjectionFailed, error.Code);
        Assert.Equal(
            "授權人員名單有 1 列無法讀取，本次匯入未儲存：第 3 列：人員識別值超過 450 個字，請縮短來源資料或改選其他識別欄。",
            error.Message);
    }

    [Fact]
    public void AuthorizedPreparerProjection_NameAtStorageLimit_KeepsFullText()
    {
        var projection = JetAuditProgram.PrepareAuthorizedPreparerProjection(
            ["AUTHORIZED_PREPARER"], "AUTHORIZED_PREPARER");
        projection.Observe(Row(2, ("AUTHORIZED_PREPARER", new string('王', 450))));

        Assert.Equal([new string('王', 450)], projection.Complete(1, "authorized.xlsx"));
    }

    [Fact]
    public async Task CalendarFileProjection_NameOverStorageLimit_FailsAndAtLimitIsKept()
    {
        var plan = JetAuditProgram.Plan(new CalendarFileRequest(
            "import.holiday.fromFile",
            "project",
            CalendarDayType.Holiday,
            "calendar.xlsx",
            FileExists: true,
            Extension: ".xlsx"));

        var error = await Assert.ThrowsAsync<JetActionException>(() =>
            JetAuditProgram.ProjectCalendarFileAsync(
                plan,
                ["Date_of_Holiday", "Holiday_Name"],
                Rows(
                    Row(3, ("Date_of_Holiday", "2025-01-01"), ("Holiday_Name", new string('節', 257))),
                    Row(4, ("Date_of_Holiday", "2025-01-02"), ("Holiday_Name", new string('節', 256)))),
                CancellationToken.None));
        Assert.Equal(JetErrorCodes.ProjectionFailed, error.Code);
        Assert.Equal("檔案有 1 列無法讀取：第 3 列：「Holiday_Name」超過 256 個字，請縮短來源資料", error.Message);

        var entries = await JetAuditProgram.ProjectCalendarFileAsync(
            plan,
            ["Date_of_Holiday", "Holiday_Name"],
            Rows(Row(4, ("Date_of_Holiday", "2025-01-02"), ("Holiday_Name", new string('節', 256)))),
            CancellationToken.None);
        Assert.Equal(new CalendarDayEntry("2025-01-02", new string('節', 256)), Assert.Single(entries));
    }

    [Fact]
    public void NonWorkingDaysPlan_DetectsNoOpWithoutChangingNormalization()
    {
        var requested = JetAuditProgram.ValidateNonWorkingDays([6, 0, 6]);
        var plan = JetAuditProgram.Plan(new NonWorkingDaysRequest(
            "project",
            CurrentDays: [0, 6],
            Requested: requested));

        Assert.Equal([0, 6], plan.NormalizedDays);
        Assert.False(plan.ShouldExecute);
    }

    [Fact]
    public void NonWorkingDaysValidation_OwnsExistingInvalidPayloadContract()
    {
        var error = Assert.Throws<JetActionException>(() =>
            JetAuditProgram.ValidateNonWorkingDays([0, 7]));

        Assert.Equal(JetErrorCodes.InvalidPayload, error.Code);
        Assert.Equal(
            "非工作日週幾必須是 0–6(週日=0…週六=6),收到 '7'。",
            error.Message);
    }

    [Fact]
    public void CaseCreatePlan_ConsumesDomainDocumentWithoutChangingPolicyValues()
    {
        var document = ProjectDocument.CreateNew(
            "case",
            "CODE",
            "Entity",
            "operator",
            "2025-01-01",
            "2025-12-31",
            "2024-12-31",
            ProjectDocument.DefaultDatabaseProvider,
            DateTimeOffset.UnixEpoch,
            sampleSeed: 31,
            sampleSeedVersion: JetAuditProgram.CurrentInfSamplingAlgorithmVersion);

        var plan = JetAuditProgram.Plan(new CaseCreateRequest(
            document,
            HasUserSuppliedCaseName: true,
            Principal: "domain\\user"));

        Assert.Same(document, plan.Document);
        Assert.Equal(ProjectDocument.DefaultMoneyScale, plan.Document.MoneyScale);
        Assert.Equal(ProjectDocument.DefaultRoundingMode, plan.Document.RoundingMode);
        // 舊案件的固定種子退路已刪除，持久化的 SampleSeed 就是實際使用的種子。
        Assert.Equal(31, plan.Document.SampleSeed);
        Assert.Equal(JetAuditProgram.CurrentInfSamplingAlgorithmVersion, plan.Document.SampleSeedVersion);
    }

    private static StagingRow Row(
        int sourceRowNumber,
        params (string Key, string Value)[] cells) =>
        new(
            sourceRowNumber,
            cells.ToDictionary(
                cell => cell.Key,
                cell => cell.Value,
                StringComparer.Ordinal));

    private static async IAsyncEnumerable<StagingRow> Rows(
        params StagingRow[] rows)
    {
        await Task.Yield();
        foreach (var row in rows)
        {
            yield return row;
        }
    }
}
