"""Generate reviewer inputs from current source; no test or approval is implied."""
from pathlib import Path
import json,re
ROOT=Path(__file__).resolve().parents[3]
SOURCE=Path(__file__).resolve().parent/'source'
def save(name,value):
 (SOURCE/name).write_text(json.dumps(value,ensure_ascii=False,indent=2)+'\n',encoding='utf-8',newline='\n')
# Core audit rules covered by manual review: the files that define each rule and the test classes that check it.
scope=[
 {'title':'納入測試的分錄範圍','path':'AuditCore/GlExcludedPopulation.cs','tests':'GlExcludedPopulationTests、GlExcludedPopulationPartitionTests、GlExcludedPopulationProjectionProviderTests'},
 {'title':'完整性測試','path':'AuditCore/ValidationProcedures.cs；AuditCore/AccountComparisonProcedure.cs；AuditCore/CompletenessEligibility.cs；Infrastructure/Persistence/Local/LocalValidationRunRepository.cs','tests':'CompletenessEligibilityTests、WorkflowWithoutTbTests、TaiwanSmePopulationAndCompletenessTests、CompletenessAccountPageTests'},
 {'title':'借貸不平測試','path':'AuditCore/ValidationProcedures.cs','tests':'UnbalancedVoucherDateRowsTests、TaiwanSmePopulationAndCompletenessTests'},
 {'title':'資料可靠性測試','path':'AuditCore/ValidationProcedures.cs；AuditCore/InfSampling.cs','tests':'ConfigurableInfSamplingTests、InfSamplingPrfTests、InfSamplingPrfProviderTests'},
 {'title':'進階條件篩選','path':'AuditCore/FilterCompilation.cs；AuditCore/GlRulePredicates.cs；AuditCore/GlRulePredicates.Values.cs；AuditCore/GlRulePredicates.Frequency.cs','tests':'FilterCompilationTests、SqliteGlRuleSqlEquivalenceTests、CommonFilterPredicateTests、FilterCompletenessTests、EntityFrequencyWorkflowTests'},
 {'title':'預篩選','path':'Domain/Rules/PrescreenRuleDefinitions.cs；Domain/Rules/PrescreenRules.cs；AuditCore/PrescreenRulePredicates.cs；AuditCore/GlRulePredicates.cs','tests':'PrescreenRuleFixedAnswerTests、SqliteGlRuleSqlEquivalenceTests、DemoRuleOracleTests'},
]
TESTS=ROOT/'src/JET/tests/JET.Tests'
test_sources=[p.read_text(encoding='utf-8-sig') for p in TESTS.rglob('*.cs') if not {'bin','obj'}&set(p.relative_to(TESTS).parts)]
for row in scope:
 for part in row['path'].split('；'):
  if not (ROOT/'src/JET/JET'/part).is_file():raise RuntimeError('Scope path missing: '+part)
 for name in row['tests'].split('、'):
  if not any(re.search(r'\bclass '+re.escape(name)+r'\b',text) for text in test_sources):raise RuntimeError('Scope test class missing: '+name)
# Review checks: one for each rule in sections 2 to 4 of the code review rule.
# The first column is the section heading as the rule writes it; the script stops when the rule no longer has that heading.
UNIT,HARD,SUPPORT='2. Unit-Test Acceptance Rules','3. Manual Review of Hard-Coded Content','4. Supporting Review Rules'
checklist=[
 (UNIT,'新增或修改的程序與函式均有對應的單元測試，涵蓋預期行為、邊界條件、無效輸入與錯誤處理。'),
 (UNIT,'必要的測試與受影響的迴歸測試，於送交複核的提交全數通過。'),
 (UNIT,'斷言核對具體的預期結果；預期結果依需求或已確認的審計規則訂定，非由受測程式產生。'),
 (UNIT,'測試結果可重現，測試使用的外部資源已適當控制；必要的測試無略過、停用或結果不穩定的情形。'),
 (UNIT,'變更的行為與主要判斷分支均由測試涵蓋。'),
 (UNIT,'缺陷修正於可行時附有重現該缺陷的測試；未為取得通過結果而移除、放寬或更改測試。'),
 (HARD,'程式內的固定值與規則已逐項列出，來源與用途均經確認。'),
 (HARD,'隨環境、部署或業務政策變動的值已改由設定提供；留在程式內的穩定值為具名常數，意義與理由明確。'),
 (HARD,'原始碼未包含密碼、金鑰、權杖等機敏資訊。'),
 (HARD,'固定值與規則的邊界、精度、進位、預設值與替代行為已確認；其效果於可行時由測試核對。'),
 (SUPPORT,'實作符合需求，無不相關的變更與非預期的副作用。'),
 (SUPPORT,'命名明確，控制流程易於理解，註解適當，無不必要的重複，並符合專案的撰寫慣例。'),
 (SUPPORT,'輸入檢查、例外處理、資源釋放、機敏資料處理與相關的存取檢查適當。'),
 (SUPPORT,'行為取決於資料庫、外部服務、部署設定或單元測試未涵蓋的互動時，已另以整合測試、系統測試或其他適當方式確認。'),
 (SUPPORT,'複核人員非開發人員本人；風險較高的變更已完成專案要求的額外複核。'),
]
rule_text=(SOURCE/'Code review rule.md').read_text(encoding='utf-8')
for heading in (UNIT,HARD,SUPPORT):
 if '## '+heading+'\n' not in rule_text:raise RuntimeError('Rule heading changed: '+heading)
# Fixed values and rules in the program that affect audit results (section 3 of the code review rule).
# (item, content, path under src/JET/JET, marker that must still be in that file, origin)
# Two files are written as 'a；b' with their markers in the same order.
# Origin is one of four short labels; it never cites documents outside the submitted source.
LEGACY,NEED,ACCOUNTING,DESIGN='舊版 JET','查核需求','會計慣例','程式設計'
hard=[
 ('抽樣預設筆數','預設 59 筆；審計員可於執行前改為 1 至 1,048,524 筆。','AuditCore/InfSampling.cs','DefaultSampleSize',LEGACY),
 ('抽樣排序演算法','以抽樣種子對來源列號執行三輪 Feistel 運算，依排序鍵由小到大取樣。模數 2,147,483,647；乘數 48,271、69,621、65,539；偏移量 1、104,729、130,363。','AuditCore/ValidationProcedures.cs；Domain/ProjectDocument.cs','InfSamplingPrf；SampleSeedExclusiveUpperBound = 2147483647',DESIGN),
 ('金額精度','金額乘以 10,000 後以整數儲存，精度至小數第 4 位；第 5 位四捨五入。','Domain/ProjectDocument.cs；Domain/Primitives/MoneyScaling.cs','DefaultMoneyScale = 10_000；MidpointRounding.AwayFromZero',DESIGN),
 ('金額文字','括號內的數字視為負數；僅有一個半形減號時視為 0。','Domain/Primitives/MoneyScaling.cs','TryParseAmount',ACCOUNTING),
 ('借貸別','金額大於或等於 0 為借方，小於 0 為貸方。','Domain/Rules/GlRowProjector.cs','amountScaled >= 0',NEED),
 ('TB 年度變動金額','依欄位配對方式計算：直接取年度變動金額欄；借方金額減貸方金額；期末餘額減期初餘額；期末借貸餘額之差減期初借貸餘額之差。','Domain/Rules/TbRowProjector.cs','TryProject',ACCOUNTING),
 ('納入測試的分錄範圍','總帳日期空白、早於查核起始日或晚於查核截止日的分錄不納入測試；起始日與截止日當日納入。已配對過帳狀態時，狀態不在審計員指定清單內的分錄亦不納入；先判定日期。','AuditCore/GlExcludedPopulation.cs','GlExclusionReason.PostingStatus',NEED),
 ('借貸不平測試','同一傳票號碼的分錄金額合計不為 0 即列為借貸不平，不設容許誤差。','AuditCore/ValidationProcedures.cs','UnbalancedCore',LEGACY),
 ('資料驗證報告的借貸淨額','借貸淨額取絕對值；不超過 0.01 時顯示為 0。','Infrastructure/Export/LegacyReportWriter.Validation.cs','0.01',LEGACY),
 ('完整性測試的科目差異','同一科目的 TB 年度變動金額與 GL 金額合計不相等即列為差異，不設容許誤差。','Infrastructure/Persistence/Local/LocalValidationRunRepository.cs','tb_s <> gl_s',LEGACY),
 ('核准日不在期間','傳票核准日空白、早於查核起始日或晚於查核截止日。','AuditCore/FilterCompilation.cs','BlankRecordsCategoryPredicate',LEGACY),
 ('報告明細列數上限','空白科目編號、空白傳票號碼、空白摘要、核准日不在期間：各 10,000 列。借貸不平測試、預篩選：各 9,999 列。超過時僅輸出摘要。','AuditCore/ValidationReportProgram.cs；AuditCore/PrescreenReportProgram.cs','10_000；10_000',LEGACY),
 ('期末財報準備日起核准之分錄','傳票核准日為期末財報準備日當日或之後。','AuditCore/GlRulePredicates.cs','PrepPeriodApproval',NEED),
 ('分錄摘要出現特定描述','預設關鍵字 24 個：英文 6 個、繁體中文 9 個、簡體中文 9 個；摘要包含任一關鍵字即符合。預篩選固定使用此清單；進階條件篩選可由審計員增刪。','Domain/Rules/PrescreenRules.cs','SuspiciousKeywordDefaults',LEGACY),
 ('未預期出現之特定借貸組合','收入科目的貸方分錄；同一傳票有金額大於 0 的分錄，但其中無應收款項、現金或預收款科目。','AuditCore/GlRulePredicates.cs','UnexpectedAccountPair',LEGACY),
 ('分錄金額中有連續 0 的尾數','以 GL 借方金額合計除以 GL 分錄筆數得平均；0 的個數為平均的整數位數減 1，下限 1 個、上限 12 個。','Domain/Rules/PrescreenRules.cs','totals.GlDebitScaled / moneyScale / totals.GlRowCount',LEGACY),
 ('非營業日','每週休息日預設為週六與週日。非營業日為每週休息日或國定假日，補班日除外。','Domain/Primitives/NonWorkingDays.cs；AuditCore/GlRulePredicates.Values.cs','= [0, 6]；AND NOT (',NEED),
 ('季底','季底為 3 月 31 日、6 月 30 日、9 月 30 日與 12 月 31 日，另計查核截止日。前推天數可設為 1 至 92 天，含當日。','Domain/Rules/QuarterEndWindows.cs','MaxWindowDays = 92',NEED),
 ('篩選上限','每項清單條件上限 100 個值；每個案件上限 10 個篩選情境。','Domain/Rules/FilterScenario.cs','FilterScenarioLimits',DESIGN),
 ('日期換算','民國 100 至 199 年加 1911 換算為西元年，可寫成 7 位數字；8 位數字依西元年月日解讀。其餘純數字與年份不足 4 位的寫法不視為日期。','Domain/Primitives/DateNormalizer.cs','1911',NEED),
]
hard_rows=[]
for number,(item,content,path,marker,origin) in enumerate(hard,1):
 for part,mark in zip(path.split('；'),marker.split('；'),strict=True):
  if mark not in (ROOT/'src/JET/JET'/part).read_text(encoding='utf-8-sig'):raise RuntimeError('Hard-coded marker missing: '+part+' '+mark)
 hard_rows.append({'id':number,'item':item,'content':content,'path':path,'origin':origin})
save('code-review.json',{'title':'JET 程式碼複核紀錄','change':'首次提交。全部原始碼以自動化測試驗收；人工複核的範圍為核心審計規則，以及程式內的固定值與規則（hard-coded content）。','requirement':'舊版 JET（IDEA 版）的功能與查核團隊的作業需求。','scope':scope,'checklist':[{'rule':r,'item':i} for r,i in checklist],'hardCoded':hard_rows})
print('scope rows',len(scope),'checks',len(checklist),'hard-coded rows',len(hard_rows))
