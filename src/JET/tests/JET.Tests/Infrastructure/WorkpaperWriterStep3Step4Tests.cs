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
using static JET.Tests.Infrastructure.WorkpaperWriterTestSupport;

namespace JET.Tests.Infrastructure;

/// <summary>底稿寫出器：step3 條件表、step4 傳票矩陣與 step4-1 傳票明細。</summary>
public sealed class WorkpaperWriterStep3Step4Tests
{
    // ================= Task 4:step3 高風險條件彙總 =================

    [Fact]
    public async Task Step3_ConditionTable_MatchesScenariosNameRationaleAndVoucherHits()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", MatrixScenarioPayload());

        using var workbook = await WriteDemoAndReadAsync(host, ctx.ProjectId);
        var sheet = workbook.Worksheet(Step3Sheet);

        // 欄標第 18 列(C/D/E),資料自第 19 列;B 代號 C{position} 升冪。
        var codes = ReadColumnFrom(sheet, "B", 19);
        Assert.Equal(new[] { "C1", "C2", "C3" }, codes);

        // C1 列:C 條件描述==情境 name、D 原因==rationale、E 符合傳票數==voucherHitCount recount。
        var c1Row = FindRowByColumnValue(sheet, "B", "C1", 19);
        Assert.Equal("提前過帳", sheet.Cell($"C{c1Row}").GetString());
        Assert.Equal("過帳日期早於傳票日期,可能顯示回溯日期", sheet.Cell($"D{c1Row}").GetString());

        var c1Vouchers = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT COUNT(DISTINCT g.document_number) FROM result_filter_run r " +
            "JOIN target_gl_entry g ON g.entry_id = r.entry_id " +
            "WHERE r.scenario_position = 1 AND g.document_number IS NOT NULL;");
        Assert.Equal(c1Vouchers, (long)sheet.Cell($"E{c1Row}").GetDouble());

        // 0 命中情境(C3 天價)仍列出、傳票數 0。
        var c3Row = FindRowByColumnValue(sheet, "B", "C3", 19);
        Assert.Equal(0d, sheet.Cell($"E{c3Row}").GetDouble());
    }

    /// <summary>
    /// WorkingPaper 的 step3 可見情境表固定止於 E 欄；即使 public compatibility
    /// context 帶入舊 ConditionLogic，F 欄也不得再鏡射條件布林式。
    /// </summary>
    [Fact]
    public async Task Step3_VisibleConditionTable_EndsAtColumnE()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", MatrixScenarioPayload());

        // 真 store 列出已存情境 → 真渲染器 → position→條件邏輯（鏡射 ExportWorkpaperStreamHandler 的計算）。
        var database = new SqliteProjectDatabase(new JetProjectFolder(host.ProjectsRoot));
        var saved = await new LocalFilterScenarioStore(database).ListAsync(ctx.ProjectId, CancellationToken.None);
        var conditionLogic = saved.ToDictionary(s => s.Position, s =>
        {
            using var definition = JsonDocument.Parse(s.DefinitionJson);
            return FilterConditionRenderer.Render(definition.RootElement);
        });

        var context = (await CurrentContextForAsync(host, ctx.ProjectId)) with
        {
            ScenarioConditionLogic = conditionLogic
        };
        var stream = new MemoryStream();
        await BuildWriter(host).WriteAsync(stream, context, CancellationToken.None);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(Step3Sheet);

        Assert.All(
            Enumerable.Range(18, 11),
            row => Assert.True(sheet.Cell(row, 6).IsEmpty(), $"F{row} 應保持空白。"));
    }

    // ================= Task 4:step4 符合高風險條件傳票(動態全 position 欄)=================

    [Fact]
    public async Task Step4_VoucherMatrix_DynamicColumnsAllPositions_YMatchesMatchedPositions()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", MatrixScenarioPayload());

        using var workbook = await WriteDemoAndReadAsync(host, ctx.ProjectId);
        var sheet = workbook.Worksheet(Step4Sheet);

        // 欄標第 11 列:A 編號/B 傳票號碼/C 總帳日期/D 編製者/E 傳票總金額 + 動態 C1..CN(全 position 升冪)。
        Assert.Equal("傳票號碼", sheet.Cell("B11").GetString());
        Assert.Equal("C1", sheet.Cell("F11").GetString());
        Assert.Equal("C2", sheet.Cell("G11").GetString());
        Assert.Equal("C3", sheet.Cell("H11").GetString()); // 全 position(含 0 命中的 C3)都建欄
        AssertSafeAmountColumn(sheet, 5);
        AssertSingleLine(sheet.Cell("E13"));

        // 資料自第 13 列(第 12 列為固定說明)。取一張命中傳票,核對其 Y 欄集 == matchedPositions。
        var hitDoc = (await DemoProjectPipeline.QueryStringListAsync(host, ctx.ProjectId,
            "SELECT g.document_number FROM result_filter_run r " +
            "JOIN target_gl_entry g ON g.entry_id = r.entry_id " +
            "WHERE g.document_number IS NOT NULL ORDER BY g.document_number LIMIT 1;"))[0]!;
        var docRow = FindRowByColumnValue(sheet, "B", hitDoc, 13);
        Assert.True(docRow > 0, "命中傳票應出現在 step4");

        var matched = (await DemoProjectPipeline.QueryStringListAsync(host, ctx.ProjectId,
                "SELECT DISTINCT r.scenario_position FROM result_filter_run r " +
                "JOIN target_gl_entry g ON g.entry_id = r.entry_id " +
                "WHERE g.document_number = @doc ORDER BY r.scenario_position;", ("@doc", hitDoc)))
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
    }

    [Fact]
    public async Task Step4AndStep41_SelectedC2_ExcludeC1OnlyVoucher_AndUseWholeVoucherDebitTotal()
    {
        using var host = new HandlerTestHost();
        var projectId = await SetupSelectedScenarioMatrixAsync(host);
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
        await using var stream = new MemoryStream();
        await BuildWriter(host).WriteAsync(stream, context, CancellationToken.None);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);

        // 等價分割負向：C1-only 傳票不得混入只選 C2 的 step4/4-1 母體。
        var vouchers = workbook.Worksheet(Step4Sheet);
        // 範本固定列舉 C1-C10 欄標；選取範圍只控制資料列的 Y，不刪範本文字。
        Assert.Equal("C1", vouchers.Cell("F11").GetString());
        Assert.Equal($"C{c2Position}", vouchers.Cell("G11").GetString());
        Assert.Equal(0, FindRowByColumnValue(vouchers, "B", "DOC-C1", 13));
        var c2VoucherRow = FindRowByColumnValue(vouchers, "B", "DOC-C2", 13);
        Assert.True(c2VoucherRow > 0, "只選 C2 時 DOC-C2 應列入 step4。");
        Assert.Equal(
            new[] { "DOC-C2" },
            ReadColumnFrom(vouchers, "B", 13));

        // 規格 oracle：傳票總額是命中傳票的完整 GL 借方，不是只加總 C2 命中行。
        Assert.Equal(40d, vouchers.Cell(c2VoucherRow, 5).GetDouble());
        Assert.Equal("Y", vouchers.Cell(c2VoucherRow, 7).GetString());

        var details = workbook.Worksheet(Step41Sheet);
        Assert.Equal(0, FindRowByColumnValue(details, "A", "DOC-C1", 6));
        var c2Rows = Enumerable.Range(6, Math.Max(0, details.LastRowUsed()!.RowNumber() - 5))
            .Where(row => details.Cell(row, 1).GetString() == "DOC-C2")
            .ToList();
        Assert.Equal(3, c2Rows.Count); // 命中傳票的三條 GL 行要完整列出。
        Assert.Equal("C1_Tag", details.Cell("Q5").GetString());
        Assert.Equal("C2_Tag", details.Cell("R5").GetString());
        Assert.Equal(1, c2Rows.Count(row => details.Cell(row, 18).GetString() == "Y"));
    }

    [Fact]
    public async Task Step4_ZeroHitSelection_EmitsProtectedFixedHeaderWithZeroVoucherRows()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", MatrixScenarioPayload());
        var context = (await CurrentContextForAsync(host, ctx.ProjectId)) with
        {
            ScenarioPositions = [3]
        };

        await using var stream = new MemoryStream();
        await BuildWriter(host).WriteAsync(stream, context, CancellationToken.None);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(Step4Sheet);

        Assert.Equal(
            Enumerable.Range(1, 10).Select(position => $"C{position}"),
            Enumerable.Range(6, 10).Select(column => sheet.Cell(11, column).GetString()));
        Assert.Empty(ReadColumnFrom(sheet, "B", 13));
        Assert.True(sheet.Protection.IsProtected);
        Assert.All(
            sheet.Range("P13:U13").Cells(),
            cell => Assert.True(cell.IsEmpty()));
    }

    // ================= Task 4:step4-1 行層明細(動態欄只 rowHitCount>0 的 position)=================

    [Fact]
    public async Task Step41_RowMatrix_DynamicColumnsOnlyRowHitPositions_YMatchesMatchedPositions()
    {
        using var host = new HandlerTestHost();
        var ctx = await DemoProjectPipeline.SetupAsync(host);
        await host.DispatchAsync("filter.commit", MatrixScenarioPayload());

        var context = await CurrentContextForAsync(host, ctx.ProjectId);
        var plan = await CurrentPlanForAsync(host, context);
        await using var stream = new MemoryStream();
        await ((IWorkpaperPlanWriter)BuildWriter(host)).WriteAsync(
            stream,
            context,
            plan,
            CancellationToken.None);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(Step41Sheet);

        var expectedHeaders = plan.Step41Columns.Select(column => column.Header)
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
                "SELECT scenario_position FROM result_filter_run " +
                "GROUP BY scenario_position HAVING COUNT(*) > 0 ORDER BY scenario_position;"))
            .Select(s => int.Parse(s!)).ToList();
        Assert.True(rowHitPositions.Count > 0, "demo 應有行層命中情境");
        Assert.Equal(rowHitPositions, plan.RowHitScenarioPositions);

        // 取一個命中行(entry_id),核對其在 step4-1 對應列的 Y 欄集 == matchedPositions。
        var hitEntryId = await DemoProjectPipeline.QueryScalarAsync(host, ctx.ProjectId,
            "SELECT MIN(entry_id) FROM result_filter_run;");
        var hitDoc = (await DemoProjectPipeline.QueryStringListAsync(host, ctx.ProjectId,
            "SELECT document_number FROM target_gl_entry WHERE entry_id=@id;", ("@id", hitEntryId)))[0]!;
        var hitLine = (await DemoProjectPipeline.QueryStringListAsync(host, ctx.ProjectId,
            "SELECT line_item FROM target_gl_entry WHERE entry_id=@id;", ("@id", hitEntryId)))[0];

        var matched = (await DemoProjectPipeline.QueryStringListAsync(host, ctx.ProjectId,
                "SELECT scenario_position FROM result_filter_run WHERE entry_id=@id ORDER BY scenario_position;",
                ("@id", hitEntryId)))
            .Select(s => int.Parse(s!)).ToHashSet();
        Assert.NotEmpty(matched);

        var documentColumn = plan.Step41Columns
            .Select((column, index) => (column, index))
            .Single(item => string.Equals(
                item.column.Header,
                "傳票號碼_JE",
                StringComparison.Ordinal))
            .index + 1;
        var lineColumn = plan.Step41Columns
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
            var tagCol = plan.Step41Columns.Count + index + 1;
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
    public async Task Step41_FinalSchema_UsesLatePageRawValuesSignedAmountAndFullDataWidths()
    {
        const int rowCount = 205;
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
                    "ROW_TOKEN_JE",
                    "LATE_NOCAP_JE",
                    "LATE_CAP_JE",
                    "時間_JE");
                for (var index = 1; index <= rowCount; index++)
                {
                    gl.AddRow(
                        $"DOC-{index:D4}",
                        "01",
                        "2025-03-05",
                        "2025-03-06",
                        "1101",
                        "現金",
                        "命中",
                        index == rowCount ? 123.4567m : 1m,
                        index == rowCount ? 0 : 1,
                        $"SEQ-{index:D4}",
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
                                rules = new[]
                                {
                                    new
                                    {
                                        join = "and",
                                        type = "customKeywords",
                                        keywords = "命中"
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
        var stats = await ((IWorkpaperPlanWriter)BuildWriter(host)).WriteAsync(
            stream,
            context,
            plan,
            CancellationToken.None);

        var step41 = Assert.Single(
            stats.SheetStats,
            stat => string.Equals(stat.SheetName, Step41Sheet, StringComparison.Ordinal));
        Assert.Equal(rowCount, step41.RowsWritten);
        var headers = plan.Step41Columns.Select(column => column.Header)
            .Concat(["C1_TAG"])
            .ToArray();
        var tokenColumn = Array.IndexOf(headers, "ROW_TOKEN_JE") + 1;
        var noCapColumn = Array.IndexOf(headers, "LATE_NOCAP_JE") + 1;
        var capColumn = Array.IndexOf(headers, "LATE_CAP_JE") + 1;
        var timeColumn = Array.IndexOf(headers, "時間_JE") + 1;
        var amountColumn = Array.IndexOf(headers, "傳票金額_JE") + 1;
        var tagColumn = headers.Length;
        Assert.All(
            new[] { tokenColumn, noCapColumn, capColumn, timeColumn, amountColumn },
            column => Assert.True(column > 0));

        stream.Position = 0;
        using (var workbook = new XLWorkbook(stream))
        {
            var sheet = workbook.Worksheet(Step41Sheet);
            Assert.Equal(
                headers,
                Enumerable.Range(1, headers.Length)
                    .Select(column => sheet.Cell(5, column).GetString())
                    .ToArray());
            Assert.Equal("SEQ-0001", sheet.Cell(6, tokenColumn).GetString());
            Assert.Equal($"SEQ-{rowCount:D4}", sheet.Cell(5 + rowCount, tokenColumn).GetString());
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
        var sheetRef = workbookPart.Workbook.Sheets!.Elements<Sheet>()
            .Single(sheet => string.Equals(
                sheet.Name?.Value,
                Step41Sheet,
                StringComparison.Ordinal));
        var worksheet = Assert.IsType<WorksheetPart>(
                workbookPart.GetPartById(sheetRef.Id!.Value!))
            .Worksheet;
        var widths = Assert.Single(worksheet.Elements<Columns>())
            .Elements<Column>()
            .ToDictionary(column => checked((int)column.Min!.Value));
        Assert.Equal(71d, widths[noCapColumn].Width!.Value);
        Assert.Equal(255d, widths[capColumn].Width!.Value);
        Assert.Equal(12d, widths[timeColumn].Width!.Value);
        Assert.All(widths.Values, column => Assert.True(column.BestFit?.Value));
    }

}
