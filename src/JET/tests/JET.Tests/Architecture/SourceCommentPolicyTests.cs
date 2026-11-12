using System.Text.RegularExpressions;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 原始碼的註解與文字只說明程式現在做什麼、為什麼這樣做。開發過程的紀錄另外保存，不寫進原始碼：
/// 需求決議的編號、測試執行的編號、測試修改經過的說明、開發計畫的階段名稱，以及開發任務的用語。
/// 註解也不引用文件的章節：只拿到這個方案資料夾的人查不到那些文件。
/// </summary>
public sealed class SourceCommentPolicyTests
{
    private static readonly string[] Extensions = [".cs", ".js", ".cjs", ".css", ".html", ".csproj", ".json"];

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
    ];

    private static readonly Regex CommentStart = new(@"(?<!:)//|/\*|<!--|^\s*\*", RegexOptions.CultureInvariant);

    /// <summary>一行裡從註解記號開始的部分；沒有註解記號時回傳 null。網址的雙斜線不算。</summary>
    private static string? CommentPart(string line)
    {
        var match = CommentStart.Match(line);
        return match.Success ? line[match.Index..] : null;
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
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                if (CommentPart(lines[index]) is not { } comment)
                {
                    continue;
                }

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
    }
}
