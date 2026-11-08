"""Generate reviewer inputs from current source; no test or approval is implied."""
from pathlib import Path
import json,csv
ROOT=Path(__file__).resolve().parents[3]
SAT=ROOT/'docs/SAT'
def save(name,value):
 (SAT/'source'/name).write_text(json.dumps(value,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
rows=[
 {'id':'R01','title':'納入測試的分錄','path':'AuditCore/GlExcludedPopulation.cs','question':'查核期間、空白總帳日期與過帳狀態的處理，是否符合核准的審計方法？','example':'準備期間內、期間前後、總帳日期空白及不同過帳狀態的合成分錄，逐筆寫下應否納入。','focus':'先確認範圍，再看金額。不能因程式通過測試就把不適用的分錄算進測試。','tests':'GlExcludedPopulationTests、GlProjectionQualityProviderTests'},
 {'id':'R02','title':'完整性測試','path':'AuditCore/ValidationProcedures.cs；AuditCore/AccountComparisonProcedure.cs；AuditCore/CompletenessEligibility.cs','question':'GL 與 TB 的逐科目比對、缺少 TB 的處理，以及差異提示是否正確？','example':'合成科目 A：GL 借方 100、貸方 30，淨額 70；TB 本期變動 70，應無差異。另測只在 GL 或只在 TB 出現的科目，以及沒有 TB 的案件。','focus':'同時核對存入筆數與合計。這項核對比較配對時和存下後的數字，不能寫成已核對外部來源檔。','tests':'CompletenessEligibilityTests、WorkflowWithoutTbTests、TaiwanSmePopulationAndCompletenessTests'},
 {'id':'R03','title':'借貸不平測試','path':'AuditCore/ValidationProcedures.cs：UnbalancedCore','question':'依傳票號碼彙總納入測試的分錄，借貸淨額不為零時列為不平，是否符合業務規則？','example':'同一傳票借方 100、貸方 90，淨額為 10，應列入。借貸各 100 則不列入。再核對同號碼跨日期及未納入分錄的情況。','focus':'計數、明細與底稿須指向同一批分錄；審計員判斷差異，不由程式阻止後續查核。','tests':'UnbalancedVoucherDateRowsTests、TaiwanSmePopulationAndCompletenessTests'},
 {'id':'R04','title':'可靠性抽樣','path':'AuditCore/ValidationProcedures.cs：InfSampleInsert；AuditCore/InfSamplingPrf.cs；AuditCore/InfSampling.cs','question':'抽樣範圍、筆數與重跑時保留樣本的做法，是否符合本案查核目的？','example':'設定 120 筆且有足夠分錄時，清單與正式底稿均須保留 120 筆。只有少量可用分錄時全部抽出。預設為 59 筆。','focus':'程式只產生樣本。筆數是否足夠、附件是否可靠，由審計人員判斷；範本既有查核方法說明仍需依案件確認。','tests':'ConfigurableInfSamplingTests、InfSamplingPrfTests、InfSamplingPrfProviderTests'},
 {'id':'R05','title':'篩選條件組合','path':'AuditCore/FilterCompilation.cs：BuildPlan、BuildGroup','question':'條件內與條件組之間的 AND、OR 組合順序，是否和畫面顯示的條件一致？','example':'以 A=true、B=false、C=false 驗算 (A OR B) AND C，結果應為 false。再比較 A OR (B AND C)，確認兩者不混用。','focus':'先確認括號與選取範圍。參數綁定、分頁和格式轉換由自動測試檢查。','tests':'FilterCompilationTests、CommonFilterPredicateTests'},
 {'id':'R06','title':'預篩選審計條件','path':'AuditCore/PrescreenRulePredicates.cs；AuditCore/RuleCatalog.cs','question':'本次啟用的預篩選條件是否符合核准方法？條件名稱、計算與報告解讀是否一致？','example':'由複核人員選定本次要核對的條件，另列合成分錄、應命中的傳票與理由。不要以程式自己產生的答案作為標準。','focus':'缺少對照依據的規則先記待確認。既有延後的 KCT 條件不在本次自行補定。','tests':'PrescreenProgramTests、CommonFilterPredicateTests'}
]
save('code-review.json',{'title':'JET 關鍵審計規則複核','note':'黃色欄位由具審計知識的複核人員填寫。未填結論表示尚未完成；自動測試通過不會代填人工結論。','rows':rows})
specs=[
 ('完整性測試','AuditCore/ValidationProcedures.cs','public static string CompletenessDiffCteFor',28,'GL 和 TB 各自依科目加總，再保留兩邊出現的科目。缺少的一邊以零比較。','R02','合成科目 A 的 GL 淨額及 TB 本期變動同為 70，應沒有差異。'),
 ('借貸不平','AuditCore/ValidationProcedures.cs','public static string UnbalancedCore',3,'只取納入測試且有傳票號碼的分錄，依號碼彙總淨額，留下不為零的傳票。','R03','借方 100、貸方 90：淨額 10，應列入。'),
 ('沒有試算表','AuditCore/AccountComparisonProcedure.cs','internal static ProcedureVerdict Finalize',19,'可以比對時使用差異科目數；無法比對時另寫原因，不能把沒有 TB 說成完整性已通過。','R02','未匯入 TB：逐科目比對不適用，其他作業仍可繼續。'),
 ('抽樣筆數','AuditCore/InfSampling.cs','internal static class InfSampling',5,'預設 59 筆。上限來自 Excel 工作表可容納的樣本列，並非審計上建議抽到這麼多。','R04','可輸入 120 筆；公司仍需判斷樣本是否足夠。'),
 ('條件組合','AuditCore/FilterCompilation.cs','string? combined = null;',11,'條件組依序組合，使用括號保留畫面上設定的順序。','R05','(true OR false) AND false 應為 false。'),
 ('Agent 測試範例','../tests/JET.Tests/Application/ConfigurableInfSamplingTests.cs','public async Task RequestedCount_IsStoredAndExportedWithoutFiftyNineRowTruncation',36,'測試用合成案件指定筆數，核對回應、重開案件、抽樣清單與正式底稿。預期筆數直接由測試輸入指定。','自動測試','1、17、120 筆各自核對，避免超過 59 筆後被匯出程式截斷。')
]
items=[]
for title,path,marker,count,explain,review,example in specs:
 p=ROOT/'src/JET/JET'/path
 lines=p.read_text(encoding='utf-8-sig').splitlines()
 matches=[i for i,l in enumerate(lines) if marker in l]
 if not matches and marker=='var groups':
  marker='foreach (var group';matches=[i for i,l in enumerate(lines) if marker in l]
 if not matches:raise RuntimeError('Source marker missing: '+path+' '+marker)
 start=matches[0]
 items.append({'title':title,'path':p.resolve().relative_to(ROOT).as_posix(),'start':start+1,'explanation':explain,'review':review,'example':example,'code':lines[start:start+count]})
save('code-explanation.json',{'title':'JET 程式與例子','note':'程式節錄取自本次工作目錄，供複核時定位。完整規則仍須一起閱讀其呼叫處及測試。','items':items})
with (SAT/'source/dependencies.csv').open(encoding='utf-8-sig',newline='') as f:save('dependencies.json',list(csv.DictReader(f)))
print('review rows',len(rows),'code excerpts',len(items))
