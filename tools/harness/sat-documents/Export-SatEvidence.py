"""Read test results, the test environment and measured C# coverage into the SAT workbook source.

Two inputs are accepted: a harness Public receipt (collected with -CollectCoverage), or the TRX and
Cobertura files written by running JET.Tests.exe directly with the documented filters, together with the
JUnit file written by node --test for tests/frontend (--trx/--coverage/--frontend-junit).
"""
from pathlib import Path
import sys,json,xml.etree.ElementTree as E,collections,hashlib,argparse,platform,re,subprocess
ROOT=Path(__file__).resolve().parents[3]
SOURCE=Path(__file__).resolve().parent/'source'
def load(p):return json.loads(p.read_text(encoding='utf-8-sig'))
# 方案資料夾底下的三行命令，和技術文件第 14 節的程式碼區塊相同；路徑以 JET.slnx 所在的資料夾為起點。
COMMANDS=[
 'dotnet build JET.slnx -c Debug',
 'tests/JET.Tests/bin/Debug/net10.0-windows/JET.Tests.exe --filter-not-trait "TestProfile=PrivateCase" "TestProfile=Scale" "TestProfile=DevelopmentRepository"',
 'node --test --test-concurrency=4 tests/frontend/',
]
# 量測涵蓋率時，後端測試那一行命令另外加上的三個參數；最後一個後面接輸出檔的位置。
COVERAGE_ARGUMENTS='--coverage、--coverage-output-format cobertura 與 --coverage-output'
# 涵蓋率表裡直接放在 JET 資料夾、不屬於任何子資料夾的那一組原始檔；技術文件第 6 節把這一層稱為 WinForms 主機。
ROOT_MODULE='WinForms 主機（JET 資料夾根目錄的原始檔）'
def tool_version(*command):
 # 轉換當下實際查到的版本；查不到就停止，不填推測的值。
 text=subprocess.run(command,cwd=ROOT,capture_output=True,text=True,check=True).stdout.strip()
 if not text:raise ValueError('No version from '+command[0])
 return text.lstrip('v')
def package_version(name):
 project=(ROOT/'src/JET/tests/JET.Tests/JET.Tests.csproj').read_text(encoding='utf-8-sig')
 hit=re.search(r'<PackageReference Include="'+re.escape(name)+r'" Version="([^"]+)"',project)
 if not hit:raise ValueError('Package not referenced: '+name)
 return hit[1]
def environment():
 # 測試與轉換在同一台電腦執行；作業系統與工具版本取轉換當下的值，套件版本取測試專案檔。
 if platform.system()!='Windows':raise ValueError('Run on the Windows machine that ran the tests')
 edition={'Professional':'專業版','Enterprise':'企業版','Core':'家用版'}.get(platform.win32_edition(),platform.win32_edition())
 bits='64 位元' if platform.machine().endswith('64') else platform.machine()
 return {
  'os':'Windows '+platform.release()+' '+edition+' '+bits+'（'+platform.version()+'）',
  'dotnetSdk':tool_version('dotnet','--version'),
  'testFramework':'xUnit v3（xunit.v3.mtp-v2 '+package_version('xunit.v3.mtp-v2')+'）',
  'node':tool_version('node','--version'),
  'coverageTool':'Microsoft.Testing.Extensions.CodeCoverage '+package_version('Microsoft.Testing.Extensions.CodeCoverage'),
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
args=parser.parse_args()
NS='{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'
steps=[]; records=[]; coverage_file=None
if args.trx:
 # 直接執行測試程式的結果：排除的標記由執行命令決定，這裡只讀結果檔。
 if args.receipt or not args.coverage or not args.frontend_junit:raise ValueError('Use --trx together with --coverage and --frontend-junit, without a receipt')
 trx_root=E.parse(args.trx).getroot()
 counter=trx_root.find('.//'+NS+'Counters')
 total,executed,passed,failed=(int(counter.get(k)) for k in ['total','executed','passed','failed'])
 if total==0:raise ValueError('TRX has no tests')
 names={d.get('id'):d.find(NS+'TestMethod').get('className')+'.'+d.find(NS+'TestMethod').get('name') for d in trx_root.iter(NS+'UnitTest')}
 records=[{'fqn':names[u.get('testId')],'outcome':u.get('outcome')} for u in trx_root.iter(NS+'UnitTestResult')]
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
review=load(SOURCE/'code-review.json')
env=environment()
checks=[]
for row in review['scope']:
 names=row['tests'].split('、')
 selected=[x for x in records if any('.'+name+'.' in x['fqn'] for name in names)]
 checks.append({'title':row['title'],'classes':names,'total':len(selected),'passed':sum(x['outcome']=='Passed' for x in selected),'failed':sum(x['outcome']=='Failed' for x in selected)})
data={'title':'JET 單元測試報告','date':r['startedUtc'][:10],'status':r['status'],'runId':r['runId'],'receipt':receipt_path.relative_to(ROOT).as_posix() if receipt_path else '','revision':(r.get('repository') or {}).get('head','')[:7],'configuration':r['configuration'],'command':'\n'.join(COMMANDS),'scope':'全部測試使用合成測試資料。','environment':env,'exitCriteria':'全部測試案例通過，無失敗與略過。','steps':steps,'reviewTests':checks,'coverage':{'covered':sum(g['covered'] for g in groups),'total':sum(g['total'] for g in groups),'modules':groups,'files':files,'measure':'行涵蓋率（line coverage），以 '+env['coverageTool']+' 量測。\n後端測試命令另加 '+COVERAGE_ARGUMENTS+'。','method':'計算範圍為後端原始碼，不含編譯時自動產生的程式碼與原生程式庫；依原始檔與行號計算。'}}
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
