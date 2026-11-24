"""Generate reviewer inputs from current source; no test or approval is implied."""
from pathlib import Path
import json,re
ROOT=Path(__file__).resolve().parents[3]
SOURCE=Path(__file__).resolve().parent/'source'
def save(name,value):
 (SOURCE/name).write_text(json.dumps(value,ensure_ascii=False,indent=2)+'\n',encoding='utf-8',newline='\n')
# Core audit logic covered by manual review: the review points of each logic, the files that define its rules and the test classes that check it.
# Each review point is one rule the reviewer confirms in the code, at most three for a logic; "例：" gives a small example of the rule.
MOST=3
scope=[
 {'title':'納入測試的分錄範圍','points':[
  '日期：總帳日期在查核期間內的分錄才納入；總帳日期空白或無法辨識的分錄不納入。\n例：查核期間是 2025-01-01 至 2025-12-31 時，總帳日期 2026-01-01 的分錄不納入。',
  '過帳狀態：配對了過帳狀態時，值在指定清單內的分錄才納入；選擇納入空白時，過帳狀態空白的分錄也納入。',
  '不納入的原因：每筆不納入的分錄只記一種原因。先判斷日期，日期在查核期間內才判斷過帳狀態。\n例：日期與過帳狀態都不符的分錄，記為日期的原因。'],
  'path':'AuditCore/GlExcludedPopulation.cs','tests':'GlExcludedPopulationTests、GlExcludedPopulationPartitionTests、GlExcludedPopulationProjectionProviderTests'},
 {'title':'完整性測試','points':[
  'GL 的合計：納入測試的分錄依會計科目編號合計，借方為正、貸方為負；未納入測試的分錄不計入。',
  '比較與差異金額：GL 合計與 TB 同一科目的年度變動金額不相等，就列為差異；差異金額是 TB 減 GL。\n例：TB 1,000、GL 900 時，差異金額是 100。',
  '只在一邊出現的科目：GL 有分錄、TB 沒有的科目，TB 金額當成 0；TB 有金額、GL 沒有分錄的科目，GL 金額當成 0。兩者都列為差異。'],
  'path':'AuditCore/ValidationProcedures.cs；AuditCore/AccountComparisonProcedure.cs；AuditCore/CompletenessEligibility.cs','tests':'CompletenessEligibilityTests、WorkflowWithoutTbTests、TaiwanSmePopulationAndCompletenessTests、CompletenessAccountPageTests'},
 {'title':'借貸不平測試','points':[
  '合計方式：納入測試的分錄依傳票號碼合計，借方合計與貸方合計不相等的傳票列為借貸不平。\n例：借方合計 1,000.00、貸方合計 999.99 的傳票列出。',
  '部分分錄未納入：同一張傳票只合計納入測試的分錄；整張傳票都未納入測試時不列出。',
  '傳票號碼空白：傳票號碼空白的分錄不合成一張傳票，也不列為借貸不平。'],
  'path':'AuditCore/ValidationProcedures.cs','tests':'UnbalancedVoucherDateRowsTests、TaiwanSmePopulationAndCompletenessTests'},
 {'title':'資料可靠性測試','points':[
  '抽樣的範圍：只從納入測試的分錄抽樣，抽出的分錄不重複。',
  '抽樣筆數：預設 59 筆，使用者可在執行前指定；納入測試的分錄少於要抽的筆數時，全部抽出。\n例：只有 6 筆納入測試的分錄時，6 筆全部抽出。',
  '重新執行：同一個案件的資料與抽樣筆數不變時，重新執行抽到同一批分錄。'],
  'path':'AuditCore/ValidationProcedures.cs；AuditCore/InfSampling.cs','tests':'ConfigurableInfSamplingTests、InfSamplingPrfTests、InfSamplingPrfProviderTests'},
 {'title':'進階條件篩選','points':[
  '單一條件的判斷：欄位條件、常用篩選條件與當成條件的預篩選規則，各自判斷一筆分錄是否命中；「介於」的條件含上下限。\n例：金額絕對值介於 1,000 至 5,000 時，金額 -5,000 的分錄命中。',
  '條件的合併：用 AND 連接的條件要由同一筆分錄同時符合；AND 與 OR 混用時，依畫面上的順序由左到右計算，AND 不優先計算。\n例：「金額等於 5,000 OR 借方 AND 金額大於或等於 50,000」只有金額達到 50,000 的借方分錄命中。',
  '整張傳票條件：選「至少一筆符合」時，同一張傳票有任一筆分錄符合子條件，這張傳票納入測試的分錄全部列出；選「不存在符合」時，沒有任何一筆符合的傳票全部列出。'],
  'path':'AuditCore/FilterCompilation.cs；AuditCore/GlRulePredicates.cs；AuditCore/GlRulePredicates.Values.cs；AuditCore/GlRulePredicates.Frequency.cs；AuditCore/HitVoucherRows.cs；AuditCore/PersonIdentity.cs','tests':'FilterCompilationTests、SqliteGlRuleSqlEquivalenceTests、CommonFilterPredicateTests、FilterCompletenessTests、EntityFrequencyWorkflowTests'},
 {'title':'預篩選','points':[
  '比較的日期欄位：週末過帳與假日過帳依總帳日期；週末核准、假日核准與期末財報準備日起核准之分錄依傳票核准日，傳票核准日空白的分錄不命中。',
  '週末與假日：週末的規則依每週休息日判斷，補班日是週六或週日時仍然命中；假日的規則只依使用者匯入的國定假日清單判斷。\n例：補班日是週六時，那天入帳的分錄列入週末過帳。',
  '名單的比對：非授權編製人員比對使用者匯入的授權編製人員名單；英文大小寫不同或前後多了空白，仍算同一個人；傳票建立人員空白的分錄不命中。'],
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
# A rule that asks for several things is split into at most three checks, each with its own result; "例：" gives a small example.
# The script stops when the rule no longer has the section heading or the bold name.
UNIT,HARD,SUPPORT='2. Unit-Test Acceptance Rules','3. Manual Review of Hard-Coded Content','4. Supporting Review Rules'
checklist=[
 (UNIT,'Required tests',[
  '預期行為有測試：正常的資料得到需求規定的結果。\n例：借方與貸方合計相等的傳票，不列為借貸不平。',
  '邊界條件有測試：剛好落在界線上的值，結果符合需求。\n例：總帳日期是查核起始日或截止日當天的分錄，納入測試。',
  '無效輸入與錯誤處理有測試：資料無法使用或超過上限時，結果符合需求。\n例：總帳日期無法辨識的分錄不納入測試，並記下原因；清單條件超過 100 個值時，儲存時擋下並說明原因。']),
 (UNIT,'Pass criterion',[
  '本次變更的測試與受影響的回歸測試，以送交複核的版本執行，全部通過。']),
 (UNIT,'Test quality',[
  '斷言核對具體的結果，不只確認程式執行完成。\n例：核對列為借貸不平的傳票號碼與金額。',
  '預期結果依需求或已確認的查核邏輯訂定，不是把受測程式算出的值抄進測試。\n例：差異金額的預期值依「TB 減 GL」的規則另外算出。']),
 (UNIT,'Repeatability',[
  '重複執行得到相同的結果，測試使用的外部資源已控制。\n例：測試使用程式產生的合成資料與暫存資料夾。',
  '必要的測試沒有略過、停用或結果不穩定的情形。\n例：單元測試報告的略過數為 0。']),
 (UNIT,'Coverage',[
  '變更的行為與主要的判斷分支都有測試執行到。\n例：配對了過帳狀態與沒有配對過帳狀態的兩種情形都有測試。']),
 (UNIT,'Regression protection',[
  '缺陷修正附有重現該缺陷的測試，修正前失敗、修正後通過。',
  '沒有為了取得通過的結果而刪除、放寬或改動測試。']),
 (HARD,'Identify hard-coded content',[
  '程式內影響查核結果的固定值與規則，都列在「固定值與規則」工作表，例如門檻、日期、比率、代碼與固定的判斷邏輯。']),
 (HARD,'Validate the source',[
  '每一項的來源已確認，符合需求或舊版 JET 的規則；用途與單位已寫明。\n例：分錄摘要的預設關鍵字和舊版 JET 相同。']),
 (HARD,'Assess maintainability',[
  '會隨環境、部署或業務政策改變的值，由設定或使用者輸入提供。\n例：查核期間、國定假日清單與授權編製人員名單由使用者填寫或匯入。',
  '留在程式內的固定值，意義與查核上的用途清楚，同一個值在各處一致。\n例：抽樣預設筆數 59 筆只在後端定義一次，前端顯示後端提供的值。']),
 (HARD,'Protect sensitive information',[
  '原始碼沒有密碼、金鑰、token 等機密資訊。']),
 (HARD,'Verify behavior',[
  '邊界、精度與進位方式已確認。\n例：金額等於 0 算借方；金額保留到小數第 4 位，第 5 位四捨五入。',
  '預設值與值無法使用時的處理已確認。\n例：來源金額空白時當成 0；不是有效的金額時列為錯誤。',
  '可行時有測試確認固定值的效果。\n例：有測試核對資料可靠性測試預設抽出 59 筆。']),
 (HARD,'Record the conclusion',[
  '「固定值與規則」每一列都已填寫複核結果：可接受、已修正或例外核准；沒有未解決的事項。']),
 (SUPPORT,'Requirements and scope',[
  '實作符合需求。\n例：完整性測試的差異金額是 TB 減 GL，和需求相同。',
  '沒有不相關的變更或非預期的副作用。\n例：修改預篩選的規則時，共用同一段判斷的進階條件篩選結果不受影響。']),
 (SUPPORT,'Readability and consistency',[
  '命名看得出對應哪一項查核，控制流程容易看懂。\n例：不納入測試的分錄由 GlExcludedPopulation 決定。',
  '註解說明程式做什麼、為什麼這樣做。',
  '同一項查核邏輯只定義一次，沒有不必要的重複，並符合專案的撰寫慣例。\n例：把預篩選規則當成進階條件篩選的條件時，判斷方式和預篩選相同。']),
 (SUPPORT,'Error handling and security',[
  '輸入檢查適當。\n例：抽樣筆數填 0 或不是整數時擋下，並說明可以填的範圍。',
  '例外處理與資源釋放適當。\n例：來源檔正被 Excel 開著時，照樣匯入最後一次存檔的內容，並提醒尚未存檔的修改沒有匯入。',
  '機密資訊的處理與相關的存取檢查適當。']),
 (SUPPORT,'Beyond unit tests',[
  '行為取決於資料庫、外部服務、部署設定或單元測試沒有涵蓋的互動時，已另外用整合測試、系統測試或其他方式確認。\n例：用一份合成的案件，依序執行驗證、篩選，並匯出報告與底稿，核對每一步的結果。']),
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
# Each detail is one value or rule the reviewer confirms, at most three for an item, usually the value, its boundary
# and what happens when the value cannot be used. Two files are written as 'a；b' with their markers in the same order.
hard=[
 ('抽樣預設筆數',[
  '預設值：59 筆。',
  '可指定的筆數：使用者可在執行前改成其他筆數，上限 1,048,524 筆。'],
  'AuditCore/InfSampling.cs；Application/Handlers/ValidateRunHandler.cs','DefaultSampleSize；InfSampling.MaximumSampleSize'),
 ('抽樣方式',[
  '隨機抽樣：抽樣的順序由案件的隨機種子序號決定。',
  '重新執行：隨機種子序號在建立案件時產生一次，之後不變；同一份資料、同樣的抽樣筆數重新執行，抽到同一批分錄。'],
  'AuditCore/ValidationProcedures.cs；Domain/ProjectDocument.cs','InfSamplingPrf；SampleSeedExclusiveUpperBound = 2147483647'),
 ('金額精度',[
  '精度：金額保留到小數第 4 位。',
  '進位方式：小數第 5 位四捨五入，負數依絕對值四捨五入。\n例：0.00005 算成 0.0001，-0.00005 算成 -0.0001。'],
  'Domain/ProjectDocument.cs；Domain/Primitives/MoneyScaling.cs','DefaultMoneyScale = 10_000；MidpointRounding.AwayFromZero'),
 ('金額文字',[
  '負數的寫法：括號內的數字是負數。\n例：(1,000) 是 -1,000。',
  '算成 0 的寫法：金額空白，或只有一個減號。',
  '不是有效的金額：這一列列為錯誤並說明原因。\n例：儲存格是「#N/A」。'],
  'Domain/Primitives/MoneyScaling.cs；Domain/Rules/MappedAmountCell.cs','TryParseAmount；ProjectionErrorReasons.AmountInvalid'),
 ('借貸別',[
  '金額大於或等於 0 為借方，小於 0 為貸方。'],
  'Domain/Rules/GlRowProjector.cs','amountScaled >= 0'),
 ('試算表（TB）年度變動金額',[
  '「設定年度變動金額」：取該欄的值。',
  '「設定借方與貸方金額」：借方減貸方。',
  '期初與期末：「設定期初與期末金額」是期末減期初；「設定借方貸方之期初期末金額」是（期末借方減期末貸方）減（期初借方減期初貸方）。'],
  'Domain/Rules/TbRowProjector.cs','TryProject'),
 ('納入測試的分錄範圍',[
  '日期的邊界：查核起始日與截止日當天的分錄納入。',
  '過帳狀態的寫法：英文大小寫不同或前後多了空白，仍算同一個代碼。\n例：指定的代碼是「Y」時，過帳狀態「 y 」的分錄納入。'],
  'AuditCore/GlExcludedPopulation.cs','GlExclusionReason.PostingStatus'),
 ('借貸不平測試',[
  '容許誤差：0；同一傳票號碼的分錄金額合計不為 0 就列為借貸不平。\n例：相差 0.0001 的傳票也列出。'],
  'AuditCore/ValidationProcedures.cs','UnbalancedCore'),
 ('資料驗證報告的借貸淨額',[
  '顯示方式：借貸淨額取絕對值，不超過 0.01 時顯示為 0。'],
  'AuditCore/ValidationProcedures.cs','0.01m'),
 ('完整性測試的科目差異',[
  '容許誤差：0；同一科目的 TB 年度變動金額與 GL 金額合計不相等就列為差異。'],
  'AuditCore/ValidationProcedures.cs','tb_s <> gl_s'),
 ('核准日不在期間',[
  '條件：傳票核准日空白、早於查核起始日或晚於查核截止日。',
  '邊界：查核起始日與截止日當天屬於期間內。'],
  'AuditCore/FilterCompilation.cs','approval_date < @periodStart OR approval_date > @periodEnd'),
 ('報告明細列數上限',[
  '資料驗證報告：空白科目編號、空白傳票號碼、空白摘要與核准日不在期間的明細，各 10,000 列。',
  '借貸不平測試與預篩選的明細：各 9,999 列。',
  '超過上限時：只輸出摘要；預篩選報告在摘要註明明細資料不匯出。'],
  'AuditCore/ValidationReportProgram.cs；AuditCore/PrescreenReportProgram.cs','10_000；10_000'),
 ('期末財報準備日起核准之分錄',[
  '條件：傳票核准日在期末財報準備日當天或之後。',
  '空白：傳票核准日空白的分錄不命中。'],
  'AuditCore/GlRulePredicates.cs','PrepPeriodApproval'),
 ('分錄摘要出現特定描述',[
  '預設關鍵字共 24 個：英文 ADJ、REV、RECLASS、SUSPENSE、ERROR、WRONG；繁體中文 調整、迴轉、沖銷、重分類、避險、重編、錯誤、計畫外、預算外；簡體中文 调整、回转、冲销、重分类、避险、重编、错误、计画外、预算外。',
  '比對方式：摘要包含任一關鍵字就命中，不分英文大小寫。\n例：摘要「年底調整」包含「調整」，命中。',
  '使用的關鍵字：預篩選固定使用預設關鍵字；進階條件篩選帶入預設關鍵字，使用者可以增刪。'],
  'Domain/Rules/PrescreenRules.cs','SuspiciousKeywordDefaults'),
 ('未預期出現之特定借貸組合',[
  '命中的分錄：收入科目的貸方分錄。',
  '同一張傳票的條件：傳票內有金額大於 0 的分錄，而且這些分錄的科目都不是應收款項、現金或預收款。',
  '科目分類：科目屬於哪一類，依使用者匯入的科目配對。'],
  'AuditCore/GlRulePredicates.cs','UnexpectedAccountPair'),
 ('分錄金額中有連續 0 的尾數',[
  '0 的個數由 GL 決定：GL 借方金額的平均，整數部分有幾位數，0 的個數就是位數減 1，最少 1 個、最多 12 個。\n例：平均 12,345.67 時，0 的個數是 4。',
  '命中：金額整數部分的尾數連續 0 達到這個個數就命中，不看正負號。'],
  'Domain/Rules/PrescreenRules.cs','totals.GlDebitScaled / moneyScale / totals.GlRowCount'),
 ('非營業日',[
  '每週休息日：預設為週六與週日。',
  '非營業日：每週休息日或使用者匯入的國定假日。',
  '補班日：使用者匯入的補班日不算非營業日。'],
  'Domain/Primitives/NonWorkingDays.cs；AuditCore/GlRulePredicates.Values.cs','= [0, 6]；AND NOT ('),
 ('季底',[
  '季底：3 月 31 日、6 月 30 日、9 月 30 日、12 月 31 日。',
  '前推天數：1 至 92 天，含季底當天。\n例：前推 3 天時，12 月 29 日至 31 日屬於季底期間。',
  '查核截止日：查核截止日以同樣的前推天數往前推，也屬於季底期間。\n例：查核截止日是 11 月 30 日、前推 3 天時，11 月 28 日至 30 日屬於季底期間。'],
  'Domain/Rules/QuarterEndWindows.cs','MaxWindowDays = 92'),
 ('篩選上限',[
  '清單條件：每條最多 100 個值，超過時儲存時擋下並說明原因。',
  '篩選情境：每個案件最多儲存 10 個，超過時儲存時擋下並說明原因。'],
  'Domain/Rules/FilterScenario.cs','FilterScenarioLimits'),
 ('日期換算',[
  '可辨識的寫法：西元與民國年的寫法都換成西元日期，民國年加 1911。\n例：2025/6/11、20250611、114/6/11 與 1140611 都是 2025-06-11。',
  '無法辨識的寫法：只有年份、缺日、月日不存在等寫法。\n例：「2025」「2025.12」「114/13/1」。'],
  'Domain/Primitives/DateNormalizer.cs','1911'),
]
hard_rows=[]
for number,(item,details,path,marker) in enumerate(hard,1):
 if not 1<=len(details)<=MOST:raise RuntimeError('A fixed value has one to three details: '+item)
 for part,mark in zip(path.split('；'),marker.split('；'),strict=True):
  if mark not in (ROOT/'src/JET/JET'/part).read_text(encoding='utf-8-sig'):raise RuntimeError('Hard-coded marker missing: '+part+' '+mark)
 hard_rows.append({'id':number,'item':item,'details':details,'path':path})
# Test cases of the six core audit logics. The case file under the test project is the only definition:
# AuditLogicCaseTests runs every case against the product, and the unit test report lists each case with its result.
# The test data and the expected result of a case stay in the case file; nothing here decides an expected result.
import hashlib
CASE_FILE,CASE_CLASS,CASE_METHOD='Fixtures/audit-logic-cases.json','AuditLogicCaseTests','Case_ProducesTheExpectedResult'
# The cases are listed in the order the user meets the logics in the product: validation first, then prescreening, then filtering.
CASE_ORDER=['納入測試的分錄範圍','完整性測試','借貸不平測試','資料可靠性測試','預篩選','進階條件篩選']
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
 case_rows.append({'id':case['id'],'logic':case['logic'],'group':case['group'],'name':case['name'],'directoryNo':case['directoryNo']})
ids=[c['id'] for c in case_rows]
if len(set(ids))!=len(ids):raise RuntimeError('Case ids repeat')
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
# Within a logic the groups follow the tester's directory: a group is placed by the smallest directory number among its cases,
# and groups the directory does not have come after, in the order the case file lists them. Inside a group the directory number decides, then the file order.
LAST_PLACE=10**6
group_place={}
for n,c in enumerate(case_rows):
 key=(c['logic'],c['group'])
 place=(c['directoryNo'] if c['directoryNo'] is not None else LAST_PLACE,n)
 group_place[key]=min(group_place.get(key,place),place)
for n,c in enumerate(case_rows):c['_order']=(CASE_ORDER.index(c['logic']),group_place[(c['logic'],c['group'])],c['directoryNo'] if c['directoryNo'] is not None else LAST_PLACE,n)
case_rows.sort(key=lambda c:c['_order'])
for c in case_rows:del c['_order'],c['directoryNo']
# The hash ties a recorded test result to the case file it was run on; a result recorded for another version of the file is not shown as passed.
case_source={'file':'tests/JET.Tests/'+CASE_FILE,'testFile':'tests/JET.Tests/Application/'+CASE_CLASS+'.cs','testClass':CASE_CLASS,
 'order':CASE_ORDER,'sha256':hashlib.sha256(case_bytes).hexdigest()}
save('code-review.json',{'title':'Code Review 紀錄','change':'首次 commit。','acceptance':'以自動化測試結果為主要驗收依據，結果見 Code Review/Unit test Report.xlsx。','manualScope':'程式內的固定值與規則（hard-coded content），即寫在程式內的六項核心查核邏輯，以及這些邏輯使用的固定值。複核規則第 4 節的需求、可讀性與錯誤處理三項檢查，以此範圍為限。','requirement':'舊版 JET（IDEA 版）的功能與查核團隊的作業需求。','scope':scope,'checklist':[{'rule':r,'name':n,'items':items} for r,n,items in checklist],'hardCoded':hard_rows,'caseSource':case_source,'cases':case_rows})
print('scope rows',len(scope),'review points',sum(len(r['points']) for r in scope),'checks',sum(len(c[2]) for c in checklist),
 'hard-coded items',len(hard_rows),'details',sum(len(r['details']) for r in hard_rows),'cases',len(case_rows))
