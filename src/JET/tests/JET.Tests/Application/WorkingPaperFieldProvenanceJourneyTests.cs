using System.Text.Json;
using ClosedXML.Excel;
using JET.Domain;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Application;

/// <summary>
/// 欄位的來歷記成資料，底稿「自動化工具-檔案欄位資訊」與 step4-1 不從欄名結尾與說明文字推斷。
/// 前三個測試用合成資料確認下面三個錯誤不會發生：
/// 來源欄撞到 JET 欄名時產生底稿失敗、沒有配對但欄名以 _JE 結尾的來源欄被當成已配對、
/// 傳票核准日選「與總帳日期相同」時 step4-1 整欄空白。
/// </summary>
public sealed class WorkingPaperFieldProvenanceJourneyTests
{
    private const string Step41SheetName = WorkingPaperSheetCatalog.Step41;

    // 要防止的錯誤：匯出底稿時 step4-1 的欄位名稱「傳票金額_JE」重複，整份底稿產生失敗。
    [Theory]
    [InlineData("sqlite")]
    public async Task SourceColumnNamedLikeJetField_YieldsNameInsteadOfFailingExport(string provider)
    {
        var builder = new InlineGlWorkbookBuilder()
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "傳票金額_JE", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "合成現金", "合成借方", 100, 1)
            .AddRow("JV-001", "2025-03-05", "4101", "合成收入", "合成貸方", 100, 0);
        var mapping = builder.BuildFlagModeMapping();
        mapping["amount"] = "傳票金額_JE";

        await ExportAsync(provider, builder, Commit(mapping), workbook =>
        {
            var fieldInfo = ReadGlFieldInfo(workbook);
            var yielded = Assert.Single(fieldInfo, row => row.Before == "傳票金額_JE（來源）");
            Assert.Equal(string.Empty, yielded.After);
            var generated = Assert.Single(fieldInfo, row => row.After == "傳票金額_JE");
            Assert.StartsWith("【傳票金額_JE】由系統產生", generated.Before, StringComparison.Ordinal);

            var step41 = ReadStep41(workbook);
            Assert.Single(step41.Headers, header => header == "傳票金額_JE");
            Assert.DoesNotContain("傳票金額_JE（來源）", step41.Headers);
            Assert.Equal(2, step41.Rows.Count);
        });
    }

    // 要防止的錯誤：欄位資訊把 COST_CENTER_JE 寫成配對後欄位，step4-1 多出這一欄。
    [Theory]
    [InlineData("sqlite")]
    public async Task UnmappedSourceColumnEndingWithJe_IsNotTreatedAsJetField(string provider)
    {
        var builder = new InlineGlWorkbookBuilder()
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標", "COST_CENTER_JE")
            .AddRow("JV-001", "2025-03-05", "1101", "合成現金", "合成借方", 100, 1, "CC-1")
            .AddRow("JV-001", "2025-03-05", "4101", "合成收入", "合成貸方", 100, 0, "CC-2");

        await ExportAsync(provider, builder, Commit(builder.BuildFlagModeMapping()), workbook =>
        {
            var fieldInfo = ReadGlFieldInfo(workbook);
            var unmapped = Assert.Single(fieldInfo, row => row.Before == "COST_CENTER_JE");
            Assert.Equal(string.Empty, unmapped.After);

            var step41 = ReadStep41(workbook);
            Assert.DoesNotContain("COST_CENTER_JE", step41.Headers);
        });
    }

    // 要防止的錯誤：step4-1 的「傳票核准日_JE」整欄空白。
    [Theory]
    [InlineData("sqlite")]
    public async Task ApprovalDateSameAsGlDate_Step41WritesTheGlDate(string provider)
    {
        var builder = new InlineGlWorkbookBuilder()
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", new DateTime(2025, 3, 5), "1101", "合成現金", "合成借方", 100, 1)
            .AddRow("JV-001", new DateTime(2025, 3, 5), "4101", "合成收入", "合成貸方", 100, 0)
            .AddRow("JV-002", new DateTime(2025, 4, 9), "1101", "合成現金", "合成借方", 50, 1)
            .AddRow("JV-002", new DateTime(2025, 4, 9), "4101", "合成收入", "合成貸方", 50, 0);
        var commit = Commit(builder.BuildFlagModeMapping());
        commit["approvalDateMode"] = "sameAsGlDate";

        await ExportAsync(provider, builder, commit, workbook =>
        {
            var fieldInfo = ReadGlFieldInfo(workbook);
            var approval = Assert.Single(fieldInfo, row => row.After == "傳票核准日_JE");
            Assert.Equal("【傳票核准日_JE】由系統產生 : 同【總帳日期_JE】來源", approval.Before);

            var step41 = ReadStep41(workbook);
            var approvalIndex = step41.Headers.IndexOf("傳票核准日_JE");
            var glDateIndex = step41.Headers.IndexOf("總帳日期_JE");
            Assert.True(approvalIndex >= 0);
            Assert.True(glDateIndex >= 0);
            Assert.Equal(4, step41.Rows.Count);
            Assert.All(step41.Rows, row =>
            {
                Assert.False(string.IsNullOrEmpty(row[glDateIndex]));
                Assert.Equal(row[glDateIndex], row[approvalIndex]);
            });
        });
    }

    // 勾了往下填補的欄位，step4-1 寫填補後的值。
    [Theory]
    [InlineData("sqlite")]
    public async Task FillDownField_Step41WritesTheFilledValue(string provider)
    {
        var builder = new InlineGlWorkbookBuilder()
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標")
            .AddRow("JV-001", "2025-03-05", "1101", "合成現金", "合成借方", 100, 1)
            .AddRow(null, "2025-03-05", "4101", "合成收入", "合成貸方", 100, 0);
        var commit = Commit(builder.BuildFlagModeMapping());
        commit["fillDownFields"] = new[] { "voucherNumber" };

        await ExportAsync(provider, builder, commit, workbook =>
        {
            var step41 = ReadStep41(workbook);
            var voucherIndex = step41.Headers.IndexOf("傳票號碼_JE");
            Assert.Equal(2, step41.Rows.Count);
            Assert.All(step41.Rows, row => Assert.Equal("JV-001", row[voucherIndex]));
        });
    }

    // 攸關資料元素照舊版加 _JE 並標成已配對；配對了過帳狀態時多一欄過帳碼_JE。
    [Theory]
    [InlineData("sqlite")]
    public async Task RdeAndPostingStatus_AppearWithLegacySuffixes(string provider)
    {
        var builder = new InlineGlWorkbookBuilder()
            .WithColumns("傳票號碼", "傳票日期", "科目代號", "科目名稱", "摘要", "金額", "借方旗標", "成本中心", "過帳")
            .AddRow("JV-001", "2025-03-05", "1101", "合成現金", "合成借方", 100, 1, "CC-1", "Y")
            .AddRow("JV-001", "2025-03-05", "4101", "合成收入", "合成貸方", 100, 0, "CC-2", "Y");
        var mapping = builder.BuildFlagModeMapping();
        mapping["postingStatus"] = "過帳";
        var commit = Commit(mapping);
        commit["postingStatusPolicy"] = new { acceptedValues = new[] { "Y" }, includeBlank = false };
        commit["rdeFields"] = new object[] { new { sourceColumn = "成本中心", label = "成本中心", valueType = "text" } };

        await ExportAsync(
            provider,
            builder,
            commit,
            workbook =>
            {
                var fieldInfo = ReadGlFieldInfo(workbook);
                var rde = Assert.Single(fieldInfo, row => row.After == "成本中心_JE");
                Assert.Equal("【成本中心】", rde.Before);
                var postingCode = Assert.Single(fieldInfo, row => row.After == "過帳碼_JE");
                Assert.Equal("【過帳】過帳判斷值「Y」", postingCode.Before);
                var postingSource = Assert.Single(fieldInfo, row => row.Before == "過帳");
                Assert.Equal(string.Empty, postingSource.After);

                var step41 = ReadStep41(workbook);
                var postingIndex = step41.Headers.IndexOf("過帳碼_JE");
                var rdeIndex = step41.Headers.IndexOf("成本中心_JE");
                Assert.True(postingIndex >= 0);
                Assert.True(rdeIndex >= 0);
                Assert.DoesNotContain("成本中心", step41.Headers);
                Assert.Equal(2, step41.Rows.Count);
                Assert.All(step41.Rows, row => Assert.Equal("Y", row[postingIndex]));
                Assert.Equal(new[] { "CC-1", "CC-2" }, step41.Rows.Select(row => row[rdeIndex]).ToArray());
            },
            committed =>
            {
                var fieldId = committed.GetProperty("rdeFields")[0].GetProperty("fieldId").GetString();
                return JsonSerializer.Serialize(new
                {
                    scenarios = new object[]
                    {
                        new
                        {
                            name = "成本中心有值",
                            rationale = "合成案件",
                            groups = new object[]
                            {
                                new
                                {
                                    join = "AND",
                                    conditions = new object[]
                                    {
                                        new { join = "AND", type = "typed", fieldId, @operator = "isNotBlank" }
                                    }
                                }
                            }
                        }
                    }
                });
            });
    }

    private static Dictionary<string, object?> Commit(Dictionary<string, string> mapping) => new(StringComparer.Ordinal)
    {
        ["mapping"] = mapping,
        ["amountMode"] = "flag",
        ["manualAutoPolicy"] = new { manualValues = new[] { "1" }, automaticValues = new[] { "0" } }
    };

    private static async Task ExportAsync(
        string provider,
        InlineGlWorkbookBuilder builder,
        Dictionary<string, object?> commit,
        Action<XLWorkbook> assert,
        Func<JsonElement, string>? scenarios = null)
    {
        using var host = new HandlerTestHost();
        var created = await host.DispatchAsync("project.create", JsonSerializer.Serialize(new
        {
            projectCode = "FIELD-ORIGIN-001",
            clientName = "合成欄位來歷案件",
            operatorId = "tester",
            periodStart = "2025-01-01",
            periodEnd = "2025-12-31",
            databaseProvider = provider
        }));
        var projectId = created.GetProperty("projectId").GetString()!;
        var workbookPath = builder.WriteWorkbook();
        try
        {
            await host.DispatchAsync("import.gl.fromFile", JsonSerializer.Serialize(new
            {
                filePath = workbookPath,
                fileName = "合成總帳.xlsx"
            }));
        }
        finally
        {
            TestWorkbookBuilder.Delete(workbookPath);
        }

        var committed = await host.DispatchAsync("mapping.commit.gl", JsonSerializer.Serialize(commit));
        var validation = await host.DispatchAsync("validate.run");
        var prescreen = await host.DispatchAsync("prescreen.run");
        var saved = await host.DispatchAsync("filter.commit", scenarios?.Invoke(committed) ?? """
            {
              "scenarios": [
                {
                  "name": "借方分錄",
                  "rationale": "合成案件",
                  "groups": [
                    { "join": "AND", "conditions": [ { "join": "AND", "type": "drCrOnly", "drCr": "debit" } ] }
                  ]
                }
              ]
            }
            """);
        var response = await host.DispatchAsync("export.workingPaperStream", JsonSerializer.Serialize(new
        {
            validationResultId = validation.GetProperty("resultRef").GetProperty("resultId").GetString(),
            prescreenResultId = prescreen.GetProperty("resultRef").GetProperty("resultId").GetString(),
            scenarioRevision = saved.GetProperty("resultRef").GetProperty("revision").GetString(),
            scenarioPositions = new[] { 1 }
        }));
        var path = Path.Combine(
            host.ProjectsRoot,
            projectId,
            response.GetProperty("publishedReport").GetProperty("fileName").GetString()!);

        using var workbook = new XLWorkbook(path);
        assert(workbook);
    }

    private sealed record FieldInfoLine(string Before, string After);

    /// <summary>沒有匯入 TB 時，GL 的標題在 A6，表頭在第 7 列，資料從第 8 列開始到第一個空白列。</summary>
    private static IReadOnlyList<FieldInfoLine> ReadGlFieldInfo(XLWorkbook workbook)
    {
        var sheet = workbook.Worksheet(WorkingPaperSheetCatalog.FieldInfo);
        Assert.Equal("GL檔案配對前後欄位對照表", sheet.Cell("A6").GetString());
        var rows = new List<FieldInfoLine>();
        for (var rowNumber = 8; rowNumber <= sheet.LastRowUsed()!.RowNumber(); rowNumber++)
        {
            var before = sheet.Cell(rowNumber, 1).GetString();
            if (string.IsNullOrEmpty(before))
            {
                break;
            }

            rows.Add(new FieldInfoLine(before, sheet.Cell(rowNumber, 5).GetString()));
        }

        return rows;
    }

    private sealed record Step41Sheet(List<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

    /// <summary>step4-1 的表頭在第 5 列，資料從第 6 列開始。</summary>
    private static Step41Sheet ReadStep41(XLWorkbook workbook)
    {
        var sheet = workbook.Worksheet(Step41SheetName);
        var lastColumn = sheet.Row(5).LastCellUsed()!.Address.ColumnNumber;
        var headers = Enumerable.Range(1, lastColumn)
            .Select(column => sheet.Cell(5, column).GetString())
            .ToList();
        var rows = new List<IReadOnlyList<string>>();
        for (var rowNumber = 6; rowNumber <= sheet.LastRowUsed()!.RowNumber(); rowNumber++)
        {
            var cells = Enumerable.Range(1, lastColumn)
                .Select(column => sheet.Cell(rowNumber, column).GetFormattedString())
                .ToArray();
            if (cells.All(string.IsNullOrEmpty))
            {
                break;
            }

            rows.Add(cells);
        }

        return new Step41Sheet(headers, rows);
    }
}
