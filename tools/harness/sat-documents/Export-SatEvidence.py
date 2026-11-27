"""Read test results, the test environment and measured C# coverage into the SAT workbook source.

Two inputs are accepted: a harness Public receipt (collected with -CollectCoverage), or the TRX and
Cobertura files written by running JET.Tests.exe directly with the documented filters, together with the
JUnit file written by node --test for tests/frontend (--trx/--coverage/--frontend-junit).

--cases-trx reads only the results of the audit logic cases from a TRX file and updates that one part of the
workbook source; the test totals, the environment and the coverage stay as they are.
"""
from pathlib import Path
import sys,json,xml.etree.ElementTree as E,collections,hashlib,argparse,platform,re,subprocess,datetime
ROOT=Path(__file__).resolve().parents[3]
SOURCE=Path(__file__).resolve().parent/'source'
def load(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def local_date(stamp):
 # The result files record the start time in UTC; the documents show the date on this computer's clock.
 # The stamp may carry seven decimals of a second, one more than the standard library reads.
 text=re.sub(r'(\.\d{6})\d+',r'\1',stamp.strip()).replace('Z','+00:00')
 moment=datetime.datetime.fromisoformat(text)
 if moment.tzinfo is None:moment=moment.replace(tzinfo=datetime.timezone.utc)
 return moment.astimezone().date().isoformat()
# 方案資料夾底下的三行命令，和技術文件第 12 節的程式碼區塊相同；路徑以 JET.slnx 所在的資料夾為起點。
COMMANDS=[
 'dotnet build JET.slnx -c Debug',
 'tests/JET.Tests/bin/Debug/net10.0-windows/JET.Tests.exe --filter-not-trait "TestProfile=Scale"',
 'node --test --test-concurrency=4 tests/frontend/',
]
# 涵蓋率表裡直接放在 JET 資料夾、不屬於任何子資料夾的那一組原始檔；技術文件第 6 節把這一層稱為 Host。
ROOT_MODULE='Host'
EXIT_CRITERIA = '上列命令執行的測試案例全數通過。'
def tool_version(*command):
 # 轉換當下實際查到的版本，只取主要版本；查不到就停止，不填推測的值。
 text=subprocess.run(command,cwd=ROOT,capture_output=True,text=True,check=True).stdout.strip()
 if not text:raise ValueError('No version from '+command[0])
 return text.lstrip('v').split('.')[0]
def require_package(name):
 # 測試專案檔要引用這個套件，報告才寫它的名稱。
 project=(ROOT/'src/JET/tests/JET.Tests/JET.Tests.csproj').read_text(encoding='utf-8-sig')
 if not re.search(r'<PackageReference Include="'+re.escape(name)+r'" Version="[^"]+"',project):raise ValueError('Package not referenced: '+name)
def environment():
 # 測試與轉換在同一台電腦執行；作業系統與工具取轉換當下的值，只寫名稱與主要版本，不寫建置編號與套件的完整版本。
 if platform.system()!='Windows':raise ValueError('Run on the Windows machine that ran the tests')
 bits={'AMD64':'x64','ARM64':'ARM64'}.get(platform.machine(),platform.machine())
 require_package('xunit.v3.mtp-v2')
 require_package('Microsoft.Testing.Extensions.CodeCoverage')
 return {
  'os':'Windows '+platform.release()+' '+bits,
  'dotnetSdk':tool_version('dotnet','--version'),
  'testFramework':'xUnit v3',
  'node':tool_version('node','--version'),
  'coverageTool':'Microsoft.Testing.Extensions.CodeCoverage',
 }
def frontend_step(path):
 # node --test --test-reporter=junit 的結果：每個 testcase 是一個測試，失敗的帶 failure，略過的帶 skipped。檔案路徑不寫進紀錄。
 cases=list(E.parse(path).getroot().iter('testcase'))
 if not cases:raise ValueError('JUnit has no tests')
 failed=sum(c.find('failure') is not None or c.find('error') is not None for c in cases)
 skipped=sum(c.find('skipped') is not None for c in cases)
 passed=len(cases)-failed-skipped
 status='passed' if failed==0 and skipped==0 else 'failed'
 return {'name':'frontend-tests','status':status,'total':len(cases),'executed':len(cases)-skipped,'passed':passed,'failed':failed,'skipped':skipped}
def inside(rel,bound):
 p=(ROOT/rel).resolve()
 if not p.is_relative_to(bound.resolve()):raise ValueError('Evidence outside selected run')
 return p
parser=argparse.ArgumentParser()
parser.add_argument('receipt',nargs='?')
parser.add_argument('--trx')
parser.add_argument('--coverage')
parser.add_argument('--frontend-junit')
parser.add_argument('--excel-receipt')
parser.add_argument('--cases-trx')
args=parser.parse_args()
NS='{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'
review=load(SOURCE/'code-review.json')
def trx_records(trx_root):
 names={d.get('id'):d.find(NS+'TestMethod').get('className')+'.'+d.find(NS+'TestMethod').get('name') for d in trx_root.iter(NS+'UnitTest')}
 return [{'fqn':names[u.get('testId')],'displayName':u.get('testName'),'outcome':u.get('outcome')} for u in trx_root.iter(NS+'UnitTestResult')]
def review_cases(records,date):
 # 每個查核邏輯案例的執行結果。案例編號是測試顯示名稱裡的 caseId；結果檔裡沒有的案例記為未執行，不補成通過。
 source=review['caseSource'];found={}
 for x in records:
  hit=re.search(r'\.'+re.escape(source['testClass'])+r'\.\w+\(caseId: "([^"]+)"\)',x.get('displayName') or '')
  if not hit:continue
  if hit[1] in found:raise ValueError('Case ran more than once: '+hit[1])
  found[hit[1]]=x['outcome']
 # 結果綁定案例檔的內容：記下案例檔的雜湊，產生活頁簿時雜湊不同就不採用這份結果。
 # 結果檔要和案例檔是同一版；案例檔在產生 code-review.json 之後又改過時停止，先重新執行 Build-ReviewData.py 與測試。
 digest=hashlib.sha256((ROOT/'src/JET'/source['file']).read_bytes()).hexdigest()
 if digest!=source['sha256']:raise ValueError('Case file changed after Build-ReviewData.py ran')
 known={c['id'] for c in review['cases']}
 if set(found)-known:raise ValueError('Result for a case that is not in code-review.json: '+', '.join(sorted(set(found)-known)))
 return {'testClass':source['testClass'],'date':date,'definitionSha256':digest,'results':{c['id']:{'Passed':'passed','Failed':'failed'}.get(found.get(c['id']),'notRun') for c in review['cases']}}
if args.cases_trx:
 # 只更新案例的執行結果，其他欄位照原樣寫回。
 if args.receipt or args.trx or args.coverage or args.frontend_junit or args.excel_receipt:raise ValueError('Use --cases-trx on its own')
 trx_root=E.parse(args.cases_trx).getroot()
 data=load(SOURCE/'test-record.json')
 data['reviewCases']=review_cases(trx_records(trx_root),local_date(trx_root.find(NS+'Times').get('start')))
 (SOURCE/'test-record.json').write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n',encoding='utf-8',newline='\n')
 print(json.dumps({'cases':data['reviewCases']['results']},ensure_ascii=False))
 sys.exit(0)
steps=[]; records=[]; coverage_file=None
if args.trx:
 # 直接執行測試程式的結果：排除的標記由執行命令決定，這裡只讀結果檔。
 if args.receipt or not args.coverage or not args.frontend_junit:raise ValueError('Use --trx together with --coverage and --frontend-junit, without a receipt')
 trx_root=E.parse(args.trx).getroot()
 counter=trx_root.find('.//'+NS+'Counters')
 total,executed,passed,failed=(int(counter.get(k)) for k in ['total','executed','passed','failed'])
 if total==0:raise ValueError('TRX has no tests')
 records=trx_records(trx_root)
 if len(records)!=total:raise ValueError('TRX result count mismatch')
 status='passed' if failed==0 and passed==total else 'failed'
 steps.append({'name':'public-tests','status':status,'total':total,'executed':executed,'passed':passed,'failed':failed,'skipped':total-executed})
 steps.append(frontend_step(Path(args.frontend_junit).resolve()))
 if steps[-1]['status']!='passed':status='failed'
 coverage_file=Path(args.coverage).resolve()
 r={'startedUtc':trx_root.find(NS+'Times').get('start'),'status':status,'runId':'','configuration':'Debug','steps':[]}
 receipt_path=None
else:
 if not args.receipt:raise ValueError('Give a Public receipt, or --trx with --coverage')
 receipt_path=Path(args.receipt).resolve()
 if not receipt_path.is_relative_to((ROOT/'artifacts/harness/runs').resolve()):raise ValueError('Use a harness run')
 run=receipt_path.parent
 r=load(receipt_path)
 if r['command']!='Public' or r.get('privateData',{}).get('pathInspected') is not False:raise ValueError('Public synthetic evidence required')
for step in r['steps']:
 t=step.get('test')
 if t:
  summary=load(inside(t['summary'],run))
  if summary['counters']!=t['counters']:raise ValueError('Counter mismatch')
  trx=inside(t['trx'],run)
  if not trx.exists():raise ValueError('Missing TRX')
  if hashlib.sha256(trx.read_bytes()).hexdigest().upper()!=summary['trxSha256'].upper():raise ValueError('TRX changed')
  counter=E.parse(trx).getroot().find('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}Counters')
  for k in ['total','executed','passed','failed']:
   if int(counter.get(k))!=int(t['counters'][k]):raise ValueError('TRX counter mismatch')
  steps.append({'name':step['name'],'status':step['status'],**t['counters']})
  records.extend(summary['records'])
 if step.get('coverage'):coverage_file=inside(step['coverage'],run)
 if 'frontend' in step['name'] and not t:
  log=inside(step['stdout']['path'],run).read_text(encoding='utf-8',errors='replace')
  import re
  def number(key):
   hits=re.findall(r'(?:ℹ|#)\s*'+key+r'\s+(\d+)',log)
   return int(hits[-1]) if hits else None
  steps.append({'name':step['name'],'status':step['status'],'total':number('tests'),'passed':number('pass'),'failed':number('fail'),'skipped':number('skipped')})
if coverage_file is None:raise ValueError('Run Public with -CollectCoverage')
cov=E.parse(coverage_file).getroot()
seen={}
for p in cov.findall('packages/package'):
 if p.get('name')!='JET':continue
 for c in p.findall('classes/class'):
  fn=(c.get('filename') or '').replace('\\','/')
  marker='/src/JET/JET/'
  if marker.lower() not in fn.lower():continue
  rel=fn[fn.lower().index(marker.lower())+len(marker):]
  if rel.startswith(('obj/','bin/')):continue
  lines=seen.setdefault(rel,{})
  for line in c.findall('lines/line'):
   n=int(line.get('number'));lines[n]=lines.get(n,False) or int(line.get('hits','0'))>0
modules=collections.defaultdict(lambda:[0,0])
files=[]
for name,lines in sorted(seen.items()):
 group=name.split('/')[0] if '/' in name else ROOT_MODULE
 covered=sum(lines.values());total=len(lines)
 modules[group][0]+=covered;modules[group][1]+=total
 files.append({'path':'src/JET/JET/'+name,'covered':covered,'total':total,'rate':covered/total if total else None})
groups=[]
for module,(covered,total) in sorted(modules.items(),key=lambda item:(item[0]==ROOT_MODULE,item[0])):
 groups.append({'module':module,'covered':covered,'total':total,'rate':covered/total if total else None})
env=environment()
checks=[]
# The case test runs the cases of every logic; a logic counts only its own cases, found by the case id in the display name.
case_class=review['caseSource']['testClass']
case_logic={c['id']:c['logic'] for c in review['cases']}
def case_of(x):
 hit=re.search(r'\.'+re.escape(case_class)+r'\.\w+\(caseId: "([^"]+)"\)',x.get('displayName') or '')
 return case_logic.get(hit[1]) if hit else None
for row in review['scope']:
 names=row['tests'].split('、')
 selected=[x for x in records if any('.'+name+'.' in x['fqn'] for name in names if name!=case_class) or ('.'+case_class+'.' in x['fqn'] and case_of(x)==row['title'])]
 checks.append({'title':row['title'],'classes':names,'total':len(selected),'passed':sum(x['outcome']=='Passed' for x in selected),'failed':sum(x['outcome']=='Failed' for x in selected)})
data={'title':'單元測試報告','date':local_date(r['startedUtc']),'status':r['status'],'runId':r['runId'],'receipt':receipt_path.relative_to(ROOT).as_posix() if receipt_path else '','revision':(r.get('repository') or {}).get('head','')[:7],'configuration':r['configuration'],'command':'\n'.join(COMMANDS),'scope':'合成測試資料','environment':env,'exitCriteria':EXIT_CRITERIA,'steps':steps,'reviewTests':checks,'reviewCases':review_cases(records,local_date(r['startedUtc'])),'coverage':{'covered':sum(g['covered'] for g in groups),'total':sum(g['total'] for g in groups),'modules':groups,'files':files,'measure':'行涵蓋率（line coverage），以 '+env['coverageTool']+' 量測後端的 C# 程式；前端的 JavaScript 未量測涵蓋率。'}}
data['additionalChecks']=[]
if args.excel_receipt:
 ep=Path(args.excel_receipt).resolve()
 if not ep.is_relative_to((ROOT/'artifacts/harness/runs').resolve()):raise ValueError('Use a harness Excel run')
 er=load(ep)
 if er['command']!='Excel' or er.get('privateData',{}).get('pathInspected') is not False:raise ValueError('Synthetic Excel evidence required')
 fixture=next((s['excelFixture'] for s in er['steps'] if 'excelFixture' in s),{})
 formats=fixture.get('nativeSourceImports',[])
 books=[s for s in er['steps'] if s['name'] in ['excel-accountMapping','excel-criteriaSelectionReport','excel-infReport','excel-prescreenReport','excel-validationReport','excel-workingPaper']]
 data['additionalChecks'].append({'name':'Excel 驗證','status':er['status'],'receipt':ep.relative_to(ROOT).as_posix(),'workbooks':len(books),'workbooksPassed':sum(s['status']=='passed' for s in books),'nativeImports':len(formats),'nativeImportsPassed':sum(s['status']=='passed' for s in formats)})
(SOURCE/'test-record.json').write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n',encoding='utf-8',newline='\n')
print(json.dumps({'status':data['status'],'tests':steps,'covered':data['coverage']['covered'],'lines':data['coverage']['total']},ensure_ascii=False))
