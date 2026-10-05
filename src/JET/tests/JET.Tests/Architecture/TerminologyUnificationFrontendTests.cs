using System.Text;
using System.Text.Json;
using JET.Application;
using JET.Domain;
using Xunit;

namespace JET.Tests.Architecture;

/// <summary>
/// 2026-10-03 跨前後端用語統一：畫面、條件讀回與系統端訊息使用同一組用語，只改顯示文字，
/// 不改 action、識別字、wire 欄位或資料庫內容。每項鎖住新字樣，並確認舊字樣不再出現在畫面程式。
/// 程式註解不算畫面文字；檢查前先移除註解，只看字串與程式本體。
/// </summary>
public sealed class TerminologyUnificationFrontendTests
{
    [Fact]
    public void T1_TestScope_ReplacesPopulationWordingOnScreenAndInReadback()
    {
        // 使用者 2026-09-22：「我覺得你可以重新考慮有沒有測試母體以外的名詞，這不管怎麼聽都不太對勁」。
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        Assert.Contains("label: '分錄測試範圍內編製人員分錄筆數 ≤'", filter, StringComparison.Ordinal);
        Assert.Contains("label: '分錄測試範圍內科目分錄筆數 ≤'", filter, StringComparison.Ordinal);
        Assert.Contains("在本情境的分錄測試範圍內，依匯入後的科目或人員分組；空白欄位不列入。", filter, StringComparison.Ordinal);
        Assert.Contains("統計以分錄測試範圍為準。", ReadFrontend("js", "filter-legacy.js"), StringComparison.Ordinal);
        Assert.Contains("了解查核期間分錄分布與少用科目", ReadFrontend("js", "steps", "validate-step.js"), StringComparison.Ordinal);
        Assert.Contains("目前焦點與分錄測試範圍摘要", ReadFrontend("index.html"), StringComparison.Ordinal);

        const string guidance = "預篩選呈現查核期間分錄的分布與符合條件的分錄，是否需進一步查核由審計員判斷。";
        Assert.Contains("overviewGuidance: '" + guidance + "'", ReadFrontend("js", "ui-core.js"), StringComparison.Ordinal);
        Assert.Contains("\"" + guidance + "\"", ReadSource("AuditCore", "PrescreenPositioningRenderer.cs"), StringComparison.Ordinal);

        Assert.Equal("分錄測試範圍內編製人員分錄筆數 ≤ 11",
            RenderAtom("""{"join":"AND","type":"customPreparerEntryCount","maxEntries":"11"}"""));
        Assert.Equal("分錄測試範圍內科目分錄筆數 ≤ 11",
            RenderAtom("""{"join":"AND","type":"customAccountEntryCount","maxEntries":"11"}"""));

        var parser = StripComments(ReadSource("Application", "Support", "FilterPopulationScopeParser.cs"), js: false);
        Assert.Contains("已儲存的篩選情境和目前版本不一致，請到第五步重新儲存篩選情境。", parser, StringComparison.Ordinal);
        Assert.DoesNotContain("母體", parser, StringComparison.Ordinal);
    }

    [Fact]
    public void T2_RelevantDataElementField_ReplacesExtraFieldWording()
    {
        var core = ReadFrontend("js", "ui-core.js");
        Assert.Contains("quickLabel: '攸關資料元素欄位條件'", core, StringComparison.Ordinal);
        Assert.Contains("(item.extra ? '（攸關資料元素欄位）' : '')", ReadFrontend("js", "filter-values.js"), StringComparison.Ordinal);
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        Assert.Contains("請選已配對的文字型攸關資料元素欄位", filter, StringComparison.Ordinal);
        Assert.Contains("'此攸關資料元素欄位條件沿用原設定：不納入空白值。'", filter, StringComparison.Ordinal);
        Assert.Contains("'攸關資料元素欄位的值清單每組最多 '", filter, StringComparison.Ordinal);

        var registry = StripComments(ReadSource("Application", "Support", "ResultPageColumnRegistry.cs"), js: false);
        Assert.Contains("攸關資料元素欄位的配對設定不完整", registry, StringComparison.Ordinal);
        Assert.Contains("攸關資料元素欄位資料與目前配對設定不一致", registry, StringComparison.Ordinal);
        Assert.DoesNotContain("自訂欄位", registry, StringComparison.Ordinal);
        Assert.DoesNotContain("運算子", registry, StringComparison.Ordinal);
    }

    [Fact]
    public void T5_VoucherCount_SaysSameNumberCountsOnce_OnBothSides()
    {
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        Assert.Contains("['vouchers', '傳票張數（同號只算一張）']", filter, StringComparison.Ordinal);
        Assert.Contains("(rule.countUnit === 'vouchers' ? '傳票張數（同號只算一張）' : '分錄筆數')", filter, StringComparison.Ordinal);
        Assert.Contains("要計張時改選傳票張數（同號只算一張）。", ReadFrontend("js", "filter-legacy.js"), StringComparison.Ordinal);
        Assert.Contains("'傳票 ' + fmtNum(item.hitVouchers) + ' 張（同號只算一張）'", ReadFrontend("js", "overview-bi.js"), StringComparison.Ordinal);

        Assert.Equal("分錄測試範圍內「會計科目編號」傳票張數（同號只算一張） 介於 1～2（含端點）",
            RenderAtom("""{"join":"AND","type":"entityFrequency","field":"accNum","countUnit":"vouchers","countOperator":"between","countFrom":"1","countTo":"2"}"""));
        Assert.Contains("請選擇分錄筆數或傳票張數（同號只算一張）。",
            ReadSource("Domain", "Rules", "EntityFrequencyConditions.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void T6_TrailingDigits_ReadsAsIntegerPartWithoutDecimals_OnBothSides()
    {
        Assert.Contains("return prefix + '金額整數部分（不含小數）' + tdAtoms.join(' 或 ');",
            ReadFrontend("js", "steps", "filter-step.js"), StringComparison.Ordinal);
        // L59 fixes the six-zero wording and shares the fourth/fifth-step explanation without changing tails.
        // First failure: 20261004-092023464-13b0a6928d5e400492fbe8a6a24eb69d.
        Assert.Contains("desc: Ui.prescreenConditionDescription('trailingZeros')",
            ReadFrontend("js", "steps", "validate-step.js"), StringComparison.Ordinal);
        // 第9批僅集中門檻，完整句不變；原source literal斷言在插值處截斷。
        // 首次失敗：20261004-105344715-0a3f9684995a4a20bcdcb2fb954649f6。
        Assert.Equal("依本次測試的分錄，找出金額整數部分尾數連續 6 個 0 的分錄；不含小數，整數部分為 0 不列入。",
            FrontendConstantTextReader.ObjectString(ReadFrontend("js", "ui-core.js"), "PRESCREEN_DESCRIPTIONS", "trailingZeros"));
        Assert.Equal("金額整數部分（不含小數）末 6 位 = 000000 或 末 2 位 = 99",
            RenderAtom("""{"join":"AND","type":"trailingDigits","keywords":"000000,99"}"""));
    }

    [Fact]
    public void W10_SaveWording_UsesStoreVerb_AndBlankSummaryIsOneName()
    {
        // 使用者裁定以「已儲存」為準；KCT G 卡片與空值項目已經叫「空白摘要」。
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        Assert.Contains("'已儲存「' + Ui.esc(draft.name) + '」。' : '條件或說明已變更，尚未儲存。'", filter, StringComparison.Ordinal);
        Assert.Contains("(editing ? '儲存變更' : '儲存情境')", filter, StringComparison.Ordinal);
        Assert.Contains("paneButton('saved', '已儲存情境與矩陣（'", filter, StringComparison.Ordinal);
        Assert.Contains("'配對已修改，請按「重新確認配對」儲存。'", ReadFrontend("js", "steps", "mapping-step.js"), StringComparison.Ordinal);
        Assert.Contains("'已儲存 ' + n + ' 個篩選情境' : '設定並儲存篩選情境'", ReadFrontend("js", "app.js"), StringComparison.Ordinal);
        Assert.Contains("條件儲存時間：", ReadFrontend("js", "steps", "export-step.js"), StringComparison.Ordinal);

        Assert.Equal("空白摘要", FilterConditionLabels.PrescreenKeys[PrescreenRuleKeys.BlankDescription]);
        Assert.Contains("{ value: 'blankDescription', label: '空白摘要' }", ReadFrontend("js", "ui-core.js"), StringComparison.Ordinal);
        Assert.Equal("預篩選：空白摘要",
            RenderAtom("""{"join":"AND","type":"prescreen","prescreenKey":"blankDescription"}"""));

        // 桌面情境檢查跟著產品字樣改；舊字樣留在檢查裡會讓情境永遠找不到提示。
        var guiScenarios = ReadRepoFile("tools", "harness", "gui-driver", "GuiScenarios.cs");
        Assert.Contains("配對已修改，請按「重新確認配對」儲存。", guiScenarios, StringComparison.Ordinal);
        var guiFilter = StripComments(ReadRepoFile("tools", "harness", "gui-driver", "GuiFilterWorkflowScenarios.cs"), js: false);
        Assert.Contains(".Contains(\"已儲存\", StringComparison.Ordinal)", guiFilter, StringComparison.Ordinal);
        Assert.Contains("textContent.includes('已儲存')", guiFilter, StringComparison.Ordinal);
        Assert.DoesNotContain("已保存", guiFilter, StringComparison.Ordinal);
    }

    [Fact]
    public void S9_SummariesSeparatorsStageNumbersAndFileSize_FollowEarlierUserRulings()
    {
        var app = ReadFrontend("js", "app.js");
        // 使用者 2026-09-21：「把符號 "、" 改成 "/" 就好」。
        Assert.Contains("return seg.length ? seg.join(' / ') : '匯入總帳明細與試算表';", app, StringComparison.Ordinal);
        // L24/Q8 now states GL and TB separately; the user's slash separator remains required.
        // First failure: 20261004-091831760-a762b9ef96db4d61a4bb2bff7cdb0022.
        var mappingStart = app.IndexOf("if (id === 'mapping')", StringComparison.Ordinal);
        var mappingEnd = app.IndexOf("if (id === 'validate')", mappingStart + 1, StringComparison.Ordinal);
        Assert.True(mappingStart >= 0 && mappingEnd > mappingStart);
        var mappingSummary = app[mappingStart..mappingEnd];
        Assert.Contains("return ['gl', 'tb'].map(function (kind)", mappingSummary, StringComparison.Ordinal);
        Assert.Contains("if (state.mapping[kind].committed) { return label + ' 已確認配對'; }", mappingSummary, StringComparison.Ordinal);
        Assert.Contains("return label + (imp[kind] ? ' 待確認配對' : ' 尚未匯入');", mappingSummary, StringComparison.Ordinal);
        Assert.Contains("}).join(' / ');", mappingSummary, StringComparison.Ordinal);
        // 使用者 2026-09-21：不需要強調 01、02、03 這樣的數字；總覽階段卡跟中央流程列一致。
        Assert.DoesNotContain("overview-stage__num", app, StringComparison.Ordinal);
        Assert.DoesNotContain("pad2(", app, StringComparison.Ordinal);
        Assert.DoesNotContain(".overview-stage__num", ReadFrontend("css", "app.css"), StringComparison.Ordinal);
        // 使用者 2026-09-22：「位元組直接改成顯示 MB 就好」；與報告清單共用同一個格式化函式。
        Assert.Contains("Ui.fileSizeMbText(workpaper.bytes)", app, StringComparison.Ordinal);
        Assert.DoesNotContain("位元組", StripComments(app, js: true), StringComparison.Ordinal);
        var core = ReadFrontend("js", "ui-core.js");
        Assert.Contains("function fileSizeMbText(value)", core, StringComparison.Ordinal);
        Assert.Contains("var bytesText = fileSizeMbText(artifact.bytes);", core, StringComparison.Ordinal);
        Assert.Contains("fileSizeMbText: fileSizeMbText", core, StringComparison.Ordinal);
        // 使用者 2026-09-21：「・」這類符號是 AI 味；匯入徽章改用括號放時間。
        var import = ReadFrontend("js", "steps", "import-step.js");
        Assert.Contains("+ label + '（' +", import, StringComparison.Ordinal);
        Assert.DoesNotContain("label + '·'", import, StringComparison.Ordinal);

        // 條件讀回與畫面一致：借方與貸方用逗號分開，收入條件寫完整句子。
        // 2026-10-04 C4：舊條件未指定 categorySelection 時明說相同分類用途，保留完整固定預期。
        // 第一次失敗收據：20261004-075340533-87179f5019724ec89d7c19b4243fdb09。
        Assert.Equal("相同分類用途：借貸科目組合：借方是 A 且貸方是 B（借方 Receivables，貸方 Revenue）",
            RenderAtom("""{"join":"AND","type":"accountPair","pairMode":"exact","debitCategoryIds":["builtin.receivables"],"creditCategoryIds":["builtin.revenue"]}"""));
        Assert.Equal("貸方為收入，借方非應收或預收",
            RenderAtom("""{"join":"AND","type":"revenueWithoutNormalCounterpart"}"""));
        var filter = ReadFrontend("js", "steps", "filter-step.js");
        Assert.Contains("return prefix + '貸方為收入，借方非應收或預收';", filter, StringComparison.Ordinal);
        Assert.Contains(": '借方 ' + pairDebit + '，貸方 ' + pairCredit);", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void FrontendScreenCode_NoLongerContainsRetiredWording()
    {
        // Keep the existing locked-status icon exception, plus only the exact U23 user sentence in its own file.
        var retired = new[]
        {
            "所選母體", "母體", "額外欄位", "去重傳票", "主單位整數", "已保存", "保存", "已存篩選情境", "已存情境",
            "摘要空白", "位元組", "・"
        };
        const string lockedMark = "<span class=\"toc-item__mark toc-item__mark--locked\">·</span>";
        var root = Path.Combine(RepoRoot(), "JET", "wwwroot");
        var files = Directory.GetFiles(Path.Combine(root, "js"), "*.js", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "vendor" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(files);

        var hits = new List<string>();
        foreach (var file in files)
        {
            var code = StripComments(File.ReadAllText(file), js: true).Replace(lockedMark, string.Empty, StringComparison.Ordinal);
            // U23 restores this one user-authored interpretation sentence, verified against HEAD.
            // First failure: 20261004-091831760-a762b9ef96db4d61a4bb2bff7cdb0022.
            // Do not remove the retired term globally, or exempt any other text/file containing it.
            if (Path.GetRelativePath(root, file) == Path.Combine("js", "steps", "validate-step.js"))
            {
                const string approvedSentence = "金額不符代表總帳母體可能缺漏。";
                Assert.Contains(approvedSentence, code, StringComparison.Ordinal);
                Assert.Equal(code.IndexOf(approvedSentence, StringComparison.Ordinal), code.LastIndexOf(approvedSentence, StringComparison.Ordinal));
                code = code.Replace(approvedSentence, string.Empty, StringComparison.Ordinal);
            }
            hits.AddRange(retired.Where(term => code.Contains(term, StringComparison.Ordinal))
                .Select(term => Path.GetFileName(file) + "：" + term));
            if (code.Contains('·')) hits.Add(Path.GetFileName(file) + "：·");
        }

        // 防止移除註解時誤吞整段程式：去註解後仍看得到各檔後段的畫面字樣。
        var strippedFilter = StripComments(ReadFrontend("js", "steps", "filter-step.js"), js: true);
        Assert.Contains("'範例 2 需要部門等文字型攸關資料元素欄位。", strippedFilter, StringComparison.Ordinal);
        // 2026-10-03 主線審查把這句的半形分號改成全形逗號，這裡跟著改用新字樣當檢查點
        // （第一次失敗：收據 20261003-025601604-276084fa725a4007a4587e23efcf3f08）。
        Assert.Contains("尚無可用情境，請先在上方儲存篩選情境。</p>'", strippedFilter, StringComparison.Ordinal);
        Assert.Contains("Ui.fileSizeMbText(workpaper.bytes)", StripComments(ReadFrontend("js", "app.js"), js: true), StringComparison.Ordinal);

        var html = StripHtmlComments(File.ReadAllText(Path.Combine(root, "index.html")));
        hits.AddRange(retired.Where(term => html.Contains(term, StringComparison.Ordinal)).Select(term => "index.html：" + term));

        Assert.Empty(hits);
    }

    [Fact]
    public void BackendMessages_ShownToAuditors_NoLongerContainRetiredWording()
    {
        // 系統端回給畫面或寫進條件讀回的字串。底稿範本原文不在檢查範圍：「JE測試母體」「RDE欄位」等固定文字
        // 不會被下列字樣誤判（它們不含這些字串）；只在程式錯誤時出現的「GL 有效母體」也不含這些字串。
        var retired = new[]
        {
            "所選母體", "母體分布", "測試母體版本", "去重傳票張數", "主單位整數", "額外欄位", "RDE 欄位", "摘要空白",
            "已保存", "重新保存", "沒有保存", "最多保存", "後再保存", "已存篩選情境", "・", "⚠",
            "typed 條件必須指定 fieldId", "不支援 typed 條件", "不支援的 typed text", "不支援的 typed date",
            "不支援的 typed money", "要求 revision", "自訂欄位"
        };
        var source = Path.Combine(RepoRoot(), "JET");
        var files = Directory.GetFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(files);
        Assert.Contains("\"貸方為收入，借方非應收或預收\"",
            StripComments(ReadSource("Application", "Support", "FilterConditionRenderer.cs"), js: false), StringComparison.Ordinal);

        // 刻意保留：資料表說明裡的「已保存審計資料」意思是留存，不是畫面上的儲存動作，也不回給畫面。
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            Path.Combine("AuditCore", "JetSchemaCatalog.cs") + "：已保存"
        };
        var hits = new List<string>();
        foreach (var file in files)
        {
            var code = StripComments(File.ReadAllText(file), js: false);
            hits.AddRange(retired.Where(term => code.Contains(term, StringComparison.Ordinal))
                .Select(term => Path.GetRelativePath(source, file) + "：" + term)
                .Where(hit => !allowed.Contains(hit)));
            if (code.Contains(" · ", StringComparison.Ordinal)) hits.Add(Path.GetRelativePath(source, file) + "：·");
        }

        Assert.Empty(hits);
    }

    [Fact]
    public void CommentStripper_KeepsStringsAndDropsComments()
    {
        // 自我檢查：字串內的斜線不被當成註解，註解內的字樣不被當成畫面文字。
        var js = StripComments("var a = 'http://x'; // 母體\nvar b = \"/* 不是註解 */\"; /* 保存 */ var c = `t`;", js: true);
        Assert.Contains("'http://x'", js, StringComparison.Ordinal);
        Assert.Contains("\"/* 不是註解 */\"", js, StringComparison.Ordinal);
        Assert.DoesNotContain("母體", js, StringComparison.Ordinal);
        Assert.DoesNotContain("保存", js, StringComparison.Ordinal);

        var cs = StripComments("var a = @\"c:\\\\x\"\"//\"; /// 母體\nvar b = $\"{F(\"y\")}//z\"; var c = \"\"\"\n// 保存\n\"\"\";", js: false);
        Assert.Contains("//z", cs, StringComparison.Ordinal);
        Assert.Contains("// 保存", cs, StringComparison.Ordinal);
        Assert.DoesNotContain("母體", cs, StringComparison.Ordinal);
    }

    private static string RenderAtom(string ruleJson)
    {
        using var document = JsonDocument.Parse($$"""{"groups":[{"join":"AND","rules":[{{ruleJson}}]}]}""");
        return FilterConditionRenderer.Render(document.RootElement);
    }

    /// <summary>
    /// 移除 JavaScript 或 C# 的註解，保留字串內容。處理單引號、雙引號、樣板字串、C# 逐字字串與原始字串；
    /// JavaScript 的正規表示式字面值以前一個有效字元判斷，避免把其中的引號當成字串開頭。
    /// </summary>
    internal static string StripComments(string source, bool js)
    {
        var output = new StringBuilder(source.Length);
        var previous = '\0';
        var index = 0;
        while (index < source.Length)
        {
            var c = source[index];
            var next = index + 1 < source.Length ? source[index + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (index < source.Length && source[index] != '\n') index++;
                continue;
            }

            if (c == '/' && next == '*')
            {
                var end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = end < 0 ? source.Length : end + 2;
                output.Append(' ');
                continue;
            }

            if (js && c == '/' && "(,=:[!&|?{};+-*%<>~^".Contains(previous) || js && c == '/' && previous == '\0')
            {
                var start = index++;
                var inClass = false;
                while (index < source.Length && source[index] != '\n')
                {
                    var r = source[index];
                    if (r == '\\') { index += 2; continue; }
                    if (r == '[') inClass = true;
                    else if (r == ']') inClass = false;
                    else if (r == '/' && !inClass) { index++; break; }
                    index++;
                }

                output.Append(source, start, Math.Min(index, source.Length) - start);
                previous = 'r';
                continue;
            }

            if (!js && c == '"' && next == '"' && index + 2 < source.Length && source[index + 2] == '"')
            {
                var quotes = 0;
                while (index + quotes < source.Length && source[index + quotes] == '"') quotes++;
                var fence = new string('"', quotes);
                var end = source.IndexOf(fence, index + quotes, StringComparison.Ordinal);
                var stop = end < 0 ? source.Length : end + quotes;
                output.Append(source, index, stop - index);
                index = stop;
                previous = '"';
                continue;
            }

            if (!js && (c == '@' && next == '"' || c == '@' && next == '$' && index + 2 < source.Length && source[index + 2] == '"'))
            {
                var start = index;
                index = source.IndexOf('"', index) + 1;
                while (index < source.Length)
                {
                    if (source[index] == '"' && index + 1 < source.Length && source[index + 1] == '"') { index += 2; continue; }
                    if (source[index] == '"') { index++; break; }
                    index++;
                }

                output.Append(source, start, index - start);
                previous = '"';
                continue;
            }

            if (c == '"' || c == '\'' || js && c == '`')
            {
                var start = index++;
                while (index < source.Length && source[index] != c)
                {
                    if (source[index] == '\\') { index += 2; continue; }
                    if (!js && source[index] == '\n') break;
                    index++;
                }

                index = Math.Min(index + 1, source.Length);
                output.Append(source, start, index - start);
                previous = c;
                continue;
            }

            output.Append(c);
            if (!char.IsWhiteSpace(c)) previous = c;
            index++;
        }

        return output.ToString();
    }

    private static string StripHtmlComments(string html)
    {
        var output = new StringBuilder(html.Length);
        var index = 0;
        while (index < html.Length)
        {
            var start = html.IndexOf("<!--", index, StringComparison.Ordinal);
            if (start < 0) { output.Append(html, index, html.Length - index); break; }
            output.Append(html, index, start - index);
            var end = html.IndexOf("-->", start + 4, StringComparison.Ordinal);
            index = end < 0 ? html.Length : end + 3;
        }

        return output.ToString();
    }

    private static string ReadFrontend(params string[] segments)
        => ReadSource(new[] { "wwwroot" }.Concat(segments).ToArray());

    private static string ReadSource(params string[] segments)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot(), "JET" }.Concat(segments).ToArray()));

    private static string ReadRepoFile(params string[] segments)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot(), "..", ".." }.Concat(segments).ToArray()));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JET.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("向上尋不到 JET.slnx。");
    }
}
