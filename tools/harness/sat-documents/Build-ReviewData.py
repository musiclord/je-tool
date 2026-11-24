"""Generate reviewer inputs from current source; no test or approval is implied."""
from pathlib import Path
import json,re
ROOT=Path(__file__).resolve().parents[3]
SOURCE=Path(__file__).resolve().parent/'source'
def save(name,value):
 (SOURCE/name).write_text(json.dumps(value,ensure_ascii=False,indent=2)+'\n',encoding='utf-8',newline='\n')
# Core audit logic covered by manual review: the review points of each logic, the files that define its rules and the test classes that check it.
# Each review point is one rule the reviewer confirms in the code, at most three for a logic, written as plain sentences.
MOST=3
scope=[
 {'title':'納入測試的分錄範圍','points':[
  '總帳日期落在查核期間內的分錄才納入測試，總帳日期空白或無法辨識的分錄不納入。例如查核期間為 2025-01-01 至 2025-12-31 時，總帳日期為 2026-01-01 的分錄不納入。',
  '有配對過帳狀態欄位時，過帳狀態屬於指定代碼的分錄才納入測試。使用者選擇納入空白時，過帳狀態空白的分錄一併納入。',
  '每筆未納入的分錄只記錄一種原因。程式先判斷日期，日期在查核期間內才判斷過帳狀態，因此日期與過帳狀態都不符的分錄記為日期原因。'],
  'path':'AuditCore/GlExcludedPopulation.cs','tests':'GlExcludedPopulationTests、GlExcludedPopulationPartitionTests、GlExcludedPopulationProjectionProviderTests'},
 {'title':'完整性測試','points':[
  'GL 金額依會計科目編號合計，借方為正、貸方為負，只計入納入測試的分錄。',
  'GL 合計與 TB 同一科目的年度變動金額不相等時列為差異，差異金額為 TB 減 GL。例如 TB 為 1,000、GL 為 900 時，差異金額為 100。',
  '只出現在 GL 或只出現在 TB 的科目，另一方的金額以 0 計算，並列為差異。'],
  'path':'AuditCore/ValidationProcedures.cs；AuditCore/AccountComparisonProcedure.cs；AuditCore/CompletenessEligibility.cs','tests':'CompletenessEligibilityTests、WorkflowWithoutTbTests、TaiwanSmePopulationAndCompletenessTests、CompletenessAccountPageTests'},
 {'title':'借貸不平測試','points':[
  '納入測試的分錄依傳票號碼合計，借方合計與貸方合計不相等的傳票列為借貸不平。例如借方合計 1,000.00、貸方合計 999.99 的傳票會列出。',
  '同一張傳票只合計納入測試的分錄，整張傳票都未納入測試時不列出。',
  '傳票號碼空白的分錄不視為同一張傳票，也不列為借貸不平。'],
  'path':'AuditCore/ValidationProcedures.cs','tests':'UnbalancedVoucherDateRowsTests、TaiwanSmePopulationAndCompletenessTests'},
 {'title':'資料可靠性測試','points':[
  '樣本只從納入測試的分錄抽出，且不重複。',
  '抽樣筆數預設為 59 筆，使用者可於執行前指定。納入測試的分錄少於抽樣筆數時全數抽出，例如只有 6 筆時 6 筆全數抽出。',
  '同一案件的資料與抽樣筆數不變時，重新執行會抽到同一批分錄。'],
  'path':'AuditCore/ValidationProcedures.cs；AuditCore/InfSampling.cs','tests':'ConfigurableInfSamplingTests、InfSamplingPrfTests、InfSamplingPrfProviderTests'},
 {'title':'進階條件篩選','points':[
  '欄位條件、常用篩選條件與作為條件使用的預篩選規則，各自判斷分錄是否命中。「介於」的條件包含上下限，例如金額絕對值介於 1,000 至 5,000 時，金額為 -5,000 的分錄命中。',
  '以 AND 連接的條件須由同一筆分錄同時符合。AND 與 OR 混用時依畫面上的順序由左至右計算，AND 不優先。例如「金額等於 5,000 OR 借方 AND 金額大於或等於 50,000」只有金額達 50,000 的借方分錄命中。',
  '整張傳票條件選「至少一筆符合」時，傳票內只要有一筆分錄符合子條件，該傳票納入測試的分錄全部列出。選「不存在符合」時，列出沒有任何分錄符合子條件的傳票。'],
  'path':'AuditCore/FilterCompilation.cs；AuditCore/GlRulePredicates.cs；AuditCore/GlRulePredicates.Values.cs；AuditCore/GlRulePredicates.Frequency.cs；AuditCore/HitVoucherRows.cs；AuditCore/PersonIdentity.cs','tests':'FilterCompilationTests、SqliteGlRuleSqlEquivalenceTests、CommonFilterPredicateTests、FilterCompletenessTests、EntityFrequencyWorkflowTests'},
 {'title':'預篩選','points':[
  '週末過帳與假日過帳以總帳日期判斷。週末核准、假日核准與期末財報準備日起核准之分錄以傳票核准日判斷，傳票核准日空白的分錄不命中。',
  '週末的規則依每週休息日判斷，補班日為週六或週日時仍命中，例如於週六補班日入帳的分錄列入週末過帳。假日的規則只依使用者匯入的國定假日清單判斷。',
  '非授權編製人員的規則比對使用者匯入的授權編製人員名單。英文大小寫或前後空白不同仍視為同一人，傳票建立人員空白的分錄不命中。'],
  'path':'Domain/Rules/PrescreenRuleDefinitions.cs；Domain/Rules/PrescreenRules.cs；AuditCore/PrescreenRulePredicates.cs；AuditCore/GlRulePredicates.cs','tests':'PrescreenRuleFixedAnswerTests、SqliteGlRuleSqlEquivalenceTests、DemoRuleOracleTests'},
]
for row in scope:
 if not 1<=len(row['points'])<=MOST:raise RuntimeError('A logic has one to three review points: '+row['title'])
TESTS=ROOT/'src/JET/tests/JET.Tests'
test_sources=[p.read_text(encoding='utf-8-sig') for p in TESTS.rglob('*.cs') if not {'bin','obj'}&set(p.relative_to(TESTS).parts)]
for row in scope:
 for part in row['path'].split('；'):
  if not (ROOT/'src/JET/JET'/part).is_file():raise RuntimeError('Scope path missing: '+part)
 for name in row['tests'].split('、'):
  if not any(re.search(r'\bclass '+re.escape(name)+r'\b',text) for text in test_sources):raise RuntimeError('Scope test class missing: '+name)
# Review checks for each rule in sections 2 to 4 of the code review rule, grouped by section and by the rule's bold name.
# A rule that asks for several things is split into at most three checks, each with its own result and written as plain sentences.
# The script stops when the rule no longer has the section heading or the bold name.
UNIT,HARD,SUPPORT='2. Unit-Test Acceptance Rules','3. Manual Review of Hard-Coded Content','4. Supporting Review Rules'
checklist=[
 (UNIT,'Required tests',[
  '預期行為已有測試涵蓋，正常資料的結果符合需求。例如借方與貸方合計相等的傳票不列為借貸不平。',
  '邊界條件已有測試涵蓋，落在界線上的值結果符合需求。例如總帳日期為查核起始日或截止日當天的分錄納入測試。',
  '無效輸入與錯誤處理已有測試涵蓋。例如總帳日期無法辨識的分錄不納入測試並記錄原因，清單條件超過 100 個值時不予儲存並顯示原因。']),
 (UNIT,'Pass criterion',[
  '本次變更的測試與受影響的回歸測試，均以送交複核的版本執行並全數通過。']),
 (UNIT,'Test quality',[
  '測試逐項比對實際結果與預期結果，而非僅確認程式執行完成。例如比對列為借貸不平的傳票號碼與金額。',
  '預期結果依需求或已確認的查核邏輯訂定，而非取自受測程式的輸出。例如差異金額的預期值依「TB 減 GL」的規則另行計算。']),
 (UNIT,'Repeatability',[
  '測試重複執行的結果相同，使用的外部資源已受控制。例如測試使用程式產生的合成資料與暫存資料夾。',
  '必要的測試沒有略過、停用或結果不穩定的情形。例如單元測試報告的略過數為 0。']),
 (UNIT,'Coverage',[
  '變更的行為與主要的判斷分支均有測試執行。例如有配對與未配對過帳狀態的兩種情形均有測試。']),
 (UNIT,'Regression protection',[
  '缺陷修正附有重現該缺陷的測試，修正前失敗、修正後通過。',
  '沒有為了取得通過的結果而刪除、放寬或改動測試。']),
 (HARD,'Identify hard-coded content',[
  '程式內影響查核結果的固定值與規則均已列於「固定值與規則」工作表，包括門檻、日期、比率、代碼與固定的判斷邏輯。']),
 (HARD,'Validate the source',[
  '各項的來源已確認，符合需求或舊版 JET 的規則，用途與單位已載明。例如分錄摘要的預設關鍵字與舊版 JET 相同。']),
 (HARD,'Assess maintainability',[
  '會隨環境、部署或業務政策改變的值由設定或使用者輸入提供。例如查核期間、國定假日清單與授權編製人員名單由使用者填寫或匯入。',
  '保留於程式內的固定值，其意義與查核用途明確，且同一個值在各處一致。例如抽樣預設筆數 59 筆只於後端定義一次，前端顯示後端提供的值。']),
 (HARD,'Protect sensitive information',[
  '原始碼不含密碼、金鑰、token 等機密資訊。']),
 (HARD,'Verify behavior',[
  '邊界、精度與進位方式已確認。例如金額等於 0 視為借方；金額保留至小數第 4 位，第 5 位四捨五入。',
  '預設值與無法使用的值的處理方式已確認。例如來源金額空白時以 0 計算，無法解析為金額時列為錯誤。',
  '可行時以測試確認固定值的效果。例如有測試確認資料可靠性測試預設抽出 59 筆。']),
 (HARD,'Record the conclusion',[
  '「固定值與規則」每一列均已填寫複核結果（可接受、已修正或例外核准），沒有未解決的事項。']),
 (SUPPORT,'Requirements and scope',[
  '實作符合需求。例如完整性測試的差異金額為 TB 減 GL，與需求相同。',
  '沒有不相關的變更或非預期的副作用。例如修改預篩選規則時，共用同一判斷的進階條件篩選結果不受影響。']),
 (SUPPORT,'Readability and consistency',[
  '命名能對應所屬的查核邏輯，控制流程易於理解。例如未納入測試的分錄由 GlExcludedPopulation 判定。',
  '註解說明程式的行為與原因。',
  '同一項查核邏輯只定義一次，沒有不必要的重複，並符合專案的撰寫慣例。例如預篩選規則作為進階條件篩選的條件使用時，判斷方式與預篩選相同。']),
 (SUPPORT,'Error handling and security',[
  '輸入檢查適當。例如抽樣筆數為 0 或非整數時不予執行，並說明可輸入的範圍。',
  '例外處理與資源釋放適當。例如來源檔正由 Excel 開啟時，仍可匯入最後一次存檔的內容，並提示尚未存檔的修改未匯入。',
  '機密資訊的處理與相關的存取檢查適當。']),
 (SUPPORT,'Beyond unit tests',[
  '行為取決於資料庫、外部服務、部署設定或單元測試未涵蓋的互動時，已另以整合測試、系統測試或其他方式確認。例如以合成的案件依序執行驗證與篩選，並匯出報告與底稿，核對各步驟的結果。']),
 (SUPPORT,'Independent review',[
  '複核人員不是開發人員本人。',
  '風險較高的變更已完成專案要求的額外複核。']),
]
rule_text=(SOURCE/'Code review rule.md').read_text(encoding='utf-8')
for heading in (UNIT,HARD,SUPPORT):
 if '## '+heading+'\n' not in rule_text:raise RuntimeError('Rule heading changed: '+heading)
for heading,name,items in checklist:
 section=rule_text.split('## '+heading+'\n',1)[1].split('\n## ',1)[0]
 if '- **'+name+':**' not in section:raise RuntimeError('Rule name changed: '+heading+' '+name)
 if not 1<=len(items)<=MOST:raise RuntimeError('A rule has one to three checks: '+name)
# Fixed values and rules in the program that affect audit results (section 3 of the code review rule).
# (item, details, path under src/JET/JET, marker that must still be in that file)
# Each detail is one value or rule the reviewer confirms, at most three for an item and written as a full sentence:
# the value, its boundary and what happens when the value cannot be used. Two files are written as 'a；b' with their markers in the same order.
hard=[
 ('抽樣預設筆數',[
  '預設抽樣筆數為 59 筆。',
  '使用者可於執行前指定抽樣筆數，上限為 1,048,524 筆。'],
  'AuditCore/InfSampling.cs；Application/Handlers/ValidateRunHandler.cs','DefaultSampleSize；InfSampling.MaximumSampleSize'),
 ('抽樣方式',[
  '抽樣順序由案件的隨機種子序號決定。',
  '隨機種子序號於建立案件時產生，之後不再變更。資料與抽樣筆數相同時，重新執行會抽到同一批分錄。'],
  'AuditCore/ValidationProcedures.cs；Domain/ProjectDocument.cs','InfSamplingPrf；SampleSeedExclusiveUpperBound = 2147483647'),
 ('金額精度',[
  '金額保留至小數第 4 位。',
  '小數第 5 位四捨五入，負數依絕對值進位。例如 0.00005 進位為 0.0001，-0.00005 進位為 -0.0001。'],
  'Domain/ProjectDocument.cs；Domain/Primitives/MoneyScaling.cs','DefaultMoneyScale = 10_000；MidpointRounding.AwayFromZero'),
 ('金額文字',[
  '以括號括住的數字視為負數，例如 (1,000) 為 -1,000。',
  '金額空白或只有一個減號時，以 0 計算。',
  '無法解析為金額的內容列為錯誤並說明原因，例如儲存格為 #N/A。'],
  'Domain/Primitives/MoneyScaling.cs；Domain/Rules/MappedAmountCell.cs','TryParseAmount；ProjectionErrorReasons.AmountInvalid'),
 ('借貸別',[
  '金額大於或等於 0 為借方，小於 0 為貸方。'],
  'Domain/Rules/GlRowProjector.cs','amountScaled >= 0'),
 ('試算表（TB）年度變動金額',[
  '選擇「設定年度變動金額」時，直接採用該欄的值。',
  '選擇「設定借方與貸方金額」時，以借方減貸方計算。',
  '選擇「設定期初與期末金額」時，以期末減期初計算。選擇「設定借方貸方之期初期末金額」時，以（期末借方減期末貸方）減（期初借方減期初貸方）計算。'],
  'Domain/Rules/TbRowProjector.cs','TryProject'),
 ('納入測試的分錄範圍',[
  '查核起始日與截止日當天的分錄納入測試。',
  '過帳狀態的比對不分英文大小寫，並忽略前後空白。例如指定代碼為 Y 時，過帳狀態為「 y 」的分錄納入測試。'],
  'AuditCore/GlExcludedPopulation.cs','GlExclusionReason.PostingStatus'),
 ('借貸不平測試',[
  '容許誤差為 0。同一傳票號碼的分錄金額合計不為 0 即列為借貸不平，例如相差 0.0001 的傳票也會列出。'],
  'AuditCore/ValidationProcedures.cs','UnbalancedCore'),
 ('資料驗證報告的借貸淨額',[
  '借貸淨額的絕對值不超過 0.01 時，報告顯示為 0。'],
  'AuditCore/ValidationProcedures.cs','0.01m'),
 ('完整性測試的科目差異',[
  '容許誤差為 0。同一科目的 TB 年度變動金額與 GL 金額合計不相等即列為差異。'],
  'AuditCore/ValidationProcedures.cs','tb_s <> gl_s'),
 ('核准日不在期間',[
  '傳票核准日空白、早於查核起始日或晚於查核截止日的分錄列出。',
  '查核起始日與截止日當天視為期間內。'],
  'AuditCore/FilterCompilation.cs','approval_date < @periodStart OR approval_date > @periodEnd'),
 ('報告明細列數上限',[
  '資料驗證報告中，空白科目編號、空白傳票號碼、空白摘要與核准日不在期間的明細，各以 10,000 列為上限。',
  '借貸不平測試與預篩選的明細，各以 9,999 列為上限。',
  '明細超過上限時只輸出摘要，預篩選報告並於摘要註明明細資料不匯出。'],
  'AuditCore/ValidationReportProgram.cs；AuditCore/PrescreenReportProgram.cs','10_000；10_000'),
 ('期末財報準備日起核准之分錄',[
  '傳票核准日為期末財報準備日當天或之後的分錄命中。',
  '傳票核准日空白的分錄不命中。'],
  'AuditCore/GlRulePredicates.cs','PrepPeriodApproval'),
 ('分錄摘要出現特定描述',[
  '預設關鍵字共 24 個。英文為 ADJ、REV、RECLASS、SUSPENSE、ERROR、WRONG；繁體中文為調整、迴轉、沖銷、重分類、避險、重編、錯誤、計畫外、預算外；簡體中文為调整、回转、冲销、重分类、避险、重编、错误、计画外、预算外。',
  '摘要包含任一關鍵字即命中，比對不分英文大小寫。例如摘要「年底調整」包含「調整」，因此命中。',
  '預篩選固定使用預設關鍵字。進階條件篩選預先帶入預設關鍵字，使用者可增刪。'],
  'Domain/Rules/PrescreenRules.cs','SuspiciousKeywordDefaults'),
 ('未預期出現之特定借貸組合',[
  '命中的分錄為收入科目的貸方分錄。',
  '同一張傳票須有金額大於 0 的分錄，且這些分錄的科目均不屬於應收款項、現金或預收款。',
  '科目的分類依使用者匯入的科目配對判定。'],
  'AuditCore/GlRulePredicates.cs','UnexpectedAccountPair'),
 ('分錄金額中有連續 0 的尾數',[
  '連續 0 的個數依 GL 計算：取 GL 借方金額平均值的整數位數減 1，最少 1 個、最多 12 個。例如平均值為 12,345.67 時為 4 個。',
  '金額整數部分的尾數連續 0 達到上述個數即命中，不分正負。'],
  'Domain/Rules/PrescreenRules.cs','totals.GlDebitScaled / moneyScale / totals.GlRowCount'),
 ('非營業日',[
  '每週休息日預設為週六與週日。',
  '非營業日為每週休息日與使用者匯入的國定假日。',
  '使用者匯入的補班日不視為非營業日。'],
  'Domain/Primitives/NonWorkingDays.cs；AuditCore/GlRulePredicates.Values.cs','= [0, 6]；AND NOT ('),
 ('季底',[
  '季底為 3 月 31 日、6 月 30 日、9 月 30 日與 12 月 31 日。',
  '前推天數可設定為 1 至 92 天，含季底當天。例如前推 3 天時，12 月 29 日至 31 日屬於季底期間。',
  '查核截止日也以相同的前推天數計算。例如查核截止日為 11 月 30 日、前推 3 天時，11 月 28 日至 30 日屬於季底期間。'],
  'Domain/Rules/QuarterEndWindows.cs','MaxWindowDays = 92'),
 ('篩選上限',[
  '每個清單條件最多 100 個值，超過時不予儲存並顯示原因。',
  '每個案件最多儲存 10 個篩選情境，超過時不予儲存並顯示原因。'],
  'Domain/Rules/FilterScenario.cs','FilterScenarioLimits'),
 ('日期換算',[
  '西元與民國年的日期寫法均換算為西元日期，民國年加 1911。例如 2025/6/11、20250611、114/6/11 與 1140611 均為 2025-06-11。',
  '只有年份、缺少日或月日不存在的寫法視為無法辨識，例如「2025」「2025.12」「114/13/1」。'],
  'Domain/Primitives/DateNormalizer.cs','1911'),
]
hard_rows=[]
for number,(item,details,path,marker) in enumerate(hard,1):
 if not 1<=len(details)<=MOST:raise RuntimeError('A fixed value has one to three details: '+item)
 for part,mark in zip(path.split('；'),marker.split('；'),strict=True):
  if mark not in (ROOT/'src/JET/JET'/part).read_text(encoding='utf-8-sig'):raise RuntimeError('Hard-coded marker missing: '+part+' '+mark)
 hard_rows.append({'id':number,'item':item,'details':details,'path':path})
# Test cases of the six core audit logics. The case file under the test project is the only definition of the test data
# and the expected results: AuditLogicCaseTests runs every case against the product.
# The unit test report does not list the cases one by one. It lists the test items below, each written here in our own words:
# what is tested, the input conditions and the expected result. An item names the cases that check it, by group or by case id,
# and the report shows how many of them there are and whether they all passed.
import hashlib
CASE_FILE,CASE_CLASS,CASE_METHOD='Fixtures/audit-logic-cases.json','AuditLogicCaseTests','Case_ProducesTheExpectedResult'
# The items are listed in the order the user meets the logics in the product: validation first, then prescreening, then filtering.
CASE_ORDER=['納入測試的分錄範圍','完整性測試','借貸不平測試','資料可靠性測試','預篩選','進階條件篩選']
# (logic, item, input conditions, expected result, groups of the case file, case ids)
test_items=[
 ('納入測試的分錄範圍','查核期間',
  '總帳日期為查核起始日前一天、起始日當天、截止日當天與截止日後一天的分錄，以及總帳日期空白或無法辨識的分錄。',
  '起始日與截止日當天的分錄納入測試。期間外、空白與無法辨識的分錄不納入，並各自記錄原因。',
  ['查核期間'],[]),
 ('納入測試的分錄範圍','過帳狀態',
  '已配對過帳狀態欄位。分錄的過帳狀態分別為指定的代碼、其他代碼與空白，另有日期與過帳狀態都不符的分錄。',
  '過帳狀態為指定代碼的分錄納入測試；選擇納入空白時，空白的分錄一併納入。日期與過帳狀態都不符的分錄記為日期原因。',
  ['過帳狀態'],[]),
 ('納入測試的分錄範圍','日期寫法',
  '同一個日期以西元與民國年的不同寫法表示，另有純數字、缺年份或缺日的寫法。',
  '西元與民國年的寫法換算為同一個西元日期，分錄納入測試。純數字、缺年份或缺日的寫法視為無法辨識，分錄不納入。',
  ['日期寫法'],[]),
 ('納入測試的分錄範圍','核准日不在期間',
  '納入測試的分錄中，傳票核准日分別為空白、早於查核起始日、晚於查核截止日，以及在起始日或截止日當天。',
  '傳票核准日空白或在查核期間外的分錄列出，起始日與截止日當天的分錄不列出。',
  ['資料驗證的輔助檢視'],[]),
 ('完整性測試','科目差異',
  'GL 與 TB 各科目金額相符的資料、GL 缺少一個月分錄的資料、科目只出現在 GL 或只出現在 TB 的資料，以及含有未納入測試分錄的資料。',
  '金額相符時沒有差異科目。GL 合計與 TB 年度變動金額不相等的科目列為差異，差異金額為 TB 減 GL。只出現在一方的科目，另一方以 0 計算。未納入測試的分錄不計入 GL 合計。',
  ['科目差異'],[]),
 ('完整性測試','TB 年度變動金額',
  'TB 分別以借方與貸方金額、期初與期末金額、期初與期末各自的借方與貸方金額提供。',
  '年度變動金額依序以借方減貸方、期末減期初、（期末借方減期末貸方）減（期初借方減期初貸方）計算，與 GL 比較後沒有差異科目。',
  ['TB 年度變動金額的算法'],[]),
 ('借貸不平測試','傳票的借貸合計',
  '借貸相等的傳票、借貸不相等的傳票、借貸相差 0.01 的傳票、傳票號碼空白的分錄，以及整張或部分分錄未納入測試的傳票。',
  '借方合計與貸方合計不相等的傳票列為借貸不平，相差 0.01 的傳票也列出。傳票號碼空白的分錄不列出。未納入測試的分錄不計入合計，整張未納入測試的傳票不列出。',
  ['傳票的借貸合計'],[]),
 ('借貸不平測試','金額精度',
  '金額帶有小數第 5 位的分錄。',
  '金額四捨五入至小數第 4 位後合計。進位後借貸相等的傳票不列出，相差 0.0001 的傳票列出。',
  ['金額精度'],[]),
 ('資料可靠性測試','預設抽樣筆數',
  '未指定抽樣筆數。資料中有 6 筆納入測試的分錄，另有未納入測試的分錄。',
  '抽樣筆數為預設的 59 筆。6 筆納入測試的分錄全數抽出，未納入測試的分錄不在樣本內。',
  [],['INF-01']),
 ('資料可靠性測試','指定抽樣筆數與重新執行',
  '指定抽樣筆數，並以相同的資料重新執行。',
  '抽出指定的筆數，樣本不重複，且均為納入測試的分錄。重新執行時抽到同一批分錄。',
  [],['INF-02']),
 ('預篩選','依日期判斷的規則',
  '總帳日期或傳票核准日落在週末、國定假日、週六的補班日，以及期末財報準備日前後的分錄，另有傳票核准日空白的分錄。',
  '週末過帳與假日過帳依總帳日期判斷，週末核准與假日核准依傳票核准日判斷。補班日為週六或週日時仍列入週末的規則。傳票核准日在期末財報準備日當天或之後的分錄列入「期末財報準備日起核准之分錄」。傳票核准日空白的分錄不列入依傳票核准日判斷的規則。',
  [],['PRE-01','PRE-05','PRE-06','PRE-08','PRE-09']),
 ('預篩選','依摘要判斷的規則',
  '摘要含預設關鍵字、不含預設關鍵字，以及摘要空白或只有空白字元的分錄。',
  '摘要含任一預設關鍵字的分錄列入「分錄摘要出現特定描述」，比對不分英文大小寫。摘要空白或只有空白字元的分錄列入「空白摘要」。',
  [],['PRE-02','PRE-07']),
 ('預篩選','未預期出現之特定借貸組合',
  '收入科目的貸方分錄，同一張傳票的借方科目分別為應收款項、現金、預收款與其他科目。',
  '借方科目均不屬於應收款項、現金或預收款的傳票命中，並列出該傳票的全部分錄。',
  [],['PRE-03']),
 ('預篩選','分錄金額中有連續 0 的尾數',
  '金額整數部分的尾數帶有不同個數連續 0 的分錄。',
  '連續 0 的個數門檻取 GL 借方金額平均值的整數位數減 1。尾數連續 0 達到門檻的分錄命中。',
  [],['PRE-04']),
 ('預篩選','非授權編製人員',
  '匯入授權編製人員名單。分錄的傳票建立人員分別在名單內、不在名單內與空白。',
  '傳票建立人員不在名單內的分錄命中。比對不分英文大小寫，並忽略前後空白。傳票建立人員空白的分錄不命中。',
  [],['PRE-10']),
 ('預篩選','彙總',
  '由多位傳票建立人員編製、涉及多個會計科目的分錄，含傳票建立人員空白的分錄。',
  '依傳票建立人員與依會計科目編號，分別列出分錄筆數、借方金額合計與貸方金額合計。傳票建立人員空白的分錄另列一組。',
  ['彙總'],[]),
 ('進階條件篩選','日期條件',
  '對總帳日期與傳票核准日分別設定指定日期、日期區間、早於或晚於指定日期、每月的指定日、月初與月底的天數、查核期末的天數，以及週末、國定假日與補班日。每種條件各有符合與排除兩種設定。',
  '符合條件的分錄命中，排除的設定命中其餘的分錄。日期區間包含起日與迄日。傳票核准日空白的分錄只在勾選空白也列入時命中。',
  ['總帳日期','傳票核准日'],[]),
 ('進階條件篩選','金額條件',
  '對金額設定等於、不等於、大於、小於、介於、尾數為指定數字、尾數連續 0 的個數，以及整數部分包含指定數字。',
  '金額取絕對值後比較，借方與貸方分錄均適用。「介於」包含上下限。尾數與包含數字的條件只比較整數部分。',
  ['金額'],[]),
 ('進階條件篩選','文字條件',
  '對傳票摘要、傳票號碼、會計科目編號、會計科目名稱、傳票建立人員、傳票核准人員、分錄來源模組與修改人員，分別設定包含、不包含、等於、不等於、開頭為與結尾為指定文字。',
  '比對不分英文大小寫，並忽略前後空白。欄位空白的分錄在包含、等於、開頭為與結尾為的條件下不命中；在不包含與不等於的條件下，勾選空白也列入時才命中。',
  ['傳票摘要','傳票建立人員','會計科目編號','會計科目名稱','傳票核准人員','傳票號碼','分錄來源模組','修改人員'],[]),
 ('進階條件篩選','借貸別、人工或自動分錄與科目分類',
  '指定借方或貸方；指定人工或自動分錄，並搭配不同的人工與自動代碼設定；指定科目分類為收入。',
  '金額大於或等於 0 的分錄為借方，小於 0 為貸方。人工與自動分錄依欄位配對時指定的代碼與空白的處理方式判定。科目分類依使用者匯入的科目配對判定，借方與貸方分錄均命中。',
  ['借貸別','人工/自動分錄','科目分類'],[]),
 ('進階條件篩選','常用篩選條件',
  '季底或查核截止日前數日借記收入的分錄、指定的借貸科目組合、貸方為收入而借方非一般對方科目、收入之人工分錄、編製人員與核准人員相同、非營業日之分錄與空白摘要，各以對應的資料執行。',
  '各項條件依其定義命中分錄。季底期間包含季底與查核截止日當天。非營業日為每週休息日與國定假日，補班日除外。編製人員與核准人員任一空白時不命中。',
  ['常用篩選條件'],[]),
 ('進階條件篩選','以預篩選規則作為條件',
  '以預篩選規則「週末過帳」與「未預期出現之特定借貸組合」作為篩選條件。',
  '命中的分錄與預篩選的判斷相同。',
  ['預篩選條件'],[]),
 ('進階條件篩選','條件組合與整張傳票條件',
  '以 AND 連接兩個條件；AND 與 OR 混用三個條件；整張傳票條件分別選「至少一筆符合」與「不存在符合」。',
  'AND 連接的條件須由同一筆分錄同時符合。AND 與 OR 混用時由左至右計算。「至少一筆符合」列出含有符合分錄的傳票的全部分錄，「不存在符合」列出沒有符合分錄的傳票的全部分錄。',
  ['條件組合','整張傳票條件'],[]),
 ('進階條件篩選','科目與人員統計',
  '指定分錄筆數小於或等於 1 的會計科目。',
  '先統計各會計科目在納入測試的分錄中出現的筆數，筆數符合條件的科目，其分錄全部命中。',
  ['科目與人員統計'],[]),
 ('進階條件篩選','篩選上限',
  '清單條件分別有 100 個與 101 個值；一次儲存 10 個與 11 個篩選情境。',
  '100 個值與 10 個篩選情境可以儲存。101 個值與 11 個篩選情境不予儲存，並顯示原因。',
  ['篩選上限'],[]),
]
case_text=next((text for text in test_sources if re.search(r'\bclass '+CASE_CLASS+r'\b',text)),None)
if case_text is None or CASE_METHOD+'(' not in case_text or Path(CASE_FILE).name not in case_text:raise RuntimeError('Case test class, method or file reference missing: '+CASE_CLASS)
case_bytes=(TESTS/CASE_FILE).read_bytes()
definition=json.loads(case_bytes.decode('utf-8'))
titles=[row['title'] for row in scope]
if sorted(titles)!=sorted(CASE_ORDER):raise RuntimeError('Case order does not list the review scope rows')
hard_numbers={item:n for n,(item,_,_,_) in enumerate(hard,1)}
case_rows=[]
for case in definition['cases']:
 if case['logic'] not in titles:raise RuntimeError('Case logic is not a review scope row: '+case['id'])
 for item in case['fixedItems']:
  if item not in hard_numbers:raise RuntimeError('Case names a fixed value that is not listed: '+case['id']+' '+item)
 case_rows.append({'id':case['id'],'logic':case['logic']})
ids=[c['id'] for c in case_rows]
if len(set(ids))!=len(ids):raise RuntimeError('Case ids repeat')
# Every case belongs to exactly one test item, and an item only takes cases of its own logic.
item_rows=[];taken={}
for logic,item,given,expected,groups,named in test_items:
 found=[c['id'] for c in definition['cases'] if c['logic']==logic and (c['group'] in groups or c['id'] in named)]
 for group in groups:
  if not any(c['logic']==logic and c['group']==group for c in definition['cases']):raise RuntimeError('Test item names a group the case file does not have: '+item+' '+group)
 for name in named:
  if name not in found:raise RuntimeError('Test item names a case the logic does not have: '+item+' '+name)
 for name in found:
  if name in taken:raise RuntimeError('Case is in two test items: '+name+' '+taken[name]+' '+item)
  taken[name]=item
 if not found:raise RuntimeError('Test item has no case: '+item)
 item_rows.append({'logic':logic,'item':item,'input':given,'expected':expected,'cases':found})
missing=[name for name in ids if name not in taken]
if missing:raise RuntimeError('Cases without a test item: '+', '.join(missing))
item_rows.sort(key=lambda r:CASE_ORDER.index(r['logic']))
# Every fixed value is used by at least one case. The two below only shape the exported report files, so they have no case;
# the named test classes check them.
NO_CASE={'資料驗證報告的借貸淨額':'ReportExportTests','報告明細列數上限':'ExportValidationReportsTypedSeamTests、ExportPrescreenReportTypedSeamTests'}
with_case={item for case in definition['cases'] for item in case['fixedItems']}
for item in hard_numbers:
 if (item in with_case)==(item in NO_CASE):raise RuntimeError('Fixed value must have a case or be listed as checked elsewhere, not both: '+item)
for names in NO_CASE.values():
 for name in names.split('、'):
  if not any(re.search(r'\bclass '+re.escape(name)+r'\b',text) for text in test_sources):raise RuntimeError('Test class missing: '+name)
# Every logic of the review scope has cases, so its test classes include the case test.
for row in scope:
 if not any(c['logic']==row['title'] for c in case_rows):raise RuntimeError('No case for: '+row['title'])
 row['tests']+='、'+CASE_CLASS
# The hash ties a recorded test result to the case file it was run on; a result recorded for another version of the file is not shown as passed.
case_source={'file':'tests/JET.Tests/'+CASE_FILE,'testFile':'tests/JET.Tests/Application/'+CASE_CLASS+'.cs','testClass':CASE_CLASS,
 'order':CASE_ORDER,'sha256':hashlib.sha256(case_bytes).hexdigest()}
save('code-review.json',{'title':'Code Review 紀錄','change':'首次 commit。','acceptance':'以自動化測試結果為主要驗收依據，詳閱 Code Review/Unit test Report.xlsx。','manualScope':'程式內的固定值與規則（hard-coded content），即寫在程式內的六項核心查核邏輯，以及這些邏輯使用的固定值。複核規則第 4 節的需求、可讀性與錯誤處理三項檢查，以此範圍為限。','requirement':'舊版 JET 的功能與查核團隊的作業需求。','scope':scope,'checklist':[{'rule':r,'name':n,'items':items} for r,n,items in checklist],'hardCoded':hard_rows,'caseSource':case_source,'cases':case_rows,'testItems':item_rows})
print('scope rows',len(scope),'review points',sum(len(r['points']) for r in scope),'checks',sum(len(c[2]) for c in checklist),
 'hard-coded items',len(hard_rows),'details',sum(len(r['details']) for r in hard_rows),'cases',len(case_rows),'test items',len(item_rows))
