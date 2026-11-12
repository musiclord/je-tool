using JET.AuditCore;
using JET.Domain;
using Xunit;

namespace JET.Tests.AuditCore;

public sealed class IntakeMappingProgramTests
{
    [Theory]
    [MemberData(nameof(JET.Tests.Domain.AccountMappingColumnResolverTests.HeaderOrders), MemberType = typeof(JET.Tests.Domain.AccountMappingColumnResolverTests))]
    public void AccountMappingProjection_UsesTheSameNamedColumnsRegardlessOfOrder(
        string[] columns, string code, string name, string category)
    {
        var projection = JetAuditProgram.PrepareAccountMappingProjection(columns);
        projection.Observe(Row(2, (code, "　A1　"), (name, "合成科目"), (category, "Cash")));
        var actual = Assert.Single(projection.Complete(1, "synthetic.xlsx"));
        Assert.Equal("A1", actual.AccountNumber);
        Assert.Equal("合成科目", actual.AccountName);
        Assert.Equal(AccountCategoryBuiltIns.CashId, actual.CategoryId);
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
        // 畫面只送 replace 或 append，
        // 其他值只有程式送錯資料才會出現；訊息是非預期錯誤那一句。
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
        // 這句的用字已定案。
        Assert.Equal("尚有必填欄位未選擇來源欄：傳票號碼、總帳日期、會計科目編號、會計科目名稱、傳票摘要、金額。", error.Message);
    }

    [Fact]
    public void MappingFinalize_OwnsProjectionFailureText()
    {
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GlMappingKeys.VoucherNumber] = "doc",
            [GlMappingKeys.GlDate] = "post",
            [GlMappingKeys.AccountNumber] = "code",
            [GlMappingKeys.AccountName] = "name",
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
        // 用語以「已儲存」為準，審計員會看到的訊息寫「儲存」。
        // 同一欄同一種問題只寫一次，再依值列出列號，不是每列一整句；
        // 每組另放進 details 並帶來源欄。
        Assert.Equal(
                        // 這句的用字已定案。
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
        // 副檔名用「」包住、用全形逗號，比照 GL、TB 選檔的同類訊息。
        var accountMapping = Assert.Throws<JetActionException>(() => JetAuditProgram.Plan(
            new AccountMappingRequest("project", "mapping.pdf", FileExists: true, Extension: ".pdf", Mode: "replace")));
        Assert.Equal(JetErrorCodes.UnsupportedFileType, accountMapping.Code);
        // 一律寫「科目配對範本」，兩份清單寫全名。
        Assert.Equal("不支援的檔案類型「.pdf」，科目配對範本支援 .xlsx、.csv。", accountMapping.Message);

        var authorizedPreparer = Assert.Throws<JetActionException>(() => JetAuditProgram.Plan(
            new AuthorizedPreparerRequest("project", "preparers.csv", true, ".csv", "replace", "Code")));
        Assert.Equal(JetErrorCodes.UnsupportedFileType, authorizedPreparer.Code);
        Assert.Equal("不支援的檔案類型「.csv」，授權編製人員清單僅支援 .xlsx。", authorizedPreparer.Message);

        var calendar = Assert.Throws<JetActionException>(() => JetAuditProgram.Plan(
            new CalendarFileRequest("import.holiday.fromFile", "project", CalendarDayType.Holiday, "calendar.csv", FileExists: true, Extension: ".csv")));
        Assert.Equal(JetErrorCodes.UnsupportedFileType, calendar.Code);
        Assert.Equal("不支援的檔案類型「.csv」，國定假日清單僅支援 .xlsx。", calendar.Message);
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
                        // 這句的用字已定案。
                        "檔案有 2 列無法讀取：第 3 列：日期「2025/01/01」不是 2026-12-31 這種格式；第 4 列：日期空白",
            error.Message);
    }

    [Fact]
    public void AccountMappingProjection_UsesProjectCategory_BlankDefaultsToOthers_AndLastWins()
    {
        var custom = new AccountCategory(
            "custom.0123456789abcdef0123456789abcdef",
            "Contract assets",
            5,
            AccountCategoryBuiltIns.ReceivablesRole,
            false);
        var projection = JetAuditProgram.PrepareAccountMappingProjection(
            ["GL_NUMBER", "GL_NAME", "STANDARDIZED_ACCOUNT_NAME"],
            new AccountCategorySnapshot(2, [.. AccountCategoryBuiltIns.All, custom]));

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
            fileName: "mapping.xlsx");

        Assert.Collection(
            rows,
            row =>
            {
                Assert.Equal("1101", row.AccountNumber);
                Assert.Equal(custom.CategoryId, row.CategoryId);
                Assert.Equal("Contract assets", row.Category);
            },
            row =>
            {
                Assert.Equal("2000", row.AccountNumber);
                Assert.Equal(AccountCategoryBuiltIns.OthersId, row.CategoryId);
                Assert.Equal(AccountMappingCategories.Others, row.Category);
            });
    }

    [Fact]
    public void AccountMappingProjection_NewCategoryNames_BecomeCustomCategoriesInFirstAppearanceOrder()
    {
        var existing = new AccountCategory(
            "custom.0123456789abcdef0123456789abcdef",
            "Contract assets",
            7,
            AccountCategoryBuiltIns.ReceivablesRole,
            false);
        var projection = JetAuditProgram.PrepareAccountMappingProjection(
            ["GL_Number", "GL_Name", "Standardized Account Name*"],
            new AccountCategorySnapshot(4, [.. AccountCategoryBuiltIns.All, existing]));

        projection.Observe(Row(4, ("GL_Number", "1301"), ("GL_Name", "商品存貨"), ("Standardized Account Name*", " 存貨 ")));
        projection.Observe(Row(5, ("GL_Number", "1501"), ("GL_Name", "機器設備"), ("Standardized Account Name*", "Fixed assets")));
        projection.Observe(Row(6, ("GL_Number", "1302"), ("GL_Name", "在途存貨"), ("Standardized Account Name*", "存貨")));
        projection.Observe(Row(7, ("GL_Number", "1502"), ("GL_Name", "運輸設備"), ("Standardized Account Name*", "FIXED ASSETS")));
        projection.Observe(Row(8, ("GL_Number", "1141"), ("GL_Name", "合約資產"), ("Standardized Account Name*", "contract assets")));

        var rows = projection.Complete(rowCount: 5, fileName: "mapping.xlsx");

        Assert.Collection(
            projection.AddedCategories,
            category =>
            {
                Assert.Equal("存貨", category.Label);
                Assert.Equal(8, category.Ordinal);
            },
            category =>
            {
                Assert.Equal("Fixed assets", category.Label);
                Assert.Equal(9, category.Ordinal);
            });
        Assert.All(projection.AddedCategories, category =>
        {
            Assert.True(AccountCategoryInvariant.IsCustomId(category.CategoryId));
            Assert.Equal(AccountCategoryBuiltIns.OthersRole, category.SemanticRole);
            Assert.False(category.IsBuiltIn);
            Assert.Null(category.ParentCategoryId);
        });
        var inventory = projection.AddedCategories[0];
        var fixedAssets = projection.AddedCategories[1];
        Assert.Equal(
            [inventory.CategoryId, fixedAssets.CategoryId, inventory.CategoryId, fixedAssets.CategoryId, existing.CategoryId],
            rows.Select(row => row.CategoryId));
        Assert.Equal(
            ["存貨", "Fixed assets", "存貨", "Fixed assets", "Contract assets"],
            rows.Select(row => row.Category));
        Assert.All(rows.Take(4), row => Assert.Equal(AccountMappingCategories.Others, row.LegacyCategory));
        AccountCategoryInvariant.ValidateReplacement(
            [.. AccountCategoryBuiltIns.All, existing, .. projection.AddedCategories]);
    }

    [Fact]
    public void AccountMappingProjection_NewCategoryOnlyOnOverwrittenRow_IsNotAdded()
    {
        var projection = JetAuditProgram.PrepareAccountMappingProjection(
            ["GL_Number", "GL_Name", "Standardized Account Name*"]);

        projection.Observe(Row(4, ("GL_Number", "1301"), ("GL_Name", "商品存貨"), ("Standardized Account Name*", "存貨")));
        projection.Observe(Row(5, ("GL_Number", "1501"), ("GL_Name", "機器設備"), ("Standardized Account Name*", "固定資產")));
        projection.Observe(Row(6, ("GL_Number", "1301"), ("GL_Name", "商品存貨"), ("Standardized Account Name*", "Cash")));

        projection.Complete(rowCount: 3, fileName: "mapping.xlsx");

        var added = Assert.Single(projection.AddedCategories);
        Assert.Equal("固定資產", added.Label);
        Assert.Equal(5, added.Ordinal);
    }

    [Fact]
    public void AccountMappingProjection_OriginalBuiltInName_ResolvesToRenamedBuiltIn()
    {
        var renamed = AccountCategoryBuiltIns.All
            .Select(item => item.CategoryId == AccountCategoryBuiltIns.RevenueId
                ? item with { Label = "營業收入" }
                : item)
            .ToArray();
        var projection = JetAuditProgram.PrepareAccountMappingProjection(
            ["GL_Number", "GL_Name", "Standardized Account Name*"],
            new AccountCategorySnapshot(2, renamed));

        projection.Observe(Row(4, ("GL_Number", "4001"), ("GL_Name", "銷貨收入"), ("Standardized Account Name*", "Revenue")));

        var row = Assert.Single(projection.Complete(rowCount: 1, fileName: "mapping.xlsx"));
        Assert.Equal(AccountCategoryBuiltIns.RevenueId, row.CategoryId);
        Assert.Equal("營業收入", row.Category);
        Assert.Empty(projection.AddedCategories);
    }

    [Fact]
    public void AccountMappingProjection_NewCategoryNameOverStorageLimit_FailsWithRowNumber()
    {
        var projection = JetAuditProgram.PrepareAccountMappingProjection(["code", "name", "category"]);
        projection.Observe(Row(2, ("code", "1101"), ("name", "現金"), ("category", new string('類', 400))));
        projection.Observe(Row(3, ("code", "1102"), ("name", "銀行存款"), ("category", new string('類', 401))));

        var error = Assert.Throws<JetActionException>(() => projection.Complete(2, "mapping.csv"));

        Assert.Equal(JetErrorCodes.ProjectionFailed, error.Code);
        Assert.Equal(
            "科目配對範本有 1 列無法讀取，本次匯入未儲存：第 3 列：分類名稱超過 400 個字，請縮短來源資料。",
            error.Message);
    }

    // 分類欄只能靠欄位順序認出來時，選到的可能不是科目配對的檔案，不把那一欄的內容當成新分類；
    // 訊息寫出要新增分類時該怎麼做。
    [Fact]
    internal void AccountMappingProjection_PreservesProviderSpecificFailureText()
    {
        const string expected =
            "科目配對範本有 1 列無法讀取，本次匯入未儲存：第 7 列：分類「bad」不在本案件的科目分類中（Revenue、Receivables、Cash、Receipt in advance、Others），請改用清單中的分類名稱；"
            + "要新增分類，請先把這一欄的欄名改成「Standardized Account Name*」。";
        var projection = JetAuditProgram.PrepareAccountMappingProjection(
            ["甲", "乙", "丙"]);
        projection.Observe(Row(
            6,
            ("甲", "1101"),
            ("乙", "現金"),
            ("丙", "Cash")));
        projection.Observe(Row(
            7,
            ("甲", "1000"),
            ("乙", "Test"),
            ("丙", "bad")));

        var error = Assert.Throws<JetActionException>(() =>
            projection.Complete(2, "mapping.csv"));

        Assert.Equal(JetErrorCodes.ProjectionFailed, error.Code);
        Assert.Equal(expected, error.Message);
        Assert.Empty(projection.AddedCategories);
    }

    // 看的是分類欄怎麼認出來：科目編號與科目名稱的欄名認得出來、只有分類欄照欄位順序時，新名稱照樣不新增。
    [Fact]
    public void AccountMappingProjection_OnlyCategoryColumnFoundByPosition_DoesNotAddCategory()
    {
        const string expected =
            "科目配對範本有 1 列無法讀取，本次匯入未儲存：第 5 列：分類「存貨」不在本案件的科目分類中（Revenue、Receivables、Cash、Receipt in advance、Others），請改用清單中的分類名稱；"
            + "要新增分類，請先把這一欄的欄名改成「Standardized Account Name*」。";
        var projection = JetAuditProgram.PrepareAccountMappingProjection(
            ["科目代號", "科目名稱", "類別"]);
        projection.Observe(Row(5, ("科目代號", "1301"), ("科目名稱", "商品存貨"), ("類別", "存貨")));

        var error = Assert.Throws<JetActionException>(() =>
            projection.Complete(1, "mapping.csv"));

        Assert.Equal(JetErrorCodes.ProjectionFailed, error.Code);
        Assert.Equal(expected, error.Message);
        Assert.Empty(projection.AddedCategories);
    }

    // 分類欄的欄名認得出來時，另外兩欄照欄位順序也不影響：新名稱新增成自訂分類。
    [Fact]
    public void AccountMappingProjection_CategoryColumnFoundByName_AddsCategoryWhenOtherColumnsAreByPosition()
    {
        var projection = JetAuditProgram.PrepareAccountMappingProjection(
            ["甲", "乙", "分類"]);
        projection.Observe(Row(5, ("甲", "1301"), ("乙", "商品存貨"), ("分類", "存貨")));

        var rows = projection.Complete(1, "mapping.csv");

        var added = Assert.Single(projection.AddedCategories);
        Assert.Equal("存貨", added.Label);
        Assert.Equal(added.CategoryId, Assert.Single(rows).CategoryId);
    }

    [Fact]
    public void AuthorizedPreparerProjection_OwnsResolutionTrimBlankSkipAndDedup()
    {
        // 匯入人員清單必須指定 sourceColumn。
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

    // 文字欄長度上限：科目編號與人員識別值 450 字、科目名稱 400 字、假日名稱 256 字；剛好等於上限照常保存，超過就整批不儲存。
    [Fact]
    public void AccountMappingProjection_TextOverStorageLimit_FailsWithRowNumbers()
    {
        var projection = JetAuditProgram.PrepareAccountMappingProjection(["code", "name", "category"]);
        projection.Observe(Row(2, ("code", new string('A', 450)), ("name", new string('名', 400)), ("category", "Cash")));
        projection.Observe(Row(3, ("code", new string('B', 451)), ("name", "現金"), ("category", "Cash")));
        projection.Observe(Row(4, ("code", "1101"), ("name", new string('名', 401)), ("category", "Cash")));

        var error = Assert.Throws<JetActionException>(() =>
            projection.Complete(3, "mapping.xlsx"));

        Assert.Equal(JetErrorCodes.ProjectionFailed, error.Code);
        Assert.Equal(
            "科目配對範本有 2 列無法讀取，本次匯入未儲存：第 3 列：科目編號超過 450 個字，請縮短來源資料。 第 4 列：科目名稱超過 400 個字，請縮短來源資料。",
            error.Message);
    }

    [Fact]
    public void AccountMappingProjection_TextAtStorageLimit_KeepsFullText()
    {
        var projection = JetAuditProgram.PrepareAccountMappingProjection(["code", "name", "category"]);
        projection.Observe(Row(2, ("code", new string('A', 450)), ("name", new string('名', 400)), ("category", "Cash")));

        var row = Assert.Single(projection.Complete(1, "mapping.xlsx"));

        Assert.Equal(new string('A', 450), row.AccountNumber);
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
            "授權編製人員清單有 1 列無法讀取，本次匯入未儲存：第 3 列：人員識別值超過 450 個字，請縮短來源資料或改選其他識別欄。",
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
    public void ProjectCreatePlan_ConsumesDomainDocumentWithoutChangingPolicyValues()
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

        var plan = JetAuditProgram.Plan(new ProjectCreateRequest(
            document,
            HasUserSuppliedProjectName: true,
            Principal: "domain\\user"));

        Assert.Same(document, plan.Document);
        Assert.Equal(ProjectDocument.DefaultMoneyScale, plan.Document.MoneyScale);
        Assert.Equal(ProjectDocument.DefaultRoundingMode, plan.Document.RoundingMode);
        // 持久化的 SampleSeed 就是實際使用的種子。
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
