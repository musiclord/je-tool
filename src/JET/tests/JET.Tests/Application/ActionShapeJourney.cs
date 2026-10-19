using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using JET.Application;
using JET.Domain;
using JET.Infrastructure;
using JET.Tests.Infrastructure;

namespace JET.Tests.Application;

/// <summary>
/// 照審計員的順序，用正式的 dispatcher 走一遍合成案件，記下每個 action 收到的資料與回應的形狀。
/// 送出的資料照前端畫面實際組出來的格式寫（各 action 旁註明前端的出處）；前端測試再用記下的形狀核對自己送出的資料，
/// 兩邊對不上就失敗，所以這裡的格式寫錯時會被前端測試抓到。
/// 合成資料刻意放進幾種有問題的列，匯出後也回頭修改上游資料，讓差異清單、警告與條件失效這些畫面會遇到的回應都有紀錄。
/// </summary>
internal static class ActionShapeJourney
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    /// <summary>欄位名稱是資料的物件。路徑格式是「action request|response」加上 .欄位、[] 表示陣列元素。</summary>
    internal static readonly IReadOnlySet<string> MapPaths = new HashSet<string>(StringComparer.Ordinal)
    {
        // 攸關資料元素的值以後端產生的欄位識別（rde.…）為鍵。
        "query.filterHitsPage response.rows[].customValues",
        "query.filterVoucherRowsPage response.rows[].customValues",
        "query.infSamplePage response.rows[].customValues",
    };

    /// <summary>
    /// 正式後端會回、但這個流程穩定走不到的型別。每一項寫明哪段程式會產生它、為什麼走不到；產生契約時加進那一層。
    /// 流程之後走到了，宣告會被判定多餘而失敗，要從這裡拿掉。前端測試刻意假造正式後端根本不會回的資料時，
    /// 改用 action-contract.cjs 的 unchecked，不列在這裡。
    /// </summary>
    internal static readonly IReadOnlyList<(string Path, string Kind)> UnreachedKinds =
    [
        // ReportExportSupport.ReadArtifactCatalogAfterPublicationAsync：檔案發布之後讀報告清單失敗或超過 3 秒，回 null 與警告。
        // 發布與讀清單之間沒有可以介入的時間點；這段流程也把清單更新期限固定成不會到期（見 RecordAsync），所以走不到。
        ("export.validationArtifacts response.reportArtifacts", "null"),
        ("export.validationArtifacts response.reportArtifactWarning", "string"),
        ("export.prescreenReport response.reportArtifacts", "null"),
        ("export.prescreenReport response.reportArtifactWarning", "string"),
        ("export.criteriaSelectionReport response.reportArtifacts", "null"),
        ("export.criteriaSelectionReport response.reportArtifactWarning", "string"),
        ("export.workpaperStream response.reportArtifacts", "null"),
        ("export.workpaperStream response.reportArtifactWarning", "string"),
        // ImportAccountMappingHandler：匯入完成後計算兩種差異數失敗才回 null，匯入本身已完成；失敗無法穩定重現。
        ("import.accountMapping.fromFile response.mappingOnlyCount", "null"),
        ("import.accountMapping.fromFile response.unmappedCount", "null"),
        // WorksheetInspection.RowCountEstimate：Access 資料表與沒有範圍資訊的工作表沒有估計值；合成資料做不出這種檔案。
        ("import.inspectFile response.worksheets[].rowCountEstimate", "null"),
        // MappingValueProfileHandler：資料庫實作沒有來源值核對時回 null（sourceValueCheckStatus 是 unsupportedProvider）；
        // SQLite、DuckDB 與 SQL Server 都已實作（SQL Server 自 2026-10-19 起），合成流程做不出這個值。
        ("mapping.valueProfile response.missingComparisonValues", "null"),
    ];

    private const string Scope = "auditPeriod";

    internal static async Task<ActionShapeRecorder> RecordAsync(string provider)
    {
        // 報告清單更新的 3 秒期限是給畫面的時間上限，不是資料形狀的一部分。這段流程固定走到清單更新完成，
        // 記到的形狀才不會隨機器快慢改變（2026-10-19 Public 曾因 DuckDB 的清單更新超過 3 秒而失敗）。
        // 只影響這個非同步流程，同時執行的其他測試仍用系統時間。報告清單檔被其他程式占用的情況照常走到，見 accountTaxonomy.save。
        ReportExportSupport.CatalogRefreshClock.Value = NeverExpiringClock.Instance;
        var recorder = new ActionShapeRecorder(MapPaths);
        var shell = new JourneyHostShell();
        var temporaryFiles = new List<string>();
        using var host = new HandlerTestHost(hostShell: shell);
        try
        {
            await WalkAsync();
        }
        finally
        {
            foreach (var path in temporaryFiles)
            {
                TestWorkbookBuilder.Delete(path);
            }
        }

        foreach (var (path, kind) in UnreachedKinds)
        {
            recorder.Declare(path, kind);
        }

        return recorder;

        async Task<JsonElement> Send(string action, object? payload = null)
        {
            var json = JsonSerializer.Serialize(payload ?? new { }, Wire);
            var response = await host.DispatchAsync(action, json);
            using var request = JsonDocument.Parse(json);
            recorder.Record(action, request.RootElement, response);
            return response;
        }

        async Task<JsonElement> Demo(string action)
        {
            var response = await DemoProjectPipeline.DispatchDemoFixtureAsync(action);
            using var request = JsonDocument.Parse("{}");
            recorder.Record(action, request.RootElement, response);
            return response;
        }

        string Track(string path)
        {
            temporaryFiles.Add(path);
            return path;
        }

        async Task WalkAsync()
        {
            // 開啟 JET：app.js 依序送出連線檢查、資料庫設定、目前使用者，ui-core.js 讀本機與共用案件清單。
            await Send("system.ping");
            await Send("system.databaseInfo");
            await Send("system.whoAmI");
            await Send("project.listLocal");
            await Send("project.list");

            // 第一步建案：create-step.js 的建案表單。期末財報準備日先填和查核期間不相符的年份，後端回警告；
            // 之後修改案件資料時清空、再填錯一次，最後改回示範值。
            var demo = await Demo("project.loadDemo");
            var project = demo.GetProperty("project");
            var entityName = project.GetProperty("entityName").GetString();
            var projectCode = project.GetProperty("projectCode").GetString();
            var lastPeriodStart = project.GetProperty("lastPeriodStart").GetString();
            const string mismatchedLastPeriodStart = "2020-01-01";
            // create-step.js 允許先不填期末財報準備日（送 null），之後在案件資料補上；審計員先建了這樣一個案件。
            await Send("project.create", new
            {
                caseName = "合成未填準備日",
                projectCode,
                entityName,
                periodStart = project.GetProperty("periodStart").GetString(),
                periodEnd = project.GetProperty("periodEnd").GetString(),
                lastPeriodStart = (string?)null,
                databaseProvider = provider,
            });
            var created = await Send("project.create", new
            {
                caseName = "合成形狀紀錄",
                projectCode,
                entityName,
                periodStart = project.GetProperty("periodStart").GetString(),
                periodEnd = project.GetProperty("periodEnd").GetString(),
                lastPeriodStart = mismatchedLastPeriodStart,
                databaseProvider = provider,
            });
            var projectId = created.GetProperty("projectId").GetString()!;
            // 每個新案件的抽樣種子是隨機的；合成總帳有總帳日期空白的列，抽到與沒抽到時抽樣明細的形狀不同。
            // 這裡把種子固定下來，兩種資料庫才抽到同一組列，每次產生的契約也相同。
            await FixSampleSeedAsync(projectId);
            // ui-core.js 開案：知道資料庫種類時一併送出。
            await Send("project.load", new { projectId });
            await Send("project.load", new { projectId, databaseProvider = provider });
            await Send("project.listLocal");
            // 案件清單的「同步線上案件」：沒有線上資料庫時也列出本機案件（ProjectListHandler）。
            await Send("project.list");
            foreach (var value in new[] { null, mismatchedLastPeriodStart, lastPeriodStart })
            {
                await Send("project.update", new { entityName, projectCode, lastPeriodStart = value });
            }

            await Send("project.saveProgress", new { currentStep = 1 });
            await Send("project.heartbeat");

            // 第二步匯入：import-step.js 先選檔、讀工作表、預覽十列，再整批匯入。GL 用示範檔加工的合成檔（見 SyntheticGl）。
            var glExport = await Demo("demo.exportGlFile");
            var tbExport = await Demo("demo.exportTbFile");
            var glPath = Track(SyntheticGl(glExport.GetProperty("filePath").GetString()!));
            var tbPath = tbExport.GetProperty("filePath").GetString()!;
            shell.NextFiles = [];
            await Send("host.selectFiles", new { title = "選擇 GL 來源檔（可多選）", extensions = SourceExtensions });
            shell.NextFiles = [glPath];
            await Send("host.selectFiles", new { title = "選擇 GL 來源檔（可多選）", extensions = SourceExtensions });
            await ImportAsync("import.gl.fromFile", glPath, "合成總帳.xlsx");
            // 審計員先拿到文字檔版本的總帳，在待匯入清單指定編碼與分隔符整批取代；之後再換回 Excel 版。
            var glCsvPath = Track(WorkbookCsv(glPath, [], _ => []));
            await Send("import.gl.fromFile", new
            {
                mode = "replace",
                sources = new[] { new { filePath = glCsvPath, fileName = "合成總帳.csv", encoding = "utf-8", delimiter = "," } },
            });
            await ImportAsync("import.gl.fromFile", glPath, "合成總帳.xlsx");
            await ImportAsync("import.tb.fromFile", tbPath, tbExport.GetProperty("fileName").GetString()!);

            // 文字檔：審計員在待匯入清單指定編碼與分隔符（import-step.js）。同一份文字檔再加入一次時後端提醒可能重複匯入，
            // 審計員再整批取代回來。
            var tbMapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                demo.GetProperty("tb").GetProperty("mapping").GetRawText())!;
            var tbCsvPath = Track(TrialBalanceCsv(tbPath, tbMapping[TbMappingKeys.DebitAmt], tbMapping[TbMappingKeys.CreditAmt]));
            // 審計員誤選一份報表格式的文字檔（整份只有一欄）：檢查與預覽都附上提醒，審計員沒有加入它。
            var reportLayoutPath = Track(Path.Combine(Path.GetTempPath(), $"jet-shape-report-{Guid.NewGuid():N}.txt"));
            File.WriteAllText(reportLayoutPath, "合成報表\n第一行\n第二行\n", new UTF8Encoding(false));
            await Send("import.inspectFile", new { filePath = reportLayoutPath });
            await Send("import.previewFile", new { filePath = reportLayoutPath, limit = 10, encoding = "utf-8" });
            await Send("import.inspectFile", new { filePath = tbCsvPath });
            await Send("import.previewFile", new { filePath = tbCsvPath, limit = 10, encoding = "utf-8", delimiter = "," });
            var csvSource = new { filePath = tbCsvPath, fileName = "合成試算表.csv", encoding = "utf-8", delimiter = "," };
            foreach (var mode in new[] { "replace", "append", "replace" })
            {
                await Send("import.tb.fromFile", new { mode, sources = new[] { csvSource } });
            }

            // 右側資料預覽與第三步的原始資料預覽：data-preview.js、mapping-step.js。
            foreach (var dataset in new[] { "pbcGl", "pbcTb" })
            {
                // 2026-10-16 欄位配對改用有界的 200 筆分頁，offset 是實際送出的頁起點。
                await Send("query.dataPreview", new { dataset, limit = 200, offset = 0 });
                // 換到下一頁：總筆數與統計只在第一頁計算，續頁回 null（使用者 2026-10-16 要求處理每頁重算總筆數）。
                await Send("query.dataPreview", new { dataset, limit = 1, offset = 1 });
            }

            // 第三步欄位配對：mapping-step.js 的值清單與確認配對。
            var loaded = await Send("project.load", new { projectId });
            var profileColumn = loaded.GetProperty("importState").GetProperty("gl").GetProperty("columns")[0].GetString();
            var profile = await Send("mapping.valueProfile", new
            {
                dataset = "gl", sourceColumn = profileColumn, limit = 50, comparisonValues = Array.Empty<string>(),
                checkSourceValues = false,
            });
            await Send("mapping.valueProfile", new
            {
                dataset = "gl", sourceColumn = profileColumn, limit = 50, comparisonValues = new[] { "1", "0" },
                checkSourceValues = true,
            });
            await Send("mapping.valueProfile", new
            {
                dataset = "gl", sourceColumn = profileColumn, comparisonOnly = true,
                // mapping-step.js 只濾掉空字串；審計員輸入只有空白的代碼時照樣送出，後端回的比對鍵是 null。
                comparisonValues = profile.GetProperty("values").EnumerateArray()
                    .Select(value => value.GetProperty("value").GetString()).Append("新代碼").Append(" ").ToArray(),
            });
            var glMapping = JsonSerializer.Deserialize<Dictionary<string, string>>(
                demo.GetProperty("gl").GetProperty("mapping").GetRawText())!;
            // 合成總帳多一個核准人員欄（見 SyntheticGl），配對後抽樣明細與矩陣才有核准人員。
            var syntheticGlMapping = new Dictionary<string, string>(glMapping, StringComparer.Ordinal)
            {
                [GlMappingKeys.ApproveBy] = "合成核准人員",
            };
            var amountMode = demo.GetProperty("gl").GetProperty("amountMode").GetString()!;
            // 攸關資料元素：新欄位不帶 fieldId，由後端配置；再確認配對時沿用後端給的識別。
            var newRdeFields = new JsonArray
            {
                new JsonObject { ["sourceColumn"] = "合成部門", ["label"] = "合成部門", ["valueType"] = "text" },
                new JsonObject { ["sourceColumn"] = "合成金額欄", ["label"] = "合成金額欄", ["valueType"] = "money" },
            };
            // 傳票摘要先配到整欄空白的欄，後端回警告；審計員改回「摘要」欄再確認一次。
            var misMapped = new Dictionary<string, string>(syntheticGlMapping, StringComparer.Ordinal)
            {
                [GlMappingKeys.Description] = "合成空白欄",
            };
            var firstGlCommit = await Send("mapping.commit.gl", GlCommit(misMapped, amountMode, newRdeFields));
            var rdeFields = CommittedRdeFields(firstGlCommit);
            // 審計員先試借方、貸方金額分開的配對，不配傳票項次，另配分錄來源與過帳狀態（只認列已過帳，空白也算），
            // 人工代碼只列自動的一側、其餘算人工，空白不分類。重開案件看一次，再改回單一金額欄的配對。
            // mapping-step.js 有配過帳狀態時一併送 postingStatusPolicy；沒有配傳票核准日時 approvalDateMode 送 unmapped。
            var dualMapping = new[]
            {
                GlMappingKeys.DocNum, GlMappingKeys.PostDate, GlMappingKeys.AccNum, GlMappingKeys.AccName, GlMappingKeys.Description,
            }.ToDictionary(key => key, key => glMapping[key], StringComparer.Ordinal);
            dualMapping[GlMappingKeys.DebitAmount] = "合成借方金額";
            dualMapping[GlMappingKeys.CreditAmount] = "合成貸方金額";
            dualMapping[GlMappingKeys.JeSource] = "合成分錄來源";
            dualMapping[GlMappingKeys.PostingStatus] = "合成過帳狀態";
            var dualCommit = GlCommit(dualMapping, GlAmountModeNames.Dual, rdeFields, approvalDateMode: "unmapped");
            dualCommit["postingStatusPolicy"] = new JsonObject
            {
                ["acceptedValues"] = new JsonArray("已過帳"), ["includeBlank"] = true,
            };
            dualCommit["manualAutoPolicy"] = new JsonObject
            {
                ["manualValues"] = new JsonArray(), ["automaticValues"] = new JsonArray("0"),
                ["unlistedValueKind"] = ManualAutoValueKindNames.Manual, ["blankValueKind"] = ManualAutoValueKindNames.Unclassified,
            };
            await Send("mapping.commit.gl", dualCommit);
            await Send("project.load", new { projectId });
            var glCommit = await Send("mapping.commit.gl", GlCommit(syntheticGlMapping, amountMode, rdeFields));
            var moneyFieldId = glCommit.GetProperty("rdeFields").EnumerateArray()
                .Single(field => field.GetProperty("valueType").GetString() == "money")
                .GetProperty("fieldId").GetString()!;
            // 試算表同樣先把科目名稱配到整欄空白的欄，後端回警告，再改回正確的欄。
            var changeMode = demo.GetProperty("tb").GetProperty("changeMode").GetString()!;
            await Send("mapping.commit.tb", TbCommit(new Dictionary<string, string>(tbMapping, StringComparer.Ordinal)
            {
                [TbMappingKeys.AccName] = "合成空白欄",
            }, changeMode));
            // 變動金額另有三種給法：直接給年度變動、給期初與期末餘額、給期初與期末的借貸方（見 TrialBalanceCsv）。
            // 審計員依來源檔的欄位選一種；這裡三種都確認一次，最後改回示範資料的借方、貸方金額。
            foreach (var (mode, columns) in new (string, Dictionary<string, string>)[]
            {
                (TbChangeModeNames.Direct, new() { [TbMappingKeys.Amount] = "合成年度變動" }),
                (TbChangeModeNames.OpenClose, new()
                {
                    [TbMappingKeys.OpeningBalance] = "合成期初餘額", [TbMappingKeys.ClosingBalance] = "合成期末餘額",
                }),
                (TbChangeModeNames.OpenCloseBySide, new()
                {
                    [TbMappingKeys.OpeningDebit] = "合成期初借方", [TbMappingKeys.OpeningCredit] = "合成期初貸方",
                    [TbMappingKeys.ClosingDebit] = "合成期末借方", [TbMappingKeys.ClosingCredit] = "合成期末貸方",
                }),
            })
            {
                var modeMapping = new Dictionary<string, string>(columns, StringComparer.Ordinal)
                {
                    [TbMappingKeys.AccNum] = tbMapping[TbMappingKeys.AccNum], [TbMappingKeys.AccName] = tbMapping[TbMappingKeys.AccName],
                };
                await Send("mapping.commit.tb", TbCommit(modeMapping, mode));
            }

            await Send("mapping.commit.tb", TbCommit(tbMapping, changeMode));
            foreach (var dataset in new[] { "gl", "tb", "glExcluded", "dateDimension", "schemaOverview" })
            {
                await Send("query.dataPreview", new { dataset });
            }

            // 第二步的行事曆與授權編製人員：import-step.js。行事曆範本第一列是標題，欄名在第二列（同 Batch9MutationStateTests）。
            var holidayFile = Track(TestWorkbookBuilder.WriteWorkbook(sheet =>
            {
                sheet.Cell(1, 1).Value = "Synthetic calendar";
                sheet.Cell(2, 1).Value = "Date_of_Holiday";
                sheet.Cell(2, 2).Value = "Holiday_Name";
                sheet.Cell(2, 3).Value = "IS_Holiday";
                sheet.Cell(3, 1).Value = "2025-01-01";
                sheet.Cell(3, 2).Value = "合成假日";
                sheet.Cell(3, 3).Value = "Y";
            }));
            var makeupFile = Track(TestWorkbookBuilder.WriteWorkbook(sheet =>
            {
                sheet.Cell(1, 1).Value = "Synthetic calendar";
                sheet.Cell(2, 1).Value = "Date_of_MakeUpday";
                sheet.Cell(3, 1).Value = "2025-02-08";
            }));
            const string holidayTitle = "選擇假日檔（Date_of_Holiday、Holiday_Name、IS_Holiday）";
            shell.NextFile = null;
            await Send("host.selectFile", new { title = holidayTitle, extensions = new[] { ".xlsx" } });
            shell.NextFile = holidayFile;
            var holiday = await Send("host.selectFile", new { title = holidayTitle, extensions = new[] { ".xlsx" } });
            await Send("import.holiday.fromFile", new
            {
                filePath = holiday.GetProperty("filePath").GetString(), fileName = holiday.GetProperty("fileName").GetString(),
            });
            await Send("import.makeupDay.fromFile", new { filePath = makeupFile, fileName = Path.GetFileName(makeupFile) });
            await Send("calendar.setNonWorkingDays", new { days = new[] { 0, 6 } });

            var preparerFile = await Demo("demo.exportAuthorizedPreparerFile");
            var preparerPath = preparerFile.GetProperty("filePath").GetString()!;
            var preparerSheets = await Send("import.inspectFile", new { filePath = preparerPath });
            var preparerSheet = FirstSheet(preparerSheets);
            await Send("import.previewFile", new { filePath = preparerPath, sheetName = preparerSheet });
            var preparer = new
            {
                filePath = preparerPath, fileName = preparerFile.GetProperty("fileName").GetString(), sheetName = preparerSheet,
                sourceColumn = "姓名",
            };
            await Send("import.authorizedPreparer.fromFile", preparer);
            await Send("import.authorizedPreparer.clear");
            await Send("import.authorizedPreparer.fromFile", preparer);
            foreach (var dataset in new[] { "authorizedPreparers", "dateDimension" })
            {
                await Send("query.dataPreview", new { dataset });
            }

            // 第四步科目配對與驗證：validate-step.js、account-mapping-editor.js。科目配對檔匯入前，科目清單的分類都是空的。
            await Send("query.accountMappingPage", new { cursor = (string?)null, pageSize = 100, search = "" });
            var accountExport = await Demo("demo.exportAccountMappingFile");
            var accountPath = Track(SyntheticAccountMapping(accountExport.GetProperty("filePath").GetString()!));
            // 審計員先匯入一份欄名不是標準名稱的科目配對檔：後端依欄位順序對應並提醒，審計員改用正確的檔案再匯入。
            var renamedAccountPath = Track(SyntheticAccountMapping(accountExport.GetProperty("filePath").GetString()!, renameHeaders: true));
            await Send("import.accountMapping.fromFile", new { filePath = renamedAccountPath, fileName = "合成科目配對（欄名不同）.xlsx" });
            // 審計員改用文字檔匯入，其中一個科目名稱用引號包住、內含換行：匯入照常完成並附上提醒（使用者 2026-10-19 要求處理）。
            var accountCsvPath = Track(AccountMappingCsvWithLineBreak(accountPath));
            await Send("import.accountMapping.fromFile", new { filePath = accountCsvPath, fileName = "合成科目配對.csv" });
            await Send("import.accountMapping.fromFile", new { filePath = accountPath, fileName = "合成科目配對.xlsx" });
            await Send("query.dataPreview", new { dataset = "accountMappings" });
            var accounts = await Send("query.accountMappingPage", new { cursor = (string?)null, pageSize = 1, search = "" });
            await Send("query.accountMappingPage", new
            {
                cursor = accounts.GetProperty("nextCursor").GetString(), pageSize = 1, search = "",
            });
            await Send("query.accountMappingPage", new { cursor = (string?)null, pageSize = 100, search = "1" });
            // 第五步科目編號清單的選取樹（filter-values.js）：依分類讀，也可以不分類搜尋。
            await Send("query.accountMappingPage", new
            {
                categoryId = "builtin.cash", search = "", pageSize = 200, cursor = (string?)null,
            });
            await Send("query.accountMappingPage", new
            {
                categoryId = (string?)null, search = "1", pageSize = 200, cursor = (string?)null,
            });
            var firstAccountCode = accounts.GetProperty("rows")[0].GetProperty("accountCode").GetString();
            await Send("accountMapping.save", new { changes = new[] { new { accountCode = firstAccountCode, categoryId = "builtin.cash" } } });

            var taxonomyState = (await Send("project.load", new { projectId })).GetProperty("taxonomy");
            var taxonomy = TaxonomyPayload(taxonomyState);
            // validate-step.js 新增的分類帶 draft-N 暫時識別，放到上層底下時沿用上層的 semanticRole。
            var cashRole = taxonomyState.GetProperty("categories").EnumerateArray()
                .Single(category => category.GetProperty("categoryId").GetString() == "builtin.cash")
                .GetProperty("semanticRole").GetString();
            var categories = taxonomy["categories"]!.AsArray();
            categories.Add(new JsonObject
            {
                ["label"] = "合成下層分類",
                ["ordinal"] = categories.Count,
                ["semanticRole"] = cashRole,
                ["parentCategoryId"] = "builtin.cash",
                ["categoryId"] = "draft-1",
            });
            await Send("accountTaxonomy.save", taxonomy);

            var validation = await Send("validate.run");
            var validationRunId = validation.GetProperty("resultRef").GetProperty("runId").GetString()!;
            foreach (var kind in new[] { "unmapped", "mappingOnly" })
            {
                await CursorPagesAsync("query.accountMappingDifferencePage", new JsonObject { ["kind"] = kind });
            }

            await CursorPagesAsync("query.accountMappingBlankPage", new JsonObject());
            await PagedAsync("query.completenessDiffPage", new JsonObject(), "accountCode", "9999");
            await PagedAsync("query.docBalancePage", new JsonObject(), "documentNumber", "SHAPE");
            await PagedAsync("query.sourceQualityPage", new JsonObject(), "documentNumber", "SHAPE");
            foreach (var category in new[] { "nullAccount", "nullDocument", "nullDescription", "outOfRangeDate" })
            {
                await PagedAsync("query.nullRecordsPage", new JsonObject { ["category"] = category }, "documentNumber", "SHAPE");
            }

            await Send("export.validationArtifacts", new { runId = validationRunId });
            await Send("export.accountMappingTemplate", new { runId = validationRunId, onlyIfMissing = true });
            await Send("export.accountMappingTemplate", new { runId = validationRunId, onlyIfMissing = false });

            // 第四步預篩選：validate-step.js、export-step.js。各預篩選結果先讀一頁五十筆，展開後用共用的分頁表格。
            var prescreen = await Send("prescreen.run");
            var prescreenRunId = prescreen.GetProperty("resultRef").GetProperty("runId").GetString()!;
            foreach (var ruleKey in new[] { "blankDescription", "weekendPosting", "holidayPosting", "lowFrequencyAccount" })
            {
                await Send("query.prescreenPage", new { ruleKey, pageSize = 50 });
                await PagedAsync("query.prescreenPage", new JsonObject { ["ruleKey"] = ruleKey }, "documentNumber", "SHAPE");
            }

            await PagedAsync("query.infSamplePage", new JsonObject(), "documentNumber", "SHAPE");
            await Send("export.prescreenReport", new { runId = prescreenRunId });

            // 第五步進階條件篩選：filter-step.js 的預覽、命中傳票、儲存、矩陣與篩選條件報告。
            var amountDraft = Scenario("合成大額分錄", "合成：金額大於門檻。", new JsonArray
            {
                FieldValue("amount", "greaterThanOrEqual", value: "100000", amountBasis: "absolute"),
                FieldValue("description", "contains", values: ["調整"], join: "OR"),
            });
            await PreviewAsync(amountDraft);
            await VoucherPagesAsync(new JsonObject { ["populationScope"] = Scope, ["scenario"] = amountDraft.DeepClone() });
            await SaveAsync(amountDraft, null);
            var cashDraft = Scenario("合成借方現金", "合成：借方為現金的分錄。", new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "group", ["join"] = "AND", ["rules"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "drCrOnly", ["join"] = "AND", ["drCr"] = "debit" },
                        // ui-core.js newFilterRule('accountSide') 的預設值。
                        new JsonObject
                        {
                            ["type"] = "accountSide", ["join"] = "AND", ["categoryMode"] = "is",
                            ["categoryIds"] = new JsonArray("builtin.cash"), ["categorySelection"] = "subtree",
                        },
                    },
                },
            });
            await SaveAsync(cashDraft, null);
            // KCT 卡片：filter-step.js 在根層送 source:'kct'，editorOrigins 記下每條條件來自哪張卡，名稱與動機由系統產生。
            var kctDraft = Scenario("KCT F：特定摘要", "合成：特定摘要。", new JsonArray
            {
                FieldValue("description", "contains", values: ["迴轉", "調整"]),
            }, letters: ["F"], automatic: true);
            kctDraft["source"] = "kct";
            await PreviewAsync(kctDraft);
            await SaveAsync(kctDraft, null);
            // 舊版存下的 KCT 情境沒有命名來源；filter-step.js 預覽它時只帶根層的 source:'kct'。
            var legacyKct = (JsonObject)kctDraft.DeepClone();
            legacyKct.Remove("editorOrigins");
            await Send("filter.preview", new JsonObject { ["populationScope"] = Scope, ["scenario"] = legacyKct.DeepClone() });
            // 編輯這種舊版 KCT 情境後存回原位置：照樣只帶 source:'kct'，不補命名來源。
            await SaveAsync(legacyKct, 3);
            // 整張傳票條件與攸關資料元素條件（filter-step.js typedWireRule）。10-14 起「同一傳票」併進整張傳票條件，
            // 條件組一律逐列比對。
            var voucherDraft = new JsonObject
            {
                ["name"] = "合成整張傳票",
                ["rationale"] = "合成：攸關金額達門檻且同一傳票有貸方，或傳票借記現金。",
                ["groups"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["join"] = "OR", ["matchScope"] = "row", ["rules"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["join"] = "AND", ["type"] = "typed", ["fieldId"] = moneyFieldId,
                                ["operator"] = "greaterThanOrEqual", ["value"] = "10", ["amountBasis"] = "absolute",
                            },
                            new JsonObject
                            {
                                ["type"] = "voucher", ["join"] = "AND", ["quantifier"] = "any", ["rules"] = new JsonArray
                                {
                                    new JsonObject { ["type"] = "drCrOnly", ["join"] = "AND", ["drCr"] = "credit" },
                                },
                            },
                        },
                    },
                    new JsonObject
                    {
                        ["join"] = "OR", ["matchScope"] = "row", ["rules"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "voucher", ["join"] = "AND", ["quantifier"] = "any", ["rules"] = new JsonArray
                                {
                                    new JsonObject { ["type"] = "drCrOnly", ["join"] = "AND", ["drCr"] = "debit" },
                                    FieldValue("accNum", "in", values: ["1101"]),
                                },
                            },
                        },
                    },
                },
                ["editorOrigins"] = Origins(false, [null, null], [null]),
            };
            await PreviewAsync(voucherDraft);
            await SaveAsync(voucherDraft, null);
            // 「從範例開始」：applyTemplate 照原樣深拷貝範例的條件，條件本身沒有 join（ui-core.js FILTER_SCENARIO_TEMPLATES 的
            // 借現金、貸非現金）；名稱與動機算審計員給的。
            var templateDraft = Scenario("借現金、貸非現金", "借方為現金，而整張傳票沒有貸方現金的分錄。", new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "specialAccountCategoryPair", ["pairMode"] = "drNotCr", ["categorySelection"] = "subtree",
                    ["debitCategoryIds"] = new JsonArray("builtin.cash"), ["creditCategoryIds"] = new JsonArray("builtin.cash"),
                },
            });
            await PreviewAsync(templateDraft);
            await SaveAsync(templateDraft, null);
            // 舊版存下、現在仍可編輯的條件：預篩選結果、文字值清單（每行一個值，可含逗號）、舊式攸關資料元素條件的區間，
            // 以及條件括號裡的舊式條件。filter-step.js 編輯這些情境時照原樣送回（typedWireRule 只帶比較方式用到的值）。
            var olderRulesDraft = Scenario("合成舊式條件", "合成：舊版存下的條件。", new JsonArray
            {
                new JsonObject { ["type"] = "prescreen", ["join"] = "AND", ["prescreenKey"] = "blankDescription" },
                new JsonObject
                {
                    ["type"] = "textSet", ["join"] = "OR", ["field"] = "description", ["values"] = new JsonArray("調整,迴轉", "合成"),
                    ["mode"] = "contains", ["normalization"] = "removeAsciiSpaces",
                },
                new JsonObject
                {
                    ["join"] = "OR", ["type"] = "typed", ["fieldId"] = moneyFieldId, ["operator"] = "between",
                    ["from"] = "1", ["to"] = "100000", ["amountBasis"] = "absolute",
                },
                new JsonObject
                {
                    ["type"] = "group", ["join"] = "OR", ["rules"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["join"] = "AND", ["type"] = "typed", ["fieldId"] = moneyFieldId, ["operator"] = "between",
                            ["from"] = "1", ["to"] = "200", ["amountBasis"] = "signed",
                        },
                    },
                },
            });
            await PreviewAsync(olderRulesDraft);
            await SaveAsync(olderRulesDraft, null);
            // 常用查核條件「人工分錄」：filter-step.js conditionFromSpec 以 ui-core.js newFilterRule('manualAuto') 為底再套上規格，
            // 其他類型的預設鍵也一起送出（frontend-journey-filter-build 的 filler 逐字斷言同一份資料）。
            var manualCheck = new JsonObject
            {
                ["join"] = "AND", ["type"] = "manualAuto", ["prescreenKey"] = "suspiciousKeywords", ["field"] = "description",
                ["keywords"] = "", ["values"] = new JsonArray(), ["mode"] = "contains", ["normalization"] = "preserve",
                ["from"] = "", ["to"] = "", ["drCr"] = "debit", ["isManual"] = "true", ["pairMode"] = "exact",
                ["debitCategoryIds"] = new JsonArray("builtin.receivables"), ["creditCategoryIds"] = new JsonArray("builtin.revenue"),
                ["fieldId"] = "", ["operator"] = "", ["amountBasis"] = "absolute", ["value"] = "", ["digits"] = "3",
                ["maxEntries"] = "", ["windowDays"] = "",
            };
            var commonCheckDraft = Scenario("自訂：人工分錄", "合成：人工分錄。", new JsonArray { manualCheck });
            await PreviewAsync(commonCheckDraft);
            await SaveAsync(commonCheckDraft, null);
            amountDraft["rationale"] = "合成：修改後的動機。";
            await SaveAsync(amountDraft, 1);
            await Send("project.load", new { projectId });
            // 重新檢查會產生新的結果版本；filter-step.js 之後的查詢都帶畫面上最新的 resultRef.revision。
            var rechecked = await Send("filter.scenario.recheck", new { populationScope = Scope });
            var revision = rechecked.GetProperty("resultRef").GetProperty("revision").GetString()!;
            await PagedAsync("query.filterHitsPage", new JsonObject { ["scenarioPosition"] = 1 }, "documentNumber", "SHAPE");
            // 用到攸關資料元素的情境，命中明細多出那個欄位；舊式條件情境（第 6 個）搜尋 SHAPE，命中列的攸關金額有空白。
            await Send("query.filterHitsPage", new
            {
                scenarioPosition = 4, cursor = (string?)null, pageSize = 200, sort = (object?)null, search = (string?)null,
            });
            await Send("query.filterHitsPage", new
            {
                scenarioPosition = 6, cursor = (string?)null, pageSize = 200, sort = (object?)null, search = "SHAPE",
            });
            await VoucherPagesAsync(new JsonObject
            {
                ["scenarioPosition"] = 1, ["scenarioRevision"] = revision, ["populationScope"] = Scope,
            });
            await Send("query.tagMatrixScenarios");
            await PagedAsync("query.tagMatrixVoucherPage", new JsonObject(), "documentNumber", "SHAPE");
            await PagedAsync("query.tagMatrixRowPage", new JsonObject(), "documentNumber", "SHAPE");
            var criteria = await Send("export.criteriaSelectionReport", new { validationRunId, revision });

            // 第六步匯出底稿：export-step.js。
            await Send("export.workpaperStream", new
            {
                validationRunId, scenarioRevision = revision, scenarioPositions = new[] { 1, 2, 3, 4 },
            });

            // 匯出後回頭修改上游資料（使用者 2026-10-15 裁定這段旅程放進本輪）。使用者 2026-10-07 裁定上游修改清除下游：
            // 已匯出的報告標成過期，已存情境全部清掉（AuditDependencyPolicy）。之後每個修改的回應都帶已匯出報告的狀態。
            await Send("calendar.setNonWorkingDays", new { days = new[] { 0 } });
            await Send("import.holiday.fromFile", new { filePath = holidayFile, fileName = Path.GetFileName(holidayFile) });
            await Send("import.makeupDay.fromFile", new { filePath = makeupFile, fileName = Path.GetFileName(makeupFile) });
            await Send("import.authorizedPreparer.clear");
            await Send("import.authorizedPreparer.fromFile", preparer);
            await Send("project.update", new { entityName, projectCode, lastPeriodStart });
            await Send("accountMapping.save", new { changes = new[] { new { accountCode = firstAccountCode, categoryId = "builtin.cash" } } });
            // 報告清單檔（report-artifacts.json）一時被其他程式獨占時修改分類：變更照樣儲存，回應說明報告清單沒有更新。
            var taxonomyAfterExport = TaxonomyPayload((await Send("project.load", new { projectId })).GetProperty("taxonomy"));
            var reportList = Path.Combine(new JetProjectFolder(host.ProjectsRoot).GetProjectDirectory(projectId), ProjectReportArtifactStore.ManifestFileName);
            using (File.Open(reportList, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await Send("accountTaxonomy.save", taxonomyAfterExport);
            }

            // 總帳再加入同一份檔案時，後端提醒可能重複匯入；審計員改用示範的原始總帳整批取代，試算表也重新匯入。
            await ImportAsync("import.gl.fromFile", glPath, "合成總帳.xlsx", "append");
            await ImportAsync("import.gl.fromFile", glExport.GetProperty("filePath").GetString()!, glExport.GetProperty("fileName").GetString()!);
            await Send("import.tb.fromFile", new { mode = "replace", sources = new[] { csvSource } });
            // 原始總帳沒有攸關資料元素的欄，確認配對時一併拿掉。總帳重新投影會清掉科目配對，審計員重新匯入科目配對檔。
            await Send("mapping.commit.gl", GlCommit(glMapping, amountMode, new JsonArray()));
            await Send("mapping.commit.tb", TbCommit(tbMapping, changeMode));
            await Send("import.accountMapping.fromFile", new { filePath = accountPath, fileName = "合成科目配對.xlsx" });
            await Send("project.load", new { projectId });
            // 欄位配對改了，驗證結果跟著失效；審計員重跑資料驗證（原始總帳沒有差異），再存情境。
            await Send("validate.run");
            var debitDraft = Scenario("合成借方分錄", "合成：借方分錄。", new JsonArray
            {
                new JsonObject { ["type"] = "drCrOnly", ["join"] = "AND", ["drCr"] = "debit" },
            });
            var creditDraft = Scenario("合成貸方分錄", "合成：貸方分錄。", new JsonArray
            {
                new JsonObject { ["type"] = "drCrOnly", ["join"] = "AND", ["drCr"] = "credit" },
            });
            await SaveAsync(debitDraft, null);
            await SaveAsync(kctDraft, null);
            await SaveAsync(creditDraft, null);
            // 原始總帳沒有核准人員欄，抽樣明細、命中明細與矩陣明細的核准人員是空的。
            foreach (var action in new[] { "query.infSamplePage", "query.tagMatrixRowPage" })
            {
                await Send(action, new { cursor = (string?)null, pageSize = 200, sort = (object?)null, search = (string?)null });
            }

            await Send("query.filterHitsPage", new
            {
                scenarioPosition = 1, cursor = (string?)null, pageSize = 200, sort = (object?)null, search = (string?)null,
            });

            // 舊案件：第三個情境改成舊版規則存下、條件用到已拿掉的攸關金額欄。開案、重新檢查、新增與修改其他情境時
            // 都列在條件失效提示；審計員先移除新增的情境，最後移除失效的情境。
            await RewriteAsOlderScenarioAsync(projectId, 3, moneyFieldId);
            await Send("project.load", new { projectId });
            await Send("filter.scenario.recheck", new { populationScope = Scope });
            await SaveAsync(Scenario("合成貸方大額", "合成：貸方大額分錄。", new JsonArray
            {
                new JsonObject { ["type"] = "drCrOnly", ["join"] = "AND", ["drCr"] = "credit" },
                FieldValue("amount", "greaterThanOrEqual", value: "1000", amountBasis: "absolute"),
            }), null);
            debitDraft["rationale"] = "合成：修改後的借方動機。";
            await SaveAsync(debitDraft, 1);
            await Send("filter.scenario.remove", new { populationScope = Scope, position = 4, expectedName = "合成貸方大額" });
            await Send("filter.scenario.remove", new { populationScope = Scope, position = 3, expectedName = "合成貸方分錄" });
            await Send("project.load", new { projectId });

            // 各步驟共用：開啟資料夾、訊息紀錄、支援紀錄、開發工具與取消（app.js、ui-core.js、dev-panel.js）。
            await Send("host.openFolder", new { target = "projectFolder" });
            await Send("host.openFolder", new { artifactId = criteria.GetProperty("artifact").GetProperty("artifactId").GetString() });
            await Send("log.append", new { level = "info", text = "合成訊息" });
            await Send("log.recent", new { limit = 100 });
            await Send("log.recent");
            await Send("support.log.export", new { projectId });
            // dev-panel.js 從 dev.db.overview 的清單選一個資料表。
            var overview = await Send("dev.db.overview", new { });
            var tableName = overview.GetProperty("tables").EnumerateArray()
                .Where(table => table.GetProperty("rowCount").GetInt64() > 0)
                .Select(table => table.GetProperty("name").GetString()!)
                .Order(StringComparer.Ordinal)
                .First();
            await Send("dev.db.tableData", new { tableName, limit = 50, offset = 0 });
            await Send("dev.log.exportFile", new { projectId, correlationId = (string?)null });
            await Send("operation.cancel", new { requestId = "synthetic-request" });
            await Send("project.saveProgress", new { currentStep = 5 });
            await Send("project.releaseLock");
            await Send("support.log.export");

            // 回到案件清單後移除案件：app.js。案件資料夾裡還有被其他程式開著的檔案時，資料庫照樣刪除，回應提醒資料夾
            // 沒有清乾淨；審計員自己刪掉剩下的檔案後再讀清單。
            await Send("project.deletePreview", new { projectId });
            var projectDirectory = new JetProjectFolder(host.ProjectsRoot).GetProjectDirectory(projectId);
            var openFile = Path.Combine(projectDirectory, "合成開啟中的檔案.txt");
            await File.WriteAllTextAsync(openFile, "合成");
            using (File.Open(openFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await Send("project.delete", new { projectId });
            }

            Directory.Delete(projectDirectory, recursive: true);
            await Send("project.listLocal");
            await Send("host.exitApp");
        }

        async Task ImportAsync(string action, string path, string fileName, string mode = "replace")
        {
            var inspected = await Send("import.inspectFile", new { filePath = path });
            var sheet = FirstSheet(inspected);
            await Send("import.previewFile", new { filePath = path, limit = 10, sheetName = sheet });
            await Send(action, new { mode, sources = new[] { new { filePath = path, fileName, sheetName = sheet } } });
        }

        // ui-core.js 的分頁表格：先讀第一頁；點欄位標題排序後從第一頁重讀；換到下一頁時帶上一頁的 nextCursor 與同一個排序；
        // 「依號碼查看明細」帶搜尋文字。fixedFields 是各頁固定帶的欄位（例如 ruleKey）。
        async Task PagedAsync(string action, JsonObject fixedFields, string sortKey, string search)
        {
            JsonObject Page(string? cursor, int pageSize, bool sorted, string? text)
            {
                var payload = (JsonObject)fixedFields.DeepClone();
                payload["cursor"] = cursor;
                payload["pageSize"] = pageSize;
                payload["sort"] = sorted ? new JsonObject { ["key"] = sortKey, ["direction"] = "desc" } : null;
                payload["search"] = text;
                return payload;
            }

            await Send(action, Page(null, 200, false, null));
            var first = await Send(action, Page(null, 1, true, null));
            if (first.GetProperty("nextCursor").ValueKind == JsonValueKind.String)
            {
                await Send(action, Page(first.GetProperty("nextCursor").GetString(), 1, true, null));
            }

            await Send(action, Page(null, 200, false, search));
        }

        // validate-step.js 只換頁、不排序的清單：第一頁 cursor 為 null，之後帶上一頁的 nextCursor。
        async Task CursorPagesAsync(string action, JsonObject fixedFields)
        {
            var payload = (JsonObject)fixedFields.DeepClone();
            payload["cursor"] = null;
            payload["pageSize"] = 1;
            var first = await Send(action, payload);
            if (first.GetProperty("nextCursor").ValueKind == JsonValueKind.String)
            {
                var next = (JsonObject)fixedFields.DeepClone();
                next["cursor"] = first.GetProperty("nextCursor").GetString();
                next["pageSize"] = 200;
                await Send(action, next);
            }
        }

        // filter-vouchers.js 的傳票清單：換頁、點表頭排序與輸入號碼查看都帶第一頁的 queryRevision，游標綁定排序；
        // 展開傳票讀分錄時也帶 queryRevision。voucherRequest 是草稿（scenario）或已存情境（位置與版本）。
        async Task VoucherPagesAsync(JsonObject voucherRequest)
        {
            JsonObject With(params (string Key, JsonNode? Value)[] fields)
            {
                var payload = (JsonObject)voucherRequest.DeepClone();
                foreach (var (key, value) in fields)
                {
                    payload[key] = value;
                }

                return payload;
            }

            JsonObject Sort() => new() { ["key"] = "voucherTotal", ["direction"] = "desc" };
            var first = await Send("query.filterVoucherPage", With(("pageSize", 50)));
            var queryRevision = first.GetProperty("queryRevision").GetString();
            var sorted = await Send("query.filterVoucherPage", With(
                ("cursor", null), ("pageSize", 1), ("queryRevision", queryRevision), ("sort", Sort()), ("search", null)));
            if (sorted.GetProperty("nextCursor").ValueKind == JsonValueKind.String)
            {
                await Send("query.filterVoucherPage", With(("cursor", sorted.GetProperty("nextCursor").GetString()), ("pageSize", 1),
                    ("queryRevision", queryRevision), ("sort", Sort()), ("search", null)));
            }

            await Send("query.filterVoucherPage", With(
                ("cursor", null), ("pageSize", 50), ("queryRevision", queryRevision), ("sort", null), ("search", "SHAPE")));
            if (first.GetProperty("rows").GetArrayLength() == 0)
            {
                return;
            }

            var documentNumber = first.GetProperty("rows")[0].GetProperty("documentNumber").GetString();
            var entries = await Send("query.filterVoucherRowsPage", With(
                ("documentNumber", documentNumber), ("cursor", null), ("pageSize", 1), ("queryRevision", queryRevision)));
            if (entries.GetProperty("nextCursor").ValueKind == JsonValueKind.String)
            {
                await Send("query.filterVoucherRowsPage", With(("documentNumber", documentNumber),
                    ("cursor", entries.GetProperty("nextCursor").GetString()), ("pageSize", 50), ("queryRevision", queryRevision)));
            }
        }

        // filter-step.js 的預覽：先算命中，再讀第一頁命中傳票；展開第一張傳票讀它的分錄。
        // filter-step.js 預覽草稿後讀傳票清單；審計員在清單搜尋合成傳票 SHAPE-9999 並展開，搜尋不到時展開第一張。
        async Task PreviewAsync(JsonObject scenario)
        {
            await Send("filter.preview", new JsonObject { ["populationScope"] = Scope, ["scenario"] = scenario.DeepClone() });
            var page = await Send("query.filterVoucherPage", new JsonObject
            {
                ["scenario"] = scenario.DeepClone(), ["populationScope"] = Scope, ["pageSize"] = 50,
            });
            var queryRevision = page.GetProperty("queryRevision").GetString();
            var searched = await Send("query.filterVoucherPage", new JsonObject
            {
                ["scenario"] = scenario.DeepClone(), ["populationScope"] = Scope, ["cursor"] = null, ["pageSize"] = 50,
                ["queryRevision"] = queryRevision, ["sort"] = null, ["search"] = "SHAPE",
            });
            var rows = searched.GetProperty("rows").GetArrayLength() > 0 ? searched.GetProperty("rows") : page.GetProperty("rows");
            if (rows.GetArrayLength() > 0)
            {
                await Send("query.filterVoucherRowsPage", new JsonObject
                {
                    ["scenario"] = scenario.DeepClone(), ["populationScope"] = Scope,
                    ["documentNumber"] = rows[0].GetProperty("documentNumber").GetString(),
                    ["cursor"] = null, ["pageSize"] = 50, ["queryRevision"] = queryRevision,
                });
            }
        }

        async Task FixSampleSeedAsync(string projectId)
        {
            var path = new JetProjectFolder(host.ProjectsRoot).GetProjectJsonPath(projectId);
            var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            document["sampleSeed"] = 48_271;
            await File.WriteAllTextAsync(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        // 模擬舊版 JET 存下的情境（同 FilterScenarioRuleUpgradeOnLoadTests）：把資料庫裡這個位置的情境定義改成舊的
        // 規則版本，再加一條指向已不存在的攸關資料元素的條件。這是畫面送不出來的狀態，只能直接改資料庫。
        async Task RewriteAsOlderScenarioAsync(string projectId, int position, string missingFieldId)
        {
            var folder = new JetProjectFolder(host.ProjectsRoot);
            ILocalProjectDatabase database = provider == "sqlite" ? new SqliteProjectDatabase(folder) : new DuckDbProjectDatabase(folder);
            await using var connection = database.CreateConnection(projectId);
            await connection.OpenAsync();
            string definitionJson;
            await using (var read = connection.CreateCommand())
            {
                read.CommandText = "SELECT definition_json FROM config_filter_scenario WHERE position = @position;";
                read.AddWithValue("@position", (long)position);
                definitionJson = (string)(await read.ExecuteScalarAsync())!;
            }

            var definition = JsonNode.Parse(definitionJson)!.AsObject();
            definition["logicVersion"] = "filter-2026-09-18-v15";
            definition["groups"]![0]!["rules"]!.AsArray().Add(new JsonObject
            {
                ["join"] = "AND", ["type"] = "typed", ["fieldId"] = missingFieldId, ["operator"] = "isBlank",
            });
            await using var write = connection.CreateCommand();
            write.CommandText = "UPDATE config_filter_scenario SET definition_json = @definition WHERE position = @position;";
            write.AddWithValue("@definition", definition.ToJsonString());
            write.AddWithValue("@position", (long)position);
            await write.ExecuteNonQueryAsync();
        }

        // filter-step.js 一次只存一個情境：新情境 position 為 null，修改時帶位置與原名稱。
        async Task SaveAsync(JsonObject scenario, int? position)
        {
            var payload = new JsonObject
            {
                ["populationScope"] = Scope, ["position"] = position, ["scenario"] = scenario.DeepClone(),
            };
            if (position is not null)
            {
                payload["expectedName"] = scenario["name"]!.GetValue<string>();
            }

            await Send("filter.scenario.save", payload);
        }
    }

    private static readonly string[] SourceExtensions = [".xlsx", ".xlsm", ".xls", ".csv", ".txt", ".mdb", ".accdb"];

    /// <summary>import-step.js 預設選 import.inspectFile 回應的第一個工作表；文字檔沒有工作表。</summary>
    private static string? FirstSheet(JsonElement inspected) =>
        inspected.TryGetProperty("worksheets", out var sheets) && sheets.ValueKind == JsonValueKind.Array && sheets.GetArrayLength() > 0
            ? sheets[0].GetProperty("name").GetString()
            : null;

    /// <summary>mapping-step.js glCommitPayload。示範資料有配對傳票核准日，approvalDateMode 預設送 mapped。</summary>
    private static JsonObject GlCommit(IReadOnlyDictionary<string, string> mapping, string amountMode, JsonArray rdeFields,
        string approvalDateMode = "mapped") => new()
    {
        ["mapping"] = new JsonObject(mapping.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))),
        ["amountMode"] = amountMode,
        ["approvalDateMode"] = approvalDateMode,
        ["manualAutoPolicy"] = new JsonObject { ["manualValues"] = new JsonArray("1"), ["automaticValues"] = new JsonArray("0") },
        ["rdeFields"] = rdeFields.DeepClone(),
    };

    /// <summary>確認配對的回應裡，後端替攸關資料元素配置的識別；mapping-step.js 之後送出時沿用。</summary>
    private static JsonArray CommittedRdeFields(JsonElement commit) => new(commit.GetProperty("rdeFields").EnumerateArray()
        .Select(field => (JsonNode?)new JsonObject
        {
            ["sourceColumn"] = field.GetProperty("sourceColumn").GetString(),
            ["label"] = field.GetProperty("label").GetString(),
            ["valueType"] = field.GetProperty("valueType").GetString(),
            ["fieldId"] = field.GetProperty("fieldId").GetString(),
        })
        .ToArray());

    /// <summary>mapping-step.js 確認試算表配對。</summary>
    private static object TbCommit(IReadOnlyDictionary<string, string> mapping, string changeMode) => new { mapping, changeMode };

    /// <summary>validate-step.js 儲存分類設定：照畫面順序送出每個分類，已存的分類帶原本的識別。</summary>
    private static JsonObject TaxonomyPayload(JsonElement taxonomy) => new()
    {
        ["revision"] = taxonomy.GetProperty("revision").GetInt64(),
        ["categories"] = new JsonArray(taxonomy.GetProperty("categories").EnumerateArray()
            .Select((category, index) => (JsonNode?)new JsonObject
            {
                ["label"] = category.GetProperty("label").GetString(),
                ["ordinal"] = index,
                ["semanticRole"] = JsonNode.Parse(category.GetProperty("semanticRole").GetRawText()),
                ["parentCategoryId"] = JsonNode.Parse(category.GetProperty("parentCategoryId").GetRawText()),
                ["categoryId"] = category.GetProperty("categoryId").GetString(),
            })
            .ToArray()),
    };

    /// <summary>filter-step.js 的 toWireDraft：名稱、動機、條件組與命名來源。</summary>
    private static JsonObject Scenario(string name, string rationale, JsonArray rules, string?[]? letters = null, bool automatic = false) => new()
    {
        ["name"] = name,
        ["rationale"] = rationale,
        ["groups"] = new JsonArray
        {
            new JsonObject { ["join"] = "OR", ["matchScope"] = "row", ["rules"] = rules },
        },
        ["editorOrigins"] = Origins(automatic, letters ?? rules.Select(_ => (string?)null).ToArray()),
    };

    /// <summary>filter-step.js projectWireScenario 的 editorOrigins：每組一份字母清單，不是 KCT 卡帶進來的條件為 null。</summary>
    private static JsonObject Origins(bool automatic, params string?[][] groups) => new()
    {
        ["version"] = 1,
        ["legacyKctSource"] = false,
        ["nameIsAutomatic"] = automatic,
        ["rationaleIsAutomatic"] = automatic,
        ["groups"] = new JsonArray(groups.Select(letters => (JsonNode?)new JsonObject
        {
            ["letters"] = new JsonArray(letters.Select(letter => (JsonNode?)letter).ToArray()),
        }).ToArray()),
    };

    /// <summary>filter-values.js 的 wire：欄位值條件只帶比較方式用到的那一個值。</summary>
    private static JsonObject FieldValue(string field, string @operator, string? value = null, string[]? values = null,
        string? amountBasis = null, string join = "AND")
    {
        var rule = new JsonObject
        {
            ["type"] = "fieldValue", ["join"] = join, ["operator"] = @operator, ["includeBlank"] = false, ["field"] = field,
        };
        if (value is not null) rule["value"] = value;
        if (values is not null) rule["values"] = new JsonArray(values.Select(item => (JsonNode?)item).ToArray());
        if (amountBasis is not null) rule["amountBasis"] = amountBasis;
        return rule;
    }

    /// <summary>
    /// 示範 GL 加工成合成檔：加核准人員欄（每四筆有一筆和建立人員相同）、兩個攸關資料元素用的欄（文字與金額，金額每五筆有一筆空白）、
    /// 一個整欄空白的欄，以及另一種配對方式用的借方金額、貸方金額、分錄來源與過帳狀態（每七筆有一筆未過帳）；三筆資料的總帳日期清空
    /// （來源資料品質）；另外三筆金額加 1（借貸不平與完整性差異）；再加一張借記 9999 的傳票 SHAPE-9999，這個科目不在試算表與
    /// 科目配對檔（完整性差異與科目配對差異），貸方那一列的攸關金額空白。每種差異都超過一筆，清單才有第二頁。
    /// </summary>
    private static string SyntheticGl(string demoPath)
    {
        var path = Path.Combine(Path.GetTempPath(), $"jet-shape-gl-{Guid.NewGuid():N}.xlsx");
        using (var workbook = new XLWorkbook(demoPath))
        {
            var sheet = workbook.Worksheets.First();
            var headers = sheet.Row(1).CellsUsed().ToDictionary(cell => cell.GetString(), cell => cell.Address.ColumnNumber, StringComparer.Ordinal);
            var lastRow = sheet.LastRowUsed()!.RowNumber();
            var extra = sheet.LastColumnUsed()!.ColumnNumber() + 1;
            sheet.Cell(1, extra).Value = "合成部門";
            sheet.Cell(1, extra + 1).Value = "合成金額欄";
            sheet.Cell(1, extra + 2).Value = "合成空白欄";
            sheet.Cell(1, extra + 3).Value = "合成核准人員";
            sheet.Cell(1, extra + 4).Value = "合成借方金額";
            sheet.Cell(1, extra + 5).Value = "合成貸方金額";
            sheet.Cell(1, extra + 6).Value = "合成分錄來源";
            sheet.Cell(1, extra + 7).Value = "合成過帳狀態";
            for (var row = 2; row <= lastRow; row++)
            {
                sheet.Cell(row, extra).Value = "部門" + (row % 3);
                // 每五筆有一筆的攸關金額欄空白：命中明細與傳票分錄的攸關資料元素值是 null。
                if (row % 5 != 0)
                {
                    sheet.Cell(row, extra + 1).Value = row * 10.5;
                }

                sheet.Cell(row, extra + 3).Value = row % 4 == 0 ? sheet.Cell(row, headers["建立人員"]).Value : "合成核准者" + (row % 2);
            }

            foreach (var row in new[] { 2, 8, 14 })
            {
                sheet.Cell(row, headers["總帳日期"]).Clear(XLClearOptions.Contents);
            }

            foreach (var row in new[] { 3, 9, 15 })
            {
                var amount = sheet.Cell(row, headers["金額"]);
                amount.Value = (amount.Value.IsNumber ? amount.Value.GetNumber() : double.Parse(amount.GetString(), CultureInfo.InvariantCulture)) + 1;
            }
            for (var line = 1; line <= 2; line++)
            {
                var row = lastRow + line;
                foreach (var column in headers.Values)
                {
                    sheet.Cell(row, column).Value = sheet.Cell(4, column).Value;
                }

                sheet.Cell(row, headers["傳票號碼"]).Value = "SHAPE-9999";
                sheet.Cell(row, headers["傳票項次"]).Value = line;
                sheet.Cell(row, headers["科目代號"]).Value = line == 1 ? "9999" : "1101";
                sheet.Cell(row, headers["科目名稱"]).Value = line == 1 ? "合成未配對科目" : "現金";
                sheet.Cell(row, headers["摘要"]).Value = "合成未配對科目傳票";
                sheet.Cell(row, headers["金額"]).Value = 100;
                sheet.Cell(row, headers["借方旗標"]).Value = line == 1 ? "1" : "0";
                sheet.Cell(row, extra).Value = "部門0";
                // 貸方那一列的攸關金額空白：展開這張傳票時，分錄的攸關資料元素值是 null。
                if (line == 1)
                {
                    sheet.Cell(row, extra + 1).Value = 100;
                }

                sheet.Cell(row, extra + 3).Value = "合成核准者0";
            }

            // 借方、貸方金額照最後的金額與借貸旗標拆開。
            for (var row = 2; row <= lastRow + 2; row++)
            {
                var amountCell = sheet.Cell(row, headers["金額"]);
                var value = amountCell.Value.IsNumber ? amountCell.Value.GetNumber() : double.Parse(amountCell.GetString(), CultureInfo.InvariantCulture);
                var debit = sheet.Cell(row, headers["借方旗標"]).GetString() == "1";
                sheet.Cell(row, extra + 4).Value = debit ? value : 0;
                sheet.Cell(row, extra + 5).Value = debit ? 0 : value;
                sheet.Cell(row, extra + 6).Value = row % 2 == 0 ? "總帳" : "應付";
                sheet.Cell(row, extra + 7).Value = row % 7 == 0 ? "未過帳" : "已過帳";
            }

            workbook.SaveAs(path);
        }

        return path;
    }

    /// <summary>
    /// 示範科目配對檔加工：最後一個科目的分類清空（分類空白清單），另加兩個 GL 沒有的科目（只在配對檔的差異，清單有第二頁）。
    /// renameHeaders 時把三個欄名改成認不出的名稱，後端改依欄位順序對應。
    /// </summary>
    private static string SyntheticAccountMapping(string demoPath, bool renameHeaders = false)
    {
        var path = Path.Combine(Path.GetTempPath(), $"jet-shape-accounts-{Guid.NewGuid():N}.xlsx");
        using (var workbook = new XLWorkbook(demoPath))
        {
            var sheet = workbook.Worksheets.First();
            var headers = sheet.Row(1).CellsUsed().ToDictionary(cell => cell.GetString(), cell => cell.Address.ColumnNumber, StringComparer.Ordinal);
            var lastRow = sheet.LastRowUsed()!.RowNumber();
            sheet.Cell(lastRow, headers["標準化分類"]).Clear(XLClearOptions.Contents);
            foreach (var (offset, code) in new[] { (1, "8888"), (2, "8889") })
            {
                sheet.Cell(lastRow + offset, headers["科目代號"]).Value = code;
                sheet.Cell(lastRow + offset, headers["科目名稱"]).Value = "合成僅配對科目" + code;
                sheet.Cell(lastRow + offset, headers["標準化分類"]).Value = sheet.Cell(3, headers["標準化分類"]).Value;
            }

            if (renameHeaders)
            {
                foreach (var (header, index) in new[] { "科目代號", "科目名稱", "標準化分類" }.Select((header, index) => (header, index)))
                {
                    sheet.Cell(1, headers[header]).Value = "合成欄" + (index + 1);
                }
            }

            workbook.SaveAs(path);
        }

        return path;
    }

    /// <summary>科目配對檔轉成文字檔，第一個科目的名稱用引號包住、內含換行，匯入時會附上欄位內含換行的提醒。</summary>
    private static string AccountMappingCsvWithLineBreak(string workbookPath)
    {
        var path = WorkbookCsv(workbookPath, [], _ => []);
        var lines = File.ReadAllText(path).Split('\n');
        var nameColumn = Array.IndexOf(lines[0].Split(','), "科目名稱");
        var cells = lines[1].Split(',');
        cells[nameColumn] = "\"" + cells[nameColumn] + "\n合成續行\"";
        lines[1] = string.Join(",", cells);
        File.WriteAllText(path, string.Join("\n", lines), new UTF8Encoding(false));
        return path;
    }

    /// <summary>
    /// 示範試算表轉成文字檔，最後加一個整欄空白的欄，以及另外三種變動金額方式用的欄：年度變動（借方減貸方）、
    /// 期初與期末餘額、期初與期末的借貸方。期初一律 1000，期末是期初加變動，三種方式算出的變動都和借方、貸方金額相同。
    /// </summary>
    private static string TrialBalanceCsv(string demoPath, string debitColumn, string creditColumn)
    {
        string[] headers = ["合成空白欄", "合成年度變動", "合成期初餘額", "合成期末餘額", "合成期初借方", "合成期初貸方", "合成期末借方", "合成期末貸方"];
        return WorkbookCsv(demoPath, headers, row =>
        {
            var debit = Number(row.Cell(Column(row, debitColumn)));
            var credit = Number(row.Cell(Column(row, creditColumn)));
            var change = debit - credit;
            const decimal opening = 1000m;
            return ["", Text(change), Text(opening), Text(opening + change), Text(opening), Text(0m), Text(opening + debit), Text(credit)];
        });

        static int Column(IXLRow row, string header) => row.Worksheet.Row(1).CellsUsed()
            .Single(cell => cell.GetString() == header).Address.ColumnNumber;

        static decimal Number(IXLCell cell) => cell.IsEmpty() ? 0m
            : cell.Value.IsNumber ? (decimal)cell.Value.GetNumber() : decimal.Parse(cell.GetString(), CultureInfo.InvariantCulture);

        static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 工作簿第一個工作表轉成 UTF-8、逗號分隔的文字檔；日期寫成 yyyy-MM-dd。extraHeaders 與 extraCells 在每列後面多加的欄。
    /// </summary>
    private static string WorkbookCsv(string workbookPath, IReadOnlyList<string> extraHeaders, Func<IXLRow, IReadOnlyList<string>> extraCells)
    {
        var path = Path.Combine(Path.GetTempPath(), $"jet-shape-{Guid.NewGuid():N}.csv");
        var text = new StringBuilder();
        using (var workbook = new XLWorkbook(workbookPath))
        {
            var sheet = workbook.Worksheets.First();
            var lastColumn = sheet.LastColumnUsed()!.ColumnNumber();
            foreach (var row in sheet.RowsUsed())
            {
                var cells = Enumerable.Range(1, lastColumn).Select(column =>
                {
                    var value = row.Cell(column).Value;
                    return value.IsNumber ? value.GetNumber().ToString(CultureInfo.InvariantCulture)
                        : value.IsDateTime ? value.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : row.Cell(column).GetString();
                }).Concat(row.RowNumber() == 1 ? extraHeaders : extraCells(row));
                text.Append(string.Join(",", cells.Select(Quote))).Append('\n');
            }
        }

        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        return path;

        static string Quote(string cell) => cell.Contains(',', StringComparison.Ordinal) || cell.Contains('"', StringComparison.Ordinal)
            ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : cell;
    }

    /// <summary>檔案對話框改成依序回傳指定的路徑；開資料夾與關閉視窗不做事。</summary>
    private sealed class JourneyHostShell : IHostShell
    {
        public string? NextFile { get; set; }

        public IReadOnlyList<string> NextFiles { get; set; } = [];

        public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<string> extensions, CancellationToken cancellationToken) =>
            Task.FromResult(NextFile);

        public Task<IReadOnlyList<string>> PickOpenFilesAsync(string title, IReadOnlyList<string> extensions,
            CancellationToken cancellationToken) => Task.FromResult(NextFiles);

        public void RequestExit()
        {
        }

        public Task RevealInExplorerAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>計時器永遠不到期：報告清單更新一定等到完成。</summary>
    private sealed class NeverExpiringClock : TimeProvider
    {
        public static readonly NeverExpiringClock Instance = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new IdleTimer();

        private sealed class IdleTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
