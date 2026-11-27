using System.Globalization;
using System.Text.Json;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml;
using JET.Application;
using JET.AuditCore;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Application;
using Xunit;
using static JET.Tests.Infrastructure.WorkingPaperWriterTestSupport;

namespace JET.Tests.Infrastructure;

/// <summary>底稿寫出器：step3 條件表、step4 符合高風險條件傳票與 step4-1 傳票明細。</summary>
public sealed class WorkingPaperWriterHighRiskSheetsTests
{
    // ================= 案件資料庫的內容讀不出來時的回報 =================

    // 欄位型態是匯入時照儲存格型別記下的，來源的內容本來就可能不合型態。傳票摘要的欄位型態改成數值、日期或時間後，
    // 存下的摘要文字都不合這些型態；底稿 step4-1 符合高風險條件傳票明細照存下來的文字寫成文字儲存格，匯出照常完成，不報案件資料庫損壞。
    [Theory]
    [InlineData("number")]
    [InlineData("date")]
    [InlineData("time")]
    public async Task HighRiskVoucherDetail_StoredValueThatDoesNotMatchFieldType_IsWrittenAsTheStoredText(string kind)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", ConditionColumnScenarioPayload());
        var db = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await using (var connection = db.CreateConnection(context.ProjectId))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE field_info SET field_kind = @kind, decimal_places = 0 WHERE definition_scope = 'target' AND jet_field = 'description';";
            command.AddWithValue("@kind", kind);
            Assert.True(await command.ExecuteNonQueryAsync() > 0);
        }

        var storedDescriptions = (await DemoProjectPipeline.QueryStringListAsync(host, context.ProjectId,
                "SELECT DISTINCT description FROM gl WHERE description IS NOT NULL AND description <> '';"))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        using var workbook = await WriteDemoAndReadAsync(host, context.ProjectId);

        var sheet = workbook.Worksheet(HighRiskVoucherDetailSheet);
        var descriptionColumn = sheet.Row(5).CellsUsed()
            .Single(cell => cell.GetString() == "傳票摘要_JE")
            .Address.ColumnNumber;
        // 示範資料有些分錄沒有摘要，那幾格照舊留白；有摘要的格子都是文字儲存格，內容是存下來的摘要。
        var written = Enumerable.Range(6, sheet.LastRowUsed()!.RowNumber() - 5)
            .Select(row => sheet.Cell(row, descriptionColumn))
            .Where(cell => cell.DataType != XLDataType.Blank)
            .ToList();
        Assert.NotEmpty(written);
        Assert.All(written, cell =>
        {
            Assert.Equal(XLDataType.Text, cell.DataType);
            Assert.Contains(cell.GetString(), storedDescriptions);
        });
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"synthetic\":\"\\ud800\"}")]
    public async Task UnreadableRawGlJson_IsReportedAsDatabaseCorruption(string damaged)
    {
        using var host = new HandlerTestHost();
        var context = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", ConditionColumnScenarioPayload());
        var db = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        await using (var connection = db.CreateConnection(context.ProjectId))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE pbc_gl SET row_json = @json;";
            command.AddWithValue("@json", damaged);
            await command.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<JetActionException>(() => WriteDemoAndReadAsync(host, context.ProjectId));

        Assert.Equal(JetErrorCodes.DatabaseCorrupt, error.Code);
        Assert.Contains("備份", error.Message);
        Assert.DoesNotContain("重新下載", error.Message);
    }

    // ================= step3 高風險條件彙總 =================

    [Fact]
    public async Task HighRiskSummary_ConditionTable_MatchesScenariosNameRationaleAndVoucherHits()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", ConditionColumnScenarioPayload());

        using var workbook = await WriteDemoAndReadAsync(host, ctx.ProjectId);
        var sheet = workbook.Worksheet(HighRiskSummarySheet);

        // 欄標第 18 列(C/D/E),資料自第 19 列;B 代號 C{position} 升冪。
        var codes = ReadColumnFrom(sheet, "B", 19);
        Assert.Equal(new[] { "C1", "C2", "C3" }, codes);

        // C1 列:C 條件描述==情境 name、D 原因==rationale、E 符合傳票數==voucherHitCount recount。
        var c1Row = FindRowByColumnValue(sheet, "B", "C1", 19);
        Assert.Equal("非授權編製人員", sheet.Cell($"C{c1Row}").GetString());
        Assert.Equal("編製人員不在授權清單內", sheet.Cell($"D{c1Row}").GetString());

        var c1Vouchers = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT COUNT(DISTINCT g.voucher_number) FROM filter_hit r " +
            "JOIN gl g ON g.entry_id = r.entry_id " +
            "WHERE r.scenario_id = 1 AND g.voucher_number IS NOT NULL;");
        Assert.Equal(c1Vouchers, (long)sheet.Cell($"E{c1Row}").GetDouble());

        // 0 命中情境(C3 天價)仍列出、傳票數 0。
        var c3Row = FindRowByColumnValue(sheet, "B", "C3", 19);
        Assert.Equal(0d, sheet.Cell($"E{c3Row}").GetDouble());

        // step3 照舊版保護，只鎖 B7:E28；其他格子，包括沒有寫值的格子，都可以填。
        Assert.True(sheet.Protection.IsProtected);
        Assert.All(
            new[] { "B7", "E7", $"C{c1Row}", "B28", "E28" },
            reference => Assert.True(sheet.Cell(reference).Style.Protection.Locked, $"{reference} 應鎖定。"));
        Assert.All(
            new[] { "A1", "A19", "F19", "B29", "H40" },
            reference => Assert.False(sheet.Cell(reference).Style.Protection.Locked, $"{reference} 應可填。"));
    }

    // 十個情境不拆列時剛好寫到第 28 列。第一個情境的條件說明超過 Excel 單格容量，拆成多列，
    // 後面的情境就寫過第 28 列。多出的列在 B 到 E 欄也鎖定，舊版 JET 只鎖 B7:E28。
    [Fact]
    public async Task HighRiskSummary_RowsWrittenPastRow28_AreLockedInColumnsBToE()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        var validation = await host.DispatchAsync("validate.run");
        var validationResultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString();
        var scenarios = Enumerable.Range(1, 10)
            .Select(index => new
            {
                name = $"情境{index:00}",
                rationale = "寫過第 28 列",
                groups = new[]
                {
                    new
                    {
                        join = "and",
                        conditions = new[]
                        {
                            new
                            {
                                join = "and",
                                type = "fieldValue",
                                field = "description",
                                @operator = "startsWith",
                                value = index == 1 ? new string('X', 33_000) : $"SYN-{index:00}",
                                includeBlank = false
                            }
                        }
                    }
                }
            })
            .ToArray();
        var saved = await host.DispatchAsync("filter.commit", JsonSerializer.Serialize(new
        {
            populationScope = GlPopulationScopeValues.AuditPeriod,
            scenarios
        }));
        var revision = saved.GetProperty("resultRef").GetProperty("revision").GetString();
        // 條件說明要走正式的匯出才會寫進底稿。
        await host.DispatchAsync(
            "export.criteriaSelectionReport",
            JsonSerializer.Serialize(new { validationResultId, revision }));
        var exported = await host.DispatchAsync("export.workingPaperStream", JsonSerializer.Serialize(new
        {
            validationResultId,
            scenarioRevision = revision,
            scenarioPositions = Enumerable.Range(1, 10).ToArray()
        }));

        using var workbook = new XLWorkbook(Path.Combine(
            host.ProjectsRoot,
            ctx.ProjectId,
            exported.GetProperty("publishedReport").GetProperty("fileName").GetString()!));
        var sheet = workbook.Worksheet(HighRiskSummarySheet);

        var codes = ReadColumnFrom(sheet, "B", 19);
        var continued = codes.Count(code => code == "C1（續）");
        Assert.True(continued > 0, "第一個情境的條件說明應拆成多列。");
        Assert.Equal(
            new[] { "C1" }
                .Concat(Enumerable.Repeat("C1（續）", continued))
                .Concat(Enumerable.Range(2, 9).Select(position => $"C{position}")),
            codes);
        var lastRow = 18 + codes.Count;
        Assert.True(lastRow > 28, "資料應寫過第 28 列。");
        Assert.Equal("C10", sheet.Cell($"B{lastRow}").GetString());

        Assert.True(sheet.Protection.IsProtected);
        Assert.All(
            Enumerable.Range(28, lastRow - 27)
                .SelectMany(row => new[] { $"B{row}", $"C{row}", $"D{row}", $"E{row}" }),
            reference => Assert.True(sheet.Cell(reference).Style.Protection.Locked, $"{reference} 應鎖定。"));
        Assert.All(
            new[] { $"A{lastRow}", $"F{lastRow}", $"B{lastRow + 1}", $"E{lastRow + 1}" },
            reference => Assert.False(sheet.Cell(reference).Style.Protection.Locked, $"{reference} 應可填。"));
    }

    /// <summary>
    /// WorkingPaper 的 step3 可見情境表固定止於 E 欄；計畫裡的條件文字不得鏡射到 F 欄。
    /// WorkingPaperContext 沒有 ScenarioConditionLogic 欄位，條件文字只在計畫裡。
    /// </summary>
    [Fact]
    public async Task HighRiskSummary_VisibleConditionTable_EndsAtColumnE()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", ConditionColumnScenarioPayload());

        var context = await CurrentContextForAsync(host, ctx.ProjectId);
        var stream = new MemoryStream();
        await WriteWithCurrentPlanAsync(host, BuildWriter(host), stream, context, CancellationToken.None);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(HighRiskSummarySheet);

        Assert.All(
            Enumerable.Range(18, 11),
            row => Assert.True(sheet.Cell(row, 6).IsEmpty(), $"F{row} 應保持空白。"));
    }

    // ================= 每張工作表的列號 =================

    // step3 與 step4 的表頭有幾列分成兩三段文字。同一列只能寫出一次，列號也要由小到大；
    // 這裡連續頁一起核對底稿的每一張工作表。
    [Fact]
    public async Task WorkingPaperSheets_RowNumbers_AreUniqueAndAscending()
    {
        using var host = new HandlerTestHost();
        using var document =
            await WorkingPaperWriterContinuationProtectionTests.WriteContinuationWorkingPaperAsync(host);

        var problems = WorksheetsByName(document)
            .Select(pair => (
                Name: pair.Key,
                Rows: pair.Value.GetFirstChild<SheetData>()!.Elements<Row>()
                    .Select(row => row.RowIndex!.Value)
                    .ToArray()))
            .Where(sheet => sheet.Rows.Zip(sheet.Rows.Skip(1)).Any(pair => pair.First >= pair.Second))
            .Select(sheet => $"{sheet.Name}: " + string.Join(
                ",",
                sheet.Rows.GroupBy(row => row).Where(group => group.Count() > 1).Select(group => group.Key)))
            .ToArray();

        Assert.Empty(problems);
    }

    // ================= step4 符合高風險條件傳票(所選情境的條件欄)=================

    [Fact]
    public async Task HighRiskVoucher_ConditionColumns_AllPositions_YMatchesMatchedPositions()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", ConditionColumnScenarioPayload());

        using var workbook = await WriteDemoAndReadAsync(host, ctx.ProjectId);
        var sheet = workbook.Worksheet(HighRiskVoucherSheet);

        // 欄標第 11 列:A 編號/B 傳票號碼/C 總帳日期/D 編製者/E 傳票總金額 + 動態 C1..CN(全 position 升冪)。
        Assert.Equal("傳票號碼", sheet.Cell("B11").GetString());
        Assert.Equal("C1", sheet.Cell("F11").GetString());
        Assert.Equal("C2", sheet.Cell("G11").GetString());
        Assert.Equal("C3", sheet.Cell("H11").GetString()); // 全 position(含 0 命中的 C3)都建欄
        AssertSafeAmountColumn(sheet, 5);
        AssertSingleLine(sheet.Cell("E13"));

        // 資料自第 13 列(第 12 列為固定說明)。取一張命中傳票,核對其 Y 欄集 == matchedPositions。
        var hitDoc = (await DemoProjectPipeline.QueryStringListAsync(host, ctx.ProjectId,
            "SELECT g.voucher_number FROM filter_hit r " +
            "JOIN gl g ON g.entry_id = r.entry_id " +
            "WHERE g.voucher_number IS NOT NULL ORDER BY g.voucher_number LIMIT 1;"))[0]!;
        var docRow = FindRowByColumnValue(sheet, "B", hitDoc, 13);
        Assert.True(docRow > 0, "命中傳票應出現在 step4");

        var matched = (await DemoProjectPipeline.QueryStringListAsync(host, ctx.ProjectId,
                "SELECT DISTINCT r.scenario_id FROM filter_hit r " +
                "JOIN gl g ON g.entry_id = r.entry_id " +
                "WHERE g.voucher_number = @doc ORDER BY r.scenario_id;", ("@doc", hitDoc)))
            .Select(s => int.Parse(s!)).ToHashSet();

        // 動態欄集 C1=F、C2=G、C3=H;每欄依該傳票是否含該 position 標 Y 或空(data-structure 對映)。
        foreach (var (pos, col) in new[] { (1, "F"), (2, "G"), (3, "H") })
        {
            var cell = sheet.Cell($"{col}{docRow}").GetString();
            if (matched.Contains(pos))
            {
                Assert.Equal("Y", cell);
            }
            else
            {
                Assert.NotEqual("Y", cell);
            }
        }

        // 手填欄(P-U)留空。
        Assert.True(sheet.Cell($"P{docRow}").IsEmpty());
        Assert.True(sheet.Cell($"U{docRow}").IsEmpty());

        // step4 照舊版保護，只鎖程式寫入的 A13:O 資料列。
        // 這裡只用到 C1 到 C3，I 到 O 的條件欄沒有值也照舊版鎖定；表頭、手填欄與資料列下方都可以填。
        var lastRow = 12 + ReadColumnFrom(sheet, "A", 13).Count;
        Assert.True(sheet.Protection.IsProtected);
        Assert.All(
            new[] { $"A{docRow}", $"E{docRow}", $"I{docRow}", $"O{docRow}", $"O{lastRow}" },
            reference => Assert.True(sheet.Cell(reference).Style.Protection.Locked, $"{reference} 應鎖定。"));
        Assert.All(
            new[] { "A1", "A11", "A12", $"P{docRow}", $"U{docRow}", $"A{lastRow + 1}" },
            reference => Assert.False(sheet.Cell(reference).Style.Protection.Locked, $"{reference} 應可填。"));
    }

    [Fact]
    public async Task HighRiskVoucherAndDetail_SelectedC2_ExcludeC1OnlyVoucher_AndUseWholeVoucherDebitTotal()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupSelectedScenarioProjectAsync(host);
        await host.DispatchAsync("filter.commit", SelectedScenarioPayload());

        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        var savedScenarios = await new LocalFilterScenarioStore(database)
            .ListAsync(projectId, CancellationToken.None);
        var c2Position = Assert.Single(savedScenarios, scenario => scenario.Name == "C2 專屬").Position;
        Assert.Equal(2, c2Position);

        var context = (await CurrentContextForAsync(host, projectId)) with
        {
            ScenarioPositions = [c2Position]
        };
        var plan = await CurrentPlanForAsync(host, context);
        await using var stream = new MemoryStream();
        await ((IWorkingPaperPlanWriter)BuildWriter(host)).WriteAsync(stream, context, plan, CancellationToken.None);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);

        // 反例：只命中 C1 的傳票，不得混入只選 C2 時的 step4 與 step4-1 母體。
        var vouchers = workbook.Worksheet(HighRiskVoucherSheet);
        // 範本固定列舉 C1-C10 欄標；選取範圍只控制資料列的 Y，不刪範本文字。
        Assert.Equal("C1", vouchers.Cell("F11").GetString());
        Assert.Equal($"C{c2Position}", vouchers.Cell("G11").GetString());
        Assert.Equal(0, FindRowByColumnValue(vouchers, "B", "DOC-C1", 13));
        var c2VoucherRow = FindRowByColumnValue(vouchers, "B", "DOC-C2", 13);
        Assert.True(c2VoucherRow > 0, "只選 C2 時 DOC-C2 應列入 step4。");
        Assert.Equal(
            new[] { "DOC-C2" },
            ReadColumnFrom(vouchers, "B", 13));

        // 依規格的預期值：傳票總額是命中傳票的完整 GL 借方，不是只加總 C2 命中行。
        Assert.Equal(40d, vouchers.Cell(c2VoucherRow, 5).GetDouble());
        Assert.Equal("Y", vouchers.Cell(c2VoucherRow, 7).GetString());

        var details = workbook.Worksheet(HighRiskVoucherDetailSheet);
        Assert.Equal(0, FindRowByColumnValue(details, "A", "DOC-C1", 6));
        var c2Rows = Enumerable.Range(6, Math.Max(0, details.LastRowUsed()!.RowNumber() - 5))
            .Where(row => details.Cell(row, 1).GetString() == "DOC-C2")
            .ToList();
        Assert.Equal(3, c2Rows.Count); // 命中傳票的三條 GL 行要完整列出。
        // step4-1 依計畫只列所選集合裡有行層命中的位置，
        // 標記欄緊接在計畫的欄位之後。
        var tagColumn = plan.HighRiskVoucherDetailColumns.Count + 1;
        Assert.Equal(new[] { c2Position }, plan.RowHitScenarioPositions);
        Assert.Equal("C2_TAG", details.Cell(5, tagColumn).GetString());
        Assert.True(details.Cell(5, tagColumn + 1).IsEmpty());
        Assert.Equal(1, c2Rows.Count(row => details.Cell(row, tagColumn).GetString() == "Y"));
    }

    [Fact]
    public async Task HighRiskVoucher_ZeroHitSelection_EmitsProtectedFixedHeaderWithZeroVoucherRows()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", ConditionColumnScenarioPayload());
        var context = (await CurrentContextForAsync(host, ctx.ProjectId)) with
        {
            ScenarioPositions = [3]
        };

        await using var stream = new MemoryStream();
        await WriteWithCurrentPlanAsync(host, BuildWriter(host), stream, context, CancellationToken.None);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(HighRiskVoucherSheet);

        Assert.Equal(
            Enumerable.Range(1, 10).Select(position => $"C{position}"),
            Enumerable.Range(6, 10).Select(column => sheet.Cell(11, column).GetString()));
        Assert.Empty(ReadColumnFrom(sheet, "B", 13));
        Assert.True(sheet.Protection.IsProtected);
        Assert.All(
            sheet.Range("P13:U13").Cells(),
            cell => Assert.True(cell.IsEmpty()));
    }

    // ================= step4-1 行層明細(動態欄只 rowHitCount>0 的 position)=================

    [Fact]
    public async Task HighRiskVoucherDetail_ConditionColumns_OnlyRowHitPositions_YMatchesMatchedPositions()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", ConditionColumnScenarioPayload());

        var context = await CurrentContextForAsync(host, ctx.ProjectId);
        var plan = await CurrentPlanForAsync(host, context);
        await using var stream = new MemoryStream();
        await ((IWorkingPaperPlanWriter)BuildWriter(host)).WriteAsync(
            stream,
            context,
            plan,
            CancellationToken.None);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(HighRiskVoucherDetailSheet);

        var expectedHeaders = plan.HighRiskVoucherDetailColumns.Select(column => column.Header)
            .Concat(plan.RowHitScenarioPositions.Select(position => $"C{position}_TAG"))
            .ToArray();
        Assert.Equal(
            expectedHeaders,
            Enumerable.Range(1, expectedHeaders.Length)
                .Select(column => sheet.Cell(5, column).GetString())
                .ToArray());
        Assert.True(sheet.Cell(5, expectedHeaders.Length + 1).IsEmpty());

        // rowHitCount>0 的 position 集(獨立 recount)= 動態欄集;0 行命中的 position(如 C3 天價)不建欄。
        var rowHitPositions = (await DemoProjectPipeline.QueryStringListAsync(host, ctx.ProjectId,
                "SELECT scenario_id FROM filter_hit " +
                "GROUP BY scenario_id HAVING COUNT(*) > 0 ORDER BY scenario_id;"))
            .Select(s => int.Parse(s!)).ToList();
        Assert.True(rowHitPositions.Count > 0, "demo 應有行層命中情境");
        Assert.Equal(rowHitPositions, plan.RowHitScenarioPositions);

        // 取一個命中行(entry_id),核對其在 step4-1 對應列的 Y 欄集 == matchedPositions。
        var hitEntryId = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT MIN(entry_id) FROM filter_hit;");
        var hitDoc = (await DemoProjectPipeline.QueryStringListAsync(host, ctx.ProjectId,
            "SELECT voucher_number FROM gl WHERE entry_id=@id;", ("@id", hitEntryId)))[0]!;
        var hitLine = (await DemoProjectPipeline.QueryStringListAsync(host, ctx.ProjectId,
            "SELECT line_item FROM gl WHERE entry_id=@id;", ("@id", hitEntryId)))[0];

        var matched = (await DemoProjectPipeline.QueryStringListAsync(host, ctx.ProjectId,
                "SELECT scenario_id FROM filter_hit WHERE entry_id=@id ORDER BY scenario_id;",
                ("@id", hitEntryId)))
            .Select(s => int.Parse(s!)).ToHashSet();
        Assert.NotEmpty(matched);

        var documentColumn = plan.HighRiskVoucherDetailColumns
            .Select((column, index) => (column, index))
            .Single(item => string.Equals(
                item.column.Header,
                "傳票號碼_JE",
                StringComparison.Ordinal))
            .index + 1;
        var lineColumn = plan.HighRiskVoucherDetailColumns
            .Select((column, index) => (column, index))
            .Single(item => string.Equals(
                item.column.Header,
                "傳票文件項次_JE_S",
                StringComparison.Ordinal))
            .index + 1;
        var rowNo = Enumerable.Range(
                6,
                Math.Max(0, (sheet.LastRowUsed()?.RowNumber() ?? 5) - 5))
            .Single(row =>
                sheet.Cell(row, documentColumn).GetString() == hitDoc
                && sheet.Cell(row, lineColumn).GetString() == (hitLine ?? string.Empty));
        Assert.True(rowNo > 0, "命中行應出現在 step4-1");

        for (var index = 0; index < rowHitPositions.Count; index++)
        {
            var position = rowHitPositions[index];
            var tagCol = plan.HighRiskVoucherDetailColumns.Count + index + 1;
            var cell = sheet.Cell(rowNo, tagCol).GetString();
            if (matched.Contains(position))
            {
                Assert.Equal("Y", cell);
            }
            else
            {
                Assert.NotEqual("Y", cell);
            }
        }

        var zeroHitPositions = Enumerable.Range(1, 10).Except(rowHitPositions);
        foreach (var position in zeroHitPositions)
        {
            Assert.DoesNotContain(
                $"C{position}_TAG",
                expectedHeaders,
                StringComparer.Ordinal);
        }
    }

    [Fact]
    public async Task HighRiskVoucherDetail_DateCellThatIsNotADate_IsWrittenAsTheStoredText()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                // 「核准人員」整欄是 Excel 的日期儲存格，配成文字欄位傳票核准人員，欄位型態照來源記成日期。
                // 第二列是 2150 年，照樣讀成日期。第三列的序列值是負數，換算不出 Excel 的日期，
                // 存下來的是序列值的文字，不是 yyyy-MM-dd。
                gl.WithColumns(
                    "傳票號碼",
                    "傳票項次",
                    "傳票日期",
                    "核准日期",
                    "科目代號",
                    "科目名稱",
                    "摘要",
                    "金額",
                    "借方旗標",
                    "核准人員");
                gl.AddRow("DOC-0001", "01", "2025-03-05", "2025-03-06", "1101", "現金", "命中 SEQ-0001", 1m, 1, new DateTime(2025, 3, 6));
                gl.AddRow("DOC-0002", "01", "2025-03-05", "2025-03-06", "1101", "現金", "命中 SEQ-0002", 1m, 0, new DateTime(2150, 1, 1));
                gl.AddRow("DOC-0003", "01", "2025-03-05", "2025-03-06", "1101", "現金", "命中 SEQ-0003", 1m, 0, new DateTime(1850, 1, 1));
            },
            validateForDownstream: true);
        await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new
            {
                populationScope = GlPopulationScopeValues.AuditPeriod,
                scenarios = new[]
                {
                    new
                    {
                        name = "換算不出日期的日期儲存格",
                        rationale = "匯出不能因為這一格失敗",
                        groups = new[]
                        {
                            new
                            {
                                join = "and",
                                conditions = new[]
                                {
                                    new
                                    {
                                        join = "and",
                                        type = "fieldValue", field = "description", @operator = "contains", values = new[] { "命中" }
                                    }
                                }
                            }
                        }
                    }
                }
            }));

        var context = await CurrentContextForAsync(host, projectId);
        var plan = await CurrentPlanForAsync(host, context);
        await using var stream = new MemoryStream();
        await ((IWorkingPaperPlanWriter)BuildWriter(host)).WriteAsync(stream, context, plan, CancellationToken.None);

        var approverColumn = plan.HighRiskVoucherDetailColumns
            .Select(column => column.Header)
            .ToList()
            .IndexOf("傳票核准人員_JE") + 1;
        Assert.True(approverColumn > 0);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(HighRiskVoucherDetailSheet);
        Assert.Equal(new DateTime(2025, 3, 6), sheet.Cell(6, approverColumn).GetDateTime());
        Assert.Equal(new DateTime(2150, 1, 1), sheet.Cell(7, approverColumn).GetDateTime());
        // Excel 的序列值 1 是 1900-01-01，1850-01-01 比它早 18262 天，所以這一格的序列值是 -18261。
        Assert.Equal(XLDataType.Text, sheet.Cell(8, approverColumn).DataType);
        Assert.Equal("-18261.0", sheet.Cell(8, approverColumn).GetString());
    }

    // 日期儲存格的序列值 60 與 0 在 Excel 顯示成日曆上沒有的 1900-02-29 與 1900 年 1 月 0 日，JET 照 Excel 顯示的年月日存下來。
    // 底稿 step4-1 符合高風險條件傳票明細照存下來的文字寫成文字儲存格，匯出照常完成；序列值 1 的 1900-01-01 早於 1900-03-01，同樣寫成文字。
    [Fact]
    public async Task HighRiskVoucherDetail_DateCellExcelShowsAsNonexistentDay_IsWrittenAsTheShownText()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                gl.WithColumns(
                    "傳票號碼",
                    "傳票項次",
                    "傳票日期",
                    "核准日期",
                    "科目代號",
                    "科目名稱",
                    "摘要",
                    "金額",
                    "借方旗標",
                    "核准人員");
                gl.AddRow("DOC-0001", "01", "2025-03-05", "2025-03-06", "1101", "現金", "命中 SEQ-0001", 1m, 1, new DateTime(2025, 3, 6));
                gl.AddRow("DOC-0002", "01", "2025-03-05", "2025-03-06", "1101", "現金", "命中 SEQ-0002", 1m, 0, new ExcelDateSerial(60));
                gl.AddRow("DOC-0003", "01", "2025-03-05", "2025-03-06", "1101", "現金", "命中 SEQ-0003", 1m, 0, new ExcelDateSerial(0));
                gl.AddRow("DOC-0004", "01", "2025-03-05", "2025-03-06", "1101", "現金", "命中 SEQ-0004", 1m, 0, new ExcelDateSerial(1));
            },
            validateForDownstream: true);
        await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new
            {
                populationScope = GlPopulationScopeValues.AuditPeriod,
                scenarios = new[]
                {
                    new
                    {
                        name = "日曆上沒有的日期",
                        rationale = "匯出不能因為這一格失敗",
                        groups = new[]
                        {
                            new
                            {
                                join = "and",
                                conditions = new[]
                                {
                                    new
                                    {
                                        join = "and",
                                        type = "fieldValue", field = "description", @operator = "contains", values = new[] { "命中" }
                                    }
                                }
                            }
                        }
                    }
                }
            }));

        var context = await CurrentContextForAsync(host, projectId);
        var plan = await CurrentPlanForAsync(host, context);
        await using var stream = new MemoryStream();
        await ((IWorkingPaperPlanWriter)BuildWriter(host)).WriteAsync(stream, context, plan, CancellationToken.None);

        var approverColumn = plan.HighRiskVoucherDetailColumns
            .Select(column => column.Header)
            .ToList()
            .IndexOf("傳票核准人員_JE") + 1;
        Assert.True(approverColumn > 0);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(HighRiskVoucherDetailSheet);
        Assert.Equal(new DateTime(2025, 3, 6), sheet.Cell(6, approverColumn).GetDateTime());
        int[] textRows = [7, 8, 9];
        Assert.All(textRows, row => Assert.Equal(XLDataType.Text, sheet.Cell(row, approverColumn).DataType));
        Assert.Equal(
            ["1900-02-29", "1900-01-00", "1900-01-01"],
            textRows.Select(row => sheet.Cell(row, approverColumn).GetString()));
    }

    // 1904 日期系統的序列值 -1460.75 與 -1460.5 早於 1900-01-01，換算不出日期，讀取器存下序列值的文字「1.25」與「1.5」。
    // 底稿 step4-1 符合高風險條件傳票明細照存下來的文字寫成文字儲存格，不把它們當成沒寫年份的 1 月 25 日與 1 月 5 日，寫成匯出當年的日期。
    [Fact]
    public async Task HighRiskVoucherDetail_SerialTextThatLooksLikeMonthAndDay_IsWrittenAsTheStoredText()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                gl.UseDate1904();
                gl.WithColumns(
                    "傳票號碼",
                    "傳票項次",
                    "傳票日期",
                    "核准日期",
                    "科目代號",
                    "科目名稱",
                    "摘要",
                    "金額",
                    "借方旗標",
                    "核准人員");
                // 1904 日期系統的序列值 44260 是 2025-03-06。
                gl.AddRow("DOC-0001", "01", "2025-03-05", "2025-03-06", "1101", "現金", "命中 SEQ-0001", 1m, 1, new ExcelDateSerial(44260));
                gl.AddRow("DOC-0002", "01", "2025-03-05", "2025-03-06", "1101", "現金", "命中 SEQ-0002", 1m, 0, new ExcelDateSerial(-1460.75));
                gl.AddRow("DOC-0003", "01", "2025-03-05", "2025-03-06", "1101", "現金", "命中 SEQ-0003", 1m, 0, new ExcelDateSerial(-1460.5));
            },
            validateForDownstream: true);
        await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new
            {
                populationScope = GlPopulationScopeValues.AuditPeriod,
                scenarios = new[]
                {
                    new
                    {
                        name = "像月日的序列值文字",
                        rationale = "照存下來的文字寫出",
                        groups = new[]
                        {
                            new
                            {
                                join = "and",
                                conditions = new[]
                                {
                                    new
                                    {
                                        join = "and",
                                        type = "fieldValue", field = "description", @operator = "contains", values = new[] { "命中" }
                                    }
                                }
                            }
                        }
                    }
                }
            }));

        var context = await CurrentContextForAsync(host, projectId);
        var plan = await CurrentPlanForAsync(host, context);
        await using var stream = new MemoryStream();
        await ((IWorkingPaperPlanWriter)BuildWriter(host)).WriteAsync(stream, context, plan, CancellationToken.None);

        var approverColumn = plan.HighRiskVoucherDetailColumns
            .Select(column => column.Header)
            .ToList()
            .IndexOf("傳票核准人員_JE") + 1;
        Assert.True(approverColumn > 0);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(HighRiskVoucherDetailSheet);
        Assert.Equal(new DateTime(2025, 3, 6), sheet.Cell(6, approverColumn).GetDateTime());
        int[] textRows = [7, 8];
        Assert.Equal(
            ["Text|1.25", "Text|1.5"],
            textRows.Select(row => sheet.Cell(row, approverColumn).DataType + "|" + sheet.Cell(row, approverColumn).GetString()));
    }

    // 其他程式產生的 xlsx 可能在日期儲存格寫 NaN 或無限大，換算不出日期，讀取器存下「NaN」「Infinity」「-Infinity」。
    // 這是來源資料本來的樣子，底稿 step4-1 符合高風險條件傳票明細照存下來的文字寫成文字儲存格，匯出照常完成。
    [Fact]
    public async Task HighRiskVoucherDetail_DateCellThatIsNaNOrInfinity_IsWrittenAsTheStoredText()
    {
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                gl.WithColumns(
                    "傳票號碼",
                    "傳票項次",
                    "傳票日期",
                    "核准日期",
                    "科目代號",
                    "科目名稱",
                    "摘要",
                    "金額",
                    "借方旗標",
                    "核准人員");
                gl.AddRow("DOC-0001", "01", "2025-03-05", "2025-03-06", "1101", "現金", "命中 SEQ-0001", 1m, 1, new DateTime(2025, 3, 6));
                gl.AddRow("DOC-0002", "01", "2025-03-05", "2025-03-06", "1101", "現金", "命中 SEQ-0002", 1m, 0, new ExcelDateSerial(double.NaN));
                gl.AddRow("DOC-0003", "01", "2025-03-05", "2025-03-06", "1101", "現金", "命中 SEQ-0003", 1m, 0, new ExcelDateSerial(double.PositiveInfinity));
                gl.AddRow("DOC-0004", "01", "2025-03-05", "2025-03-06", "1101", "現金", "命中 SEQ-0004", 1m, 0, new ExcelDateSerial(double.NegativeInfinity));
            },
            validateForDownstream: true);
        await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new
            {
                populationScope = GlPopulationScopeValues.AuditPeriod,
                scenarios = new[]
                {
                    new
                    {
                        name = "不是有限數值的日期儲存格",
                        rationale = "匯出不能因為這一格失敗",
                        groups = new[]
                        {
                            new
                            {
                                join = "and",
                                conditions = new[]
                                {
                                    new
                                    {
                                        join = "and",
                                        type = "fieldValue", field = "description", @operator = "contains", values = new[] { "命中" }
                                    }
                                }
                            }
                        }
                    }
                }
            }));

        var context = await CurrentContextForAsync(host, projectId);
        var plan = await CurrentPlanForAsync(host, context);
        await using var stream = new MemoryStream();
        var exportError = await Record.ExceptionAsync(() =>
            ((IWorkingPaperPlanWriter)BuildWriter(host)).WriteAsync(stream, context, plan, CancellationToken.None));
        Assert.Null(exportError);

        var approverColumn = plan.HighRiskVoucherDetailColumns
            .Select(column => column.Header)
            .ToList()
            .IndexOf("傳票核准人員_JE") + 1;
        Assert.True(approverColumn > 0);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(HighRiskVoucherDetailSheet);
        Assert.Equal(new DateTime(2025, 3, 6), sheet.Cell(6, approverColumn).GetDateTime());
        int[] textRows = [7, 8, 9];
        Assert.Equal(
            ["Text|NaN", "Text|Infinity", "Text|-Infinity"],
            textRows.Select(row => sheet.Cell(row, approverColumn).DataType + "|" + sheet.Cell(row, approverColumn).GetString()));
    }

    // 其他程式產生的 xlsx 用 t="d" 的儲存格寫日期的文字，也可能寫進不是日期的內容。PBC-GL 照原文存下，
    // 底稿 step4-1 符合高風險條件傳票明細照存下來的文字寫成文字儲存格，匯出照常完成；讀得成日期的 2025-03-06 也照原文寫成文字。
    [Fact]
    public async Task HighRiskVoucherDetail_DateTypedCellThatIsNotADate_ExportsWithTheStoredText()
    {
        string[] written = ["2025-03-06", "abc", "2025-02-30", "13:45"];
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                gl.WithColumns(
                    "傳票號碼",
                    "傳票項次",
                    "傳票日期",
                    "核准日期",
                    "科目代號",
                    "科目名稱",
                    "摘要",
                    "金額",
                    "借方旗標",
                    "核准人員");
                for (var index = 0; index < written.Length; index++)
                {
                    gl.AddRow(
                        $"DOC-{index + 1:D4}", "01", "2025-03-05", "2025-03-06", "1101", "現金", $"命中 SEQ-{index + 1:D4}",
                        1m, index == 0 ? 1 : 0, new DateTypedCell(written[index]));
                }
            },
            validateForDownstream: true);
        await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new
            {
                populationScope = GlPopulationScopeValues.AuditPeriod,
                scenarios = new[]
                {
                    new
                    {
                        name = "t=d 的儲存格",
                        rationale = "匯出不能因為這一格失敗",
                        groups = new[]
                        {
                            new
                            {
                                join = "and",
                                conditions = new[]
                                {
                                    new
                                    {
                                        join = "and",
                                        type = "fieldValue", field = "description", @operator = "contains", values = new[] { "命中" }
                                    }
                                }
                            }
                        }
                    }
                }
            }));

        var context = await CurrentContextForAsync(host, projectId);
        var plan = await CurrentPlanForAsync(host, context);
        await using var stream = new MemoryStream();
        var exportError = await Record.ExceptionAsync(() =>
            ((IWorkingPaperPlanWriter)BuildWriter(host)).WriteAsync(stream, context, plan, CancellationToken.None));
        Assert.Null(exportError);

        var approverColumn = plan.HighRiskVoucherDetailColumns
            .Select(column => column.Header)
            .ToList()
            .IndexOf("傳票核准人員_JE") + 1;
        Assert.True(approverColumn > 0);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(HighRiskVoucherDetailSheet);
        Assert.Equal(
            written.Select(text => "Text|" + text),
            Enumerable.Range(6, written.Length)
                .Select(row => sheet.Cell(row, approverColumn).DataType + "|" + sheet.Cell(row, approverColumn).GetString()));
    }

    // 其他程式產生的 xlsx 可能在沒有套日期格式的數值儲存格寫 NaN 或無限大，讀取器照原文存下「NaN」「Infinity」「-Infinity」。
    // 這不是數值，底稿 step4-1 符合高風險條件傳票明細照存下來的文字寫成文字儲存格，匯出照常完成；同一欄的 1001 仍寫成數值儲存格。
    [Fact]
    public async Task HighRiskVoucherDetail_NumberCellThatIsNaNOrInfinity_IsWrittenAsTheStoredText()
    {
        double[] written = [1001d, double.NaN, double.PositiveInfinity, double.NegativeInfinity];
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                gl.WithColumns(
                    "傳票號碼",
                    "傳票項次",
                    "傳票日期",
                    "核准日期",
                    "科目代號",
                    "科目名稱",
                    "摘要",
                    "金額",
                    "借方旗標",
                    "核准人員");
                for (var index = 0; index < written.Length; index++)
                {
                    gl.AddRow(
                        $"DOC-{index + 1:D4}", "01", "2025-03-05", "2025-03-06", "1101", "現金", $"命中 SEQ-{index + 1:D4}",
                        1m, index == 0 ? 1 : 0, written[index]);
                }
            },
            validateForDownstream: true);
        await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new
            {
                populationScope = GlPopulationScopeValues.AuditPeriod,
                scenarios = new[]
                {
                    new
                    {
                        name = "不是有限數值的數值儲存格",
                        rationale = "匯出不能因為這一格失敗",
                        groups = new[]
                        {
                            new
                            {
                                join = "and",
                                conditions = new[]
                                {
                                    new
                                    {
                                        join = "and",
                                        type = "fieldValue", field = "description", @operator = "contains", values = new[] { "命中" }
                                    }
                                }
                            }
                        }
                    }
                }
            }));

        var context = await CurrentContextForAsync(host, projectId);
        var plan = await CurrentPlanForAsync(host, context);
        await using var stream = new MemoryStream();
        var exportError = await Record.ExceptionAsync(() =>
            ((IWorkingPaperPlanWriter)BuildWriter(host)).WriteAsync(stream, context, plan, CancellationToken.None));
        Assert.Null(exportError);

        var approverColumn = plan.HighRiskVoucherDetailColumns
            .Select(column => column.Header)
            .ToList()
            .IndexOf("傳票核准人員_JE") + 1;
        Assert.True(approverColumn > 0);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(HighRiskVoucherDetailSheet);
        Assert.Equal(XLDataType.Number, sheet.Cell(6, approverColumn).DataType);
        Assert.Equal(1001d, sheet.Cell(6, approverColumn).GetDouble());
        int[] textRows = [7, 8, 9];
        Assert.Equal(
            ["Text|NaN", "Text|Infinity", "Text|-Infinity"],
            textRows.Select(row => sheet.Cell(row, approverColumn).DataType + "|" + sheet.Cell(row, approverColumn).GetString()));
    }

    [Fact]
    public async Task HighRiskVoucherDetail_UsesLatePageRawValuesSignedAmountAndFullDataWidths()
    {
        const int rowCount = 205;
        using var host = new HandlerTestHost();
        var projectId = await InlineWorkbookProject.SetupAsync(
            host,
            gl =>
            {
                // step4-1 只列 JET 欄位，沒有配對、只是欄名以 _JE 結尾的來源欄不進表。
                // 落在後面分頁的最長值、欄寬上限與時間型原始值，由配對成分錄來源模組、傳票建立人員與傳票核准人員的來源欄檢查，
                // 列的順序記在傳票摘要。
                gl.WithColumns(
                    "傳票號碼",
                    "傳票項次",
                    "傳票日期",
                    "核准日期",
                    "科目代號",
                    "科目名稱",
                    "摘要",
                    "金額",
                    "借方旗標",
                    "來源模組",
                    "建立人員",
                    "核准人員");
                for (var index = 1; index <= rowCount; index++)
                {
                    gl.AddRow(
                        $"DOC-{index:D4}",
                        "01",
                        "2025-03-05",
                        "2025-03-06",
                        "1101",
                        "現金",
                        $"命中 SEQ-{index:D4}",
                        index == rowCount ? 123.4567m : 1m,
                        index == rowCount ? 0 : 1,
                        index == rowCount ? new string('L', 60) : "short",
                        index == rowCount ? new string('界', 150) : "short",
                        index == rowCount
                            ? new TimeSpan(0, 6, 7, 8, 900)
                            : new TimeSpan(6, 7, 8));
                }
            },
            validateForDownstream: true);
        await host.DispatchAsync(
            "filter.commit",
            JsonSerializer.Serialize(new
            {
                populationScope = GlPopulationScopeValues.AuditPeriod,
                scenarios = new[]
                {
                    new
                    {
                        name = "完整欄寬",
                        rationale = "晚頁最大值必須參與 AutoFit",
                        groups = new[]
                        {
                            new
                            {
                                join = "and",
                                conditions = new[]
                                {
                                    new
                                    {
                                        join = "and",
                                        type = "fieldValue", field = "description", @operator = "contains", values = new[] { "命中" }
                                    }
                                }
                            }
                        }
                    }
                }
            }));

        var context = await CurrentContextForAsync(host, projectId);
        var plan = await CurrentPlanForAsync(host, context);
        await using var stream = new MemoryStream();
        var stats = await ((IWorkingPaperPlanWriter)BuildWriter(host)).WriteAsync(
            stream,
            context,
            plan,
            CancellationToken.None);

        var highRiskVoucherDetail = Assert.Single(
            stats.SheetStats,
            stat => string.Equals(stat.SheetName, HighRiskVoucherDetailSheet, StringComparison.Ordinal));
        Assert.Equal(rowCount, highRiskVoucherDetail.RowsWritten);
        var headers = plan.HighRiskVoucherDetailColumns.Select(column => column.Header)
            .Concat(["C1_TAG"])
            .ToArray();
        var tokenColumn = Array.IndexOf(headers, "傳票摘要_JE") + 1;
        var noCapColumn = Array.IndexOf(headers, "分錄來源模組_JE") + 1;
        var capColumn = Array.IndexOf(headers, "傳票建立人員_JE") + 1;
        var timeColumn = Array.IndexOf(headers, "傳票核准人員_JE") + 1;
        var amountColumn = Array.IndexOf(headers, "傳票金額_JE") + 1;
        var tagColumn = headers.Length;
        Assert.All(
            new[] { tokenColumn, noCapColumn, capColumn, timeColumn, amountColumn },
            column => Assert.True(column > 0));

        stream.Position = 0;
        using (var workbook = new XLWorkbook(stream))
        {
            var sheet = workbook.Worksheet(HighRiskVoucherDetailSheet);
            Assert.Equal(
                headers,
                Enumerable.Range(1, headers.Length)
                    .Select(column => sheet.Cell(5, column).GetString())
                    .ToArray());
            Assert.Equal("命中 SEQ-0001", sheet.Cell(6, tokenColumn).GetString());
            Assert.Equal($"命中 SEQ-{rowCount:D4}", sheet.Cell(5 + rowCount, tokenColumn).GetString());
            Assert.Equal(
                new string('L', 60),
                sheet.Cell(5 + rowCount, noCapColumn).GetString());
            Assert.Equal(
                new string('界', 150),
                sheet.Cell(5 + rowCount, capColumn).GetString());
            Assert.Equal(-123.4567d, sheet.Cell(5 + rowCount, amountColumn).GetDouble());
            Assert.Equal("Y", sheet.Cell(5 + rowCount, tagColumn).GetString());
            Assert.True(sheet.Cell(5, headers.Length + 1).IsEmpty());
            Assert.False(sheet.Cell(5 + rowCount, noCapColumn).Style.Alignment.WrapText);
            Assert.Equal(0, sheet.Cell(5 + rowCount, noCapColumn).Style.Alignment.Indent);
            Assert.False(sheet.Cell(5 + rowCount, noCapColumn).Style.Alignment.ShrinkToFit);
        }

        stream.Position = 0;
        using var package = SpreadsheetDocument.Open(stream, false);
        var workbookPart = Assert.IsType<WorkbookPart>(package.WorkbookPart);
        var sheetRef = workbookPart.RequiredWorkbook().Sheets!.Elements<Sheet>()
            .Single(sheet => string.Equals(
                sheet.Name?.Value,
                HighRiskVoucherDetailSheet,
                StringComparison.Ordinal));
        var worksheet = Assert.IsType<WorksheetPart>(
                workbookPart.GetPartById(sheetRef.Id!.Value!))
            .RequiredWorksheet();
        var widths = Assert.Single(worksheet.Elements<Columns>())
            .Elements<Column>()
            .ToDictionary(column => checked((int)column.Min!.Value));
        Assert.Equal(71d, widths[noCapColumn].Width!.Value);
        Assert.Equal(255d, widths[capColumn].Width!.Value);
        // 時間欄配成傳票核准人員，欄寬由表頭「傳票核准人員_JE」決定，不是資料值的 12。
        Assert.Equal(20d, widths[timeColumn].Width!.Value);
        Assert.All(widths.Values, column => Assert.True(column.BestFit?.Value));
    }

}
