"""Generate reviewer inputs from current source; no test or approval is implied."""
from pathlib import Path
import json
ROOT=Path(__file__).resolve().parents[3]
SOURCE=Path(__file__).resolve().parent/'source'
def save(name,value):
 (SOURCE/name).write_text(json.dumps(value,ensure_ascii=False,indent=2)+'\n',encoding='utf-8',newline='\n')
# Main functions in the first submission: program location and the unit tests that cover them.
scope=[
 {'title':'納入測試的分錄範圍','path':'AuditCore/GlExcludedPopulation.cs','tests':'GlExcludedPopulationTests、GlProjectionQualityProviderTests'},
 {'title':'完整性測試','path':'AuditCore/ValidationProcedures.cs；AuditCore/AccountComparisonProcedure.cs；AuditCore/CompletenessEligibility.cs','tests':'CompletenessEligibilityTests、WorkflowWithoutTbTests、TaiwanSmePopulationAndCompletenessTests、CompletenessAccountPageTests'},
 {'title':'借貸不平測試','path':'AuditCore/ValidationProcedures.cs','tests':'UnbalancedVoucherDateRowsTests、TaiwanSmePopulationAndCompletenessTests'},
 {'title':'資料可靠性抽樣','path':'AuditCore/ValidationProcedures.cs；AuditCore/InfSampling.cs','tests':'ConfigurableInfSamplingTests、InfSamplingPrfTests、InfSamplingPrfProviderTests'},
 {'title':'進階條件篩選','path':'AuditCore/FilterCompilation.cs；AuditCore/GlRulePredicates.Values.cs','tests':'FilterCompilationTests、CommonFilterPredicateTests、FilterCompletenessTests'},
 {'title':'預篩選','path':'AuditCore/PrescreenRulePredicates.cs；AuditCore/RuleCatalog.cs','tests':'PrescreenProgramTests、CommonFilterPredicateTests'},
]
for row in scope:
 for part in row['path'].split('；'):
  if not (ROOT/'src/JET/JET'/part).is_file():raise RuntimeError('Scope path missing: '+part)
# Review checks, taken from sections 2 to 4 of the code review rule.
checklist=[
 ('2. Unit-Test Acceptance','新增或修改的程式都有對應的單元測試，涵蓋預期行為、邊界條件、無效輸入與錯誤處理。'),
 ('2. Unit-Test Acceptance','必要的測試與受影響的回歸測試，在提交審查的程式版本上全部通過。'),
 ('2. Unit-Test Acceptance','測試的預期結果來自需求或業務規則，不是取自實作。'),
 ('2. Unit-Test Acceptance','測試結果可以重複；沒有略過、停用或不穩定的必要測試。'),
 ('2. Unit-Test Acceptance','變更的行為與重要的判斷分支有被測試執行。'),
 ('3. Hard-Coded Content','程式內的固定值與規則已逐項列出，來源與用途經確認。'),
 ('3. Hard-Coded Content','原始碼沒有密碼、金鑰、權杖等機密。'),
 ('4. Supporting Review','實作符合需求，沒有無關的變更。'),
 ('4. Supporting Review','命名、流程與註解清楚，符合專案的程式撰寫規範。'),
 ('4. Supporting Review','輸入驗證、例外處理、資源釋放與機敏資料的處理適當。'),
 ('4. Supporting Review','單元測試無法涵蓋的行為，已有整合測試或端對端測試。'),
]
# Fixed values and rules in the program that affect audit results (section 3 of the code review rule).
# (item, content, path under src/JET/JET, marker that must still be in that file, origin)
# Two files are written as 'a；b' with their markers in the same order.
# Origin is one of four short labels; it never cites documents outside the submitted source.
LEGACY,NEED,ACCOUNTING,DESIGN='舊版 JET','查核需求','會計慣例','程式設計'
hard=[
 ('抽樣預設筆數','59 筆，可在執行前修改','AuditCore/InfSampling.cs','DefaultSampleSize',LEGACY),
 ('抽樣計算常數','模數 2,147,483,647，以及三組乘數與偏移','AuditCore/ValidationProcedures.cs','InfSamplingPrf',DESIGN),
 ('金額精度','小數 4 位，四捨五入','Domain/ProjectDocument.cs','DefaultMoneyScale',DESIGN),
 ('金額文字','括號為負數；單獨的減號為 0','Domain/Primitives/MoneyScaling.cs','TryParseAmount',ACCOUNTING),
 ('借貸判斷','金額大於等於 0 為借方，小於 0 為貸方','Domain/Rules/GlRowProjector.cs','amountScaled >= 0',NEED),
 ('TB 變動金額','借方減貸方；期末減期初；期末借貸差減期初借貸差','Domain/Rules/TbRowProjector.cs','TryProject',ACCOUNTING),
 ('測試範圍','總帳日期空白或不在查核期間的分錄不納入，起迄日包含在內','AuditCore/GlExcludedPopulation.cs','ClassifyWithMatcher',NEED),
 ('借貸不平','同一傳票的借貸淨額不為 0，沒有容許差','AuditCore/ValidationProcedures.cs','UnbalancedCore',LEGACY),
 ('報告的借貸淨額','差額不超過 0.01 時寫 0','Infrastructure/Export/LegacyReportWriter.Validation.cs','0.01',LEGACY),
 ('完整性差異','GL 與 TB 的科目金額不相等，沒有容許差','Infrastructure/Persistence/Local/LocalValidationRunRepository.cs','tb_s <> gl_s',LEGACY),
 ('核准日檢查','傳票核准日空白或不在查核期間','AuditCore/FilterCompilation.cs','BlankRecordsCategoryPredicate',LEGACY),
 ('報告明細列數','最多 10,000 列，借貸不平與預篩選為 9,999 列；超過只留摘要','AuditCore/ValidationReportProgram.cs；AuditCore/PrescreenReportProgram.cs','10_000；10_000',LEGACY),
 ('期末準備期間','傳票核准日在財報準備開始日當天或之後','AuditCore/GlRulePredicates.cs','PrepPeriodApproval',NEED),
 ('摘要關鍵字','預設 24 個：英文 6、繁體中文 9、簡體中文 9，可編輯','Domain/Rules/PrescreenRules.cs','SuspiciousKeywordDefaults',LEGACY),
 ('未預期借貸組合','貸方為收入，同一傳票沒有金額大於 0 的應收、現金或預收借方','AuditCore/GlRulePredicates.cs','UnexpectedAccountPair',LEGACY),
 ('尾數 0 的個數','依借方平均金額的位數，1 到 12 個','Domain/Rules/PrescreenRules.cs','TrailingZeroThreshold',LEGACY),
 ('非營業日','預設週六、週日；週末或假日，扣除補班日','Domain/Primitives/NonWorkingDays.cs；AuditCore/GlRulePredicates.Values.cs','= [0, 6]；AND NOT (',NEED),
 ('季底','3/31、6/30、9/30、12/31；季底前 1 到 92 天','Domain/Rules/QuarterEndWindows.cs','MaxWindowDays',NEED),
 ('篩選上限','每條清單條件 100 個值；每個案件 10 個情境','Domain/Rules/FilterScenario.cs','FilterScenarioLimits',DESIGN),
 ('日期換算','民國年加 1911；其他純數字不視為日期','Domain/Primitives/DateNormalizer.cs','1911',NEED),
]
hard_rows=[]
for number,(item,content,path,marker,origin) in enumerate(hard,1):
 for part,mark in zip(path.split('；'),marker.split('；'),strict=True):
  if mark not in (ROOT/'src/JET/JET'/part).read_text(encoding='utf-8-sig'):raise RuntimeError('Hard-coded marker missing: '+part+' '+mark)
 hard_rows.append({'id':number,'item':item,'content':content,'path':path,'origin':origin})
save('code-review.json',{'title':'JET 程式碼審查紀錄','change':'首次提交。審查範圍為全部原始碼。','requirement':'舊版 JET 的功能與查核團隊的作業需求','scope':scope,'checklist':[{'rule':r,'item':i} for r,i in checklist],'hardCoded':hard_rows})
print('scope rows',len(scope),'checks',len(checklist),'hard-coded rows',len(hard_rows))
