using System.Text.RegularExpressions;
using JET.Tests.Infrastructure;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 原始碼的註解與文字只說明程式現在做什麼、為什麼這樣做。開發過程的紀錄另外保存，不寫進原始碼：
/// 需求決議的編號、測試執行的編號、測試修改經過的說明，以及開發計畫的階段名稱。
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
    ];

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
    }
}
