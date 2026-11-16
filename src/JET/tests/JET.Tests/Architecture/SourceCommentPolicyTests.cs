using System.Text.RegularExpressions;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 原始碼的註解與文字只說明程式現在做什麼、為什麼這樣做。開發過程的紀錄另外保存，不寫進原始碼：
/// 需求決議的編號、測試執行的編號、測試修改經過的說明、開發計畫的階段名稱、開發任務的用語，
/// 以及誰提了什麼意見、怎麼選定做法的經過。
/// 註解也不引用文件的章節：只拿到這個方案資料夾的人查不到那些文件。
/// </summary>
public sealed class SourceCommentPolicyTests
{
    private static readonly string[] Extensions =
    [
        ".cs", ".js", ".cjs", ".css", ".html", ".csproj", ".json",
        ".editorconfig", ".props", ".targets", ".pubxml",
    ];

    // 樣式用字串相接寫成，這個檔案自己才不會被掃到。
    private static readonly (string Name, Regex Pattern)[] DevelopmentRecords =
    [
        ("需求決議的編號", new Regex("(?:裁" + @"定|[Rr]uling)\s*\d{4}-\d{2}-\d{2}-\d{2}", RegexOptions.CultureInvariant)),
        ("測試執行的編號", new Regex(@"(?<!\d)20\d{6}-\d{9}-" + "[0-9a-f]{32}|首" + @"敗\s*\d{6,}", RegexOptions.CultureInvariant)),
        ("測試修改經過", new Regex(
            "第一次" + "失敗|首次" + "失敗|原斷" + "言|斷" + "言(?:保留|不變|仍保留|均保留|本身不變|改鎖|維持不變)",
            RegexOptions.CultureInvariant)),
        // 註解、字串與識別字都要看：開發階段與批次的編號、分組代號，不分大小寫，也包括接在識別字中間的寫法。
        // 匯入批次的代號在數字前有連字號，不算批次編號。
        ("計畫與階段的名稱", new Regex(
            @"(?:(?<![A-Za-z])(?i:stage|phase)|(?<=[a-z0-9])(?:Stage|Phase))[-_ ]?\d+"
            + @"|(?:(?<![A-Za-z])(?i:batch)|(?<=[a-z0-9])Batch)\d+"
            + @"|(?:(?<![A-Za-z])|(?<=[a-z]))Track ?[A-Z](?![a-z])"
            + "|第[一二三四五六七八九十]+(?:波|輪|階段)",
            RegexOptions.CultureInvariant)),
        // 比對用的範例檔、任務說明與任務編號、開發時用的腳本與技能名稱，以及只寫「照規定」而不寫規定內容的說法。
        // 「可靠性樣本」「摘要樣本」這類資料上的樣本，以及 C# 的 Task 型別都不算。
        ("開發任務的用語", new Regex(
            "(?:對齊|皆照|和|舊版)樣" + "本|本機參考樣" + "本|樣" + "本(?:顯示形式|全表)"
            + "|凍結(?:契" + "約|裁" + "決)"
            + "|(?<![A-Za-z])Ta" + @"sk ?\d+ ?[:：]|[此本] ?ta" + "sk(?![A-Za-z])"
            + "|TD" + @"D ?#\d|jet-tes" + @"ting|\.git/s" + "dd|(?i:user-appro" + "ved)"
            + "|(?<![A-Za-z])bri" + "ef(?:[：:]| ?設計| ?允許)|DR" + @"Y ?(?:閾值 ?)?= ?\d",
            RegexOptions.CultureInvariant)),
        // 用中文指稱開發工作的某一項或某一段進度。描述程式執行過程的「讀取階段」「投影階段」「呈現階段」、
        // 流程總覽的六階段，以及匯入步驟的任務清單與任務列，都不算。
        // 接中文數字的寫法要在數字後面斷句才算，「一致」「一律」「十分」這類詞不會被當成編號。
        ("開發任務與階段的中文說法", new Regex(
            "本任" + "務|[下上]一個?任" + "務(?![列清])"
            + "|本階" + "段|(?<![呈實發表出])現階" + "段"
            + "|階" + @"段[一二三四五六七八九十]+(?=[的：:，。、；\s）)」]|$)",
            RegexOptions.CultureInvariant)),
        // 誰提了什麼意見、看過什麼之後選了哪個做法，是需求的來歷。
        // 程式對使用者做的事（向使用者說明失敗原因）與使用者在畫面上的操作（選取、指定、要求取消）都不算。
        ("意見與選擇的經過", new Regex(
            "(?<![向對給讓])使用者(?:指" + "出|回" + "報|反" + "映|回" + "饋)|看過樣" + "張",
            RegexOptions.CultureInvariant)),
    ];

    // 只看註解的樣式：比對依據的出處字串也會寫文件名稱，那是資料，不是註解。
    // 外部標準 ECMA-376 的條文編號可以寫；只寫 manifest 的句子多半在講程式自己記錄的報告清單，也不算。
    private static readonly (string Name, Regex Pattern)[] DocumentSections =
    [
        ("文件章節的寫法", new Regex(
            "(?<!ECMA-376 )\u00A7"
            + "|(?i:jet-gui" + "de)|(?i:master sp" + @"ec)|(?i:gui" + @"de sections? \d)"
            + "|(?<![A-Za-z])(?:gui" + "de|sp" + @"ec) ?\d+(?:\.\d+)+|(?<![A-Za-z])sp" + @"ec [A-Z]\.\d"
            + "|(?<![A-Za-z])F" + @"D \d|action-contract-mani" + "fest"
            + "|(?:(?i:mani" + "fest)|前端說" + "明|action 契" + "約|(?i:frontend descrip" + @"tion))\s*「",
            RegexOptions.CultureInvariant)),
        // 用中文寫的節號與章號，數字可以是中文、阿拉伯數字或帶小數點的編號。
        // 畫面與產品用語裡的步、列、項、組、條不是文件的章節；樹狀資料的「節點」也不是。
        ("中文寫的章節引用", new Regex(
            "第" + @"\s*[0-9一二三四五六七八九十百]+(?:\.[0-9]+)*\s*[節" + "章](?!點)",
            RegexOptions.CultureInvariant)),
    ];

    private static readonly Regex CommentStart = new(@"(?<!:)//|/\*|<!--|^\s*\*", RegexOptions.CultureInvariant);

    /// <summary>一行裡從註解記號開始的部分；沒有註解記號時回傳 null。網址的雙斜線不算。</summary>
    private static string? CommentPart(string line)
    {
        var match = CommentStart.Match(line);
        return match.Success ? line[match.Index..] : null;
    }

    /// <summary>
    /// 一個檔案裡每一行的註解部分，連同行的索引。除了單行就看得出來的註解記號，還認得兩種寫法：
    /// .editorconfig 以井字號或分號開頭的整行；以及跨行的區塊註解與 XML 註解，
    /// 也就是一行以開頭記號起頭而沒有在同一行結束時，之後各行到結束記號為止都算註解。
    /// 開頭記號要在行首才往下延續，字串裡剛好出現同樣的符號時不會把後面的程式當成註解。
    /// </summary>
    private static IEnumerable<(int Index, string Comment)> CommentLines(string extension, IReadOnlyList<string> lines)
    {
        var hashComments = string.Equals(extension, ".editorconfig", StringComparison.OrdinalIgnoreCase);
        string? blockEnd = null;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (hashComments)
            {
                var text = line.TrimStart();
                if (text.StartsWith('#') || text.StartsWith(';'))
                {
                    yield return (index, text);
                }

                continue;
            }

            if (blockEnd is not null)
            {
                var end = line.IndexOf(blockEnd, StringComparison.Ordinal);
                if (end < 0)
                {
                    yield return (index, line);
                    continue;
                }

                var afterBlock = CommentPart(line[(end + blockEnd.Length)..]);
                blockEnd = null;
                yield return (index, afterBlock is null ? line[..end] : line[..end] + " " + afterBlock);
                continue;
            }

            if (CommentPart(line) is not { } comment)
            {
                continue;
            }

            yield return (index, comment);
            var start = line.TrimStart();
            if (start.StartsWith("/*", StringComparison.Ordinal) && !start.Contains("*/", StringComparison.Ordinal))
            {
                blockEnd = "*/";
            }
            else if (start.StartsWith("<!--", StringComparison.Ordinal) && !start.Contains("-->", StringComparison.Ordinal))
            {
                blockEnd = "-->";
            }
        }
    }

    [Fact]
    public void ProductAndTestSources_DoNotCarryDevelopmentRecords()
    {
        var root = TestRepositoryPaths.SolutionRoot;
        var findings = new List<string>();
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                     .Where(path => !Path.GetRelativePath(root, path)
                         .Split(Path.DirectorySeparatorChar)
                         .Any(part => part is "bin" or "obj"))
                     .Order(StringComparer.Ordinal))
        {
            scanned++;
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                foreach (var (name, pattern) in DevelopmentRecords)
                {
                    if (pattern.IsMatch(lines[index]))
                    {
                        findings.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{index + 1}: {name}");
                    }
                }
            }
        }

        Assert.True(scanned > 500, $"只掃到 {scanned} 個檔，方案資料夾的位置可能不對。");
        Assert.True(findings.Count == 0,
            "原始碼不寫開發過程的紀錄：" + Environment.NewLine + string.Join(Environment.NewLine, findings.Take(40)));
    }

    [Theory]
    [InlineData("// 空白預設不列入。", false)]
    [InlineData("// 期末日 2026-12-31 當天也算期內。", false)]
    [InlineData("\"custom.0123456789abcdef0123456789abcdef\"", false)]
    [InlineData("// 斷言比對命中的傳票號碼，不只比筆數。", false)]
    [InlineData("// 首頁失敗時保留原本的清單。", false)]
    [InlineData("if (stage == ImportFailureStage.Rows) return;", false)]
    [InlineData("// 讀來源檔的階段（選項、標頭、資料列）先依來源格式分。", false)]
    [InlineData("// 第 1 組第 2 條：此條件使用的欄位已不存在。", false)]
    [InlineData("importState: { gl: { batchId: 'batch-1', rowCount: 2 } },", false)]
    [InlineData(".AddRow(\"ROUND-1\", \"2025-03-05\", \"1101\")", false)]
    [InlineData("entry.Procedure == \"Step5_Export_Excel_TW\"", false)]
    [InlineData("public sealed class ReportBatchPublicationTests", false)]
    [InlineData("// 這一句的用字已定案，逐字比對。", false)]
    [InlineData("// 可靠性樣本照舊版依傳票號碼排序。", false)]
    [InlineData("// 分頁金額一律走 Ui.money，和摘要樣本保持相同格式。", false)]
    [InlineData("// 檔案讀取移出 UI thread，避免匯入期間介面凍結。", false)]
    [InlineData("await Task.WhenAll(first, second);", false)]
    [InlineData("// 該 task 結束後才釋放連線。", false)]
    [InlineData("// 這份簡報的英文是 briefing。", false)]
    [InlineData("// 金額可能是文字儲存格，讀取階段不解析數值語意（投影階段解析）。", false)]
    [InlineData("// 六階段只在流程總覽裡顯示；已完成的階段即使正在回看，仍算完成。", false)]
    [InlineData("// 各階段一律先寫支援日誌，兩階段一起完成才算成功。", false)]
    [InlineData("// 呈現階段只讀摘要，不重新查詢。", false)]
    [InlineData("// 階段 1：第一個工作表以新增方式匯入。", false)]
    [InlineData("// 任務清單各列的就地展開狀態；完成後展開下一個任務列。", false)]
    [InlineData("// 預設收合，不與審計員的當前任務競爭注意力。", false)]
    [InlineData("// 進度通知丟出的取消例外不是使用者要求的取消。", false)]
    [InlineData("// 使用者選取的檔案正被 Excel 開啟時，改用共用讀取。", false)]
    [InlineData("// 使用者指定的工作表不存在時，回報找不到工作表。", false)]
    [InlineData("// 匯入失敗時向使用者回報原因與接下來能做的事。", false)]
    public void PatternsLeaveOrdinaryCommentsAndDataAlone(string line, bool expected)
    {
        Assert.Equal(expected, DevelopmentRecords.Any(record => record.Pattern.IsMatch(line)));
    }

    [Fact]
    public void PatternsRecogniseEachKindOfDevelopmentRecord()
    {
        var decision = "// 依裁" + "定 2026-01-02-03 調整。";
        var run = "// 20260102-030405678-" + new string('a', 32);
        var history = "// 第一次" + "失敗時的訊息。";
        var shortRun = "// Public首" + "敗100911120：補上必要的欄位。";
        var keptAssertions = "// 欄位改成必填；原斷" + "言保留。";
        var unchangedAssertions = "// 只換夾具，斷" + "言不變。";
        var stage = "\"Sta" + "ge 8 registry diff\"";
        var track = "\"Tr" + "ack O registry diff\"";
        var phase = "/// Pha" + "se1 helper。";
        var wave = "// 第二" + "波：常駐右欄。";
        var stageInIdentifier = "using var document = OpenSta" + "ge8Document(bytes);";
        var stageWithHyphen = "\"changed from the sta" + "ge-3 fixture baseline.\"";
        var stageInConstant = "\"JET_STA" + "GE10_APPEARANCE_COMPRESSION_MODE\"";
        var phaseInData = "\"pha" + "se11-tb-import\"";
        var batchInData = "\"ba" + "tch9-broken\"";
        var batchInIdentifier = "const fixture = ba" + "tch8Fixture();";
        var trackInIdentifier = "var entries = Tra" + "ckWEntries();";
        var alignedToSample = "// 表頭合併範圍(逐字對齊" + "樣本)。";
        var sampleLayout = "// 日期轉成樣本" + "顯示形式 yyyy/MM/dd。";
        var localSample = "/// 對本機參考" + "樣本逐字復解的 cell 內容。";
        var frozenContract = "// amountBasis 是凍結" + "契約的必填值。";
        var numberedTask = "// ===== Ta" + "sk 4:step3 高風險條件彙總 =====";
        var thisTask = "// 此 ta" + "sk 尚無 handler。";
        var numberedTdd = "/// 涵蓋 TD" + "D #5（滿載覆寫最舊）。";
        var testingSkill = "/// 手寫 recording stub（jet-tes" + "ting skill）。";
        var scratchScript = "/// 用 .git/s" + "dd/xlsx_inspect.py 取得。";
        var userApproved = "// User-appro" + "ved final schema.";
        var briefNote = "// 欄序以此為準(bri" + "ef：依 position 升冪)。";
        var dryThreshold = "// 資料表共用骨架(DR" + "Y=3)。";
        Assert.Equal("需求決議的編號", Assert.Single(DevelopmentRecords, record => record.Pattern.IsMatch(decision)).Name);
        Assert.Equal("測試執行的編號", Assert.Single(DevelopmentRecords, record => record.Pattern.IsMatch(run)).Name);
        Assert.Equal("測試修改經過", Assert.Single(DevelopmentRecords, record => record.Pattern.IsMatch(history)).Name);
        Assert.Equal("測試執行的編號", Assert.Single(DevelopmentRecords, record => record.Pattern.IsMatch(shortRun)).Name);
        Assert.Equal("測試修改經過", Assert.Single(DevelopmentRecords, record => record.Pattern.IsMatch(keptAssertions)).Name);
        Assert.Equal("測試修改經過", Assert.Single(DevelopmentRecords, record => record.Pattern.IsMatch(unchangedAssertions)).Name);
        Assert.All(new[]
        {
            stage, track, phase, wave, stageInIdentifier, stageWithHyphen, stageInConstant,
            phaseInData, batchInData, batchInIdentifier, trackInIdentifier,
        }, line =>
            Assert.Equal("計畫與階段的名稱", Assert.Single(DevelopmentRecords, record => record.Pattern.IsMatch(line)).Name));
        Assert.All(new[]
        {
            alignedToSample, sampleLayout, localSample, frozenContract, numberedTask, thisTask,
            numberedTdd, testingSkill, scratchScript, userApproved, briefNote, dryThreshold,
        }, line =>
            Assert.Equal("開發任務的用語", Assert.Single(DevelopmentRecords, record => record.Pattern.IsMatch(line)).Name));

        var thisAssignment = "/// 本任" + "務只建目錄、不改前端。";
        var nextAssignment = "/// 決定下一個任" + "務該怎麼呈現。";
        var nextAssignmentShort = "/// 等價於下一任" + "務的可瀏覽白名單。";
        var thisPeriod = "/// 稽核寫入埠；本階" + "段刻意不提供查詢。";
        var currentPeriod = "/// 現階" + "段部門內使用。";
        var numberedPeriod = "rationale = \"階" + "段九的同一個數字\",";
        var numberedPeriodAtEnd = "// 沿用階" + "段三";
        Assert.All(new[]
        {
            thisAssignment, nextAssignment, nextAssignmentShort, thisPeriod, currentPeriod,
            numberedPeriod, numberedPeriodAtEnd,
        }, line =>
            Assert.Equal("開發任務與階段的中文說法", Assert.Single(DevelopmentRecords, record => record.Pattern.IsMatch(line)).Name));

        var pointedOut = "// 使用者指" + "出沒有看到玻璃效果，要求整個畫面帶有玻璃質感。";
        var reportedBefore = "/// 含控制字元的來源資料會讓匯出失敗（使用者回" + "報過這個問題）。";
        var reportedCase = "/// <summary>使用者回" + "報的情境：選到 Excel 正開著的檔案。</summary>";
        var raised = "// 使用者反" + "映表頭太擠，所以加寬。";
        var feedback = "// 依使用者回" + "饋調整順序。";
        var choseAfterSample = "// 不用漸層填色；看過樣" + "張後選了照樣套用。";
        Assert.All(new[] { pointedOut, reportedBefore, reportedCase, raised, feedback, choseAfterSample }, line =>
            Assert.Equal("意見與選擇的經過", Assert.Single(DevelopmentRecords, record => record.Pattern.IsMatch(line)).Name));
    }

    [Fact]
    public void Comments_DoNotCiteDocumentSections()
    {
        var root = TestRepositoryPaths.SolutionRoot;
        var findings = new List<string>();
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                     .Where(path => !Path.GetRelativePath(root, path)
                         .Split(Path.DirectorySeparatorChar)
                         .Any(part => part is "bin" or "obj"))
                     .Order(StringComparer.Ordinal))
        {
            scanned++;
            foreach (var (index, comment) in CommentLines(Path.GetExtension(file), File.ReadAllLines(file)))
            {
                foreach (var (name, pattern) in DocumentSections)
                {
                    if (pattern.IsMatch(comment))
                    {
                        findings.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{index + 1}: {name}");
                    }
                }
            }
        }

        Assert.True(scanned > 500, $"只掃到 {scanned} 個檔，方案資料夾的位置可能不對。");
        Assert.True(findings.Count == 0,
            "註解不引用文件的章節：" + Environment.NewLine + string.Join(Environment.NewLine, findings.Take(40)));
    }

    [Theory]
    [InlineData("// 內建 id 依 ECMA-376 \u00A718.8.30 登錄表凍結。")]
    [InlineData("// 檔案 store 只負責暫存改名與 manifest，沒有清理功能。")]
    [InlineData("// 表頭寫短表頭「項次」，分錄列只寫數字。")]
    [InlineData("// 規格手算的小型固定資料集。")]
    [InlineData("// 假後端都經過 action-contract.cjs 核對資料形狀。")]
    [InlineData("var basis = \"docs/jet-gui" + "de.md \u00A72\";")]
    [InlineData("var help = \"https://example.invalid/jet-gui" + "de\";")]
    [InlineData("// 第 4 步的第 2 列與第 3 項照舊顯示。")]
    [InlineData("// 第一步建立案件，第六步匯出工作底稿。")]
    [InlineData("// 第 1 組第 2 條的欄位已不存在時，整組標成待修正。")]
    [InlineData("// 展開第 2 節點時才載入下層分類。")]
    [InlineData("title.textContent = '第 4 步：資料驗證與測試';")]
    public void DocumentSectionPatternsLeaveOrdinaryCommentsAndStringsAlone(string line)
    {
        var comment = CommentPart(line);
        Assert.True(comment is null || !DocumentSections.Any(record => record.Pattern.IsMatch(comment)), line);
    }

    [Fact]
    public void DocumentSectionPatternsRecogniseEachWayOfCitingASection()
    {
        var sectionSign = "// 貸方錨定（gui" + "de \u00A7" + "6.1 C）。";
        var bareSectionSign = "/// 本期母體(\u00A7" + "2:gl_date 在會計期間內)。";
        var guideName = "// jet-gui" + "de section 7: the saved creator is kept.";
        var guideNumber = "// gui" + "de 3.1.5 的規則：有資料的欄不丟棄。";
        var masterSpec = "/// master sp" + "ec「匯出進度契約」：五個正式 action。";
        var specItem = "// baseline 不觸發任何規則(sp" + "ec C.3 證明義務)。";
        var descriptionLines = "// F" + "D 315-319: reimported sources keep their identity.";
        var contractFile = "// action-contract-mani" + "fest: the response keeps the field id.";
        var manifestSection = "/// import.progress 事件（mani" + "fest「Host→Web 事件」章節）。";
        var descriptionSection = "/// 前端說" + "明「案件選擇」：兩種診斷匯出仍可使用。";
        var blockComment = "/* ---- 來源必填（gui" + "de \u00A7" + "6.1）---- */";
        var trailingComment = "var x = 1; // 見 sp" + "ec \u00A7" + "1";
        Assert.All(new[]
        {
            sectionSign, bareSectionSign, guideName, guideNumber, masterSpec, specItem,
            descriptionLines, contractFile, manifestSection, descriptionSection, blockComment, trailingComment,
        }, line =>
        {
            var comment = CommentPart(line);
            Assert.NotNull(comment);
            Assert.Equal("文件章節的寫法", Assert.Single(DocumentSections, record => record.Pattern.IsMatch(comment)).Name);
        });

        var chineseNumeral = "// 第九" + "節：每條條件只表達一件事。";
        var decimalNumber = "// ── 修正後要成立的情境（設計第 5.2 " + "節）──";
        var withItem = "// 設計第 5.1 " + "節第 1 到 5 項。";
        var chapter = "/// 規則見第 3 " + "章。";
        var noSpaces = "/* 第12" + "節的欄位順序 */";
        Assert.All(new[] { chineseNumeral, decimalNumber, withItem, chapter, noSpaces }, line =>
        {
            var comment = CommentPart(line);
            Assert.NotNull(comment);
            Assert.Equal("中文寫的章節引用", Assert.Single(DocumentSections, record => record.Pattern.IsMatch(comment)).Name);
        });
    }

    [Fact]
    public void CommentLines_RecogniseHashCommentsAndCommentsThatSpanLines()
    {
        // .editorconfig：井字號或分號開頭的整行是註解，區段名稱與設定值不是。
        Assert.Equal(
            new[] { (0, "# 專案設定"), (2, "; 另一種註解") },
            CommentLines(".editorconfig", ["# 專案設定", "[*.cs]", "  ; 另一種註解", "indent_size = 4"]).ToArray());

        // 其他檔案的井字號不是註解：C# 的前置處理指示詞與 CSS 的 id 選擇器。
        Assert.Empty(CommentLines(".cs", ["#region 匯入", "#endregion"]));
        Assert.Empty(CommentLines(".css", ["#app-root { color: red; }"]));

        // 跨行的 XML 註解：第二行沒有註解記號也算註解，結束之後的元素不算。
        Assert.Equal(
            new[] { 0, 1 },
            CommentLines(".props", ["  <!-- 建置時產生文件檔，", "       只留在建置輸出。 -->", "  <PropertyGroup>"])
                .Select(item => item.Index).ToArray());

        // 跨行的區塊註解：中間各行沒有星號也算；結束記號之後的程式不算，同一行後面的單行註解照舊認得。
        var block = CommentLines(".js", ["/* 完成狀態：", "   只在這一步裡切換。", "*/ render(); // 重畫", "var done = true;"]).ToArray();
        Assert.Equal(new[] { 0, 1, 2 }, block.Select(item => item.Index).ToArray());
        Assert.Equal("   只在這一步裡切換。", block[1].Comment);
        Assert.EndsWith("// 重畫", block[2].Comment, StringComparison.Ordinal);
        Assert.DoesNotContain("render", block[2].Comment, StringComparison.Ordinal);

        // 開頭記號不在行首時不往下延續：字串裡的萬用字元樣式不會把下一行的程式當成註解。
        Assert.Equal(
            new[] { 0 },
            CommentLines(".cs", ["var pattern = \"**/*.cs\";", "var basis = \"文件出處\";"]).Select(item => item.Index).ToArray());
    }

    [Fact]
    public void DocumentSectionCheck_CoversHashCommentsAndCommentsThatSpanLines()
    {
        var hashComment = "# 四層依 gui" + "de §" + "14 分進子資料夾。";
        var secondLine = "     依 sp" + "ec 3.2.1 的順序載入。 -->";
        Assert.Contains(CommentLines(".editorconfig", [hashComment]),
            item => DocumentSections.Any(record => record.Pattern.IsMatch(item.Comment)));
        Assert.Contains(CommentLines(".targets", ["<!-- 載入順序：", secondLine]),
            item => item.Index == 1 && DocumentSections.Any(record => record.Pattern.IsMatch(item.Comment)));
    }
}
