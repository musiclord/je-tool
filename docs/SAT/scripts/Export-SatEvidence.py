"""Read a Public test receipt and measured C# coverage into the SAT workbook source."""
from pathlib import Path
import sys,json,xml.etree.ElementTree as E,collections,hashlib,argparse
ROOT=Path(__file__).resolve().parents[3]
SAT=ROOT/'docs/SAT'
def load(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def inside(rel,bound):
 p=(ROOT/rel).resolve()
 if not p.is_relative_to(bound.resolve()):raise ValueError('Evidence outside selected run')
 return p
parser=argparse.ArgumentParser()
parser.add_argument('receipt')
parser.add_argument('--excel-receipt')
args=parser.parse_args()
receipt_path=Path(args.receipt).resolve()
if not receipt_path.is_relative_to((ROOT/'artifacts/harness/runs').resolve()):raise ValueError('Use a harness run')
run=receipt_path.parent
r=load(receipt_path)
if r['command']!='Public' or r.get('privateData',{}).get('pathInspected') is not False:raise ValueError('Public synthetic evidence required')
steps=[]; records=[]; coverage_file=None
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
 group=name.split('/')[0] if '/' in name else '桌面啟動'
 covered=sum(lines.values());total=len(lines)
 modules[group][0]+=covered;modules[group][1]+=total
 files.append({'path':'src/JET/JET/'+name,'covered':covered,'total':total,'rate':covered/total if total else None})
groups=[]
for module,(covered,total) in sorted(modules.items()):
 groups.append({'module':module,'covered':covered,'total':total,'rate':covered/total if total else None})
review=load(SAT/'source/code-review.json')
checks=[]
for row in review['rows']:
 names=row['tests'].split('、')
 selected=[x for x in records if any('.'+name+'.' in x['fqn'] for name in names)]
 checks.append({'id':row['id'],'title':row['title'],'classes':names,'total':len(selected),'passed':sum(x['outcome']=='Passed' for x in selected),'failed':sum(x['outcome']=='Failed' for x in selected)})
data={'title':'JET 自動測試與交接','status':r['status'],'runId':r['runId'],'receipt':receipt_path.relative_to(ROOT).as_posix(),'configuration':r['configuration'],'command':'pwsh -NoProfile -File tools/verify.ps1 -Command Public -CollectCoverage','scope':'SQLite；合成資料；開發工作目錄。公司選定送審版本後須重跑。','steps':steps,'reviewTests':checks,'coverage':{'covered':sum(g['covered'] for g in groups),'total':sum(g['total'] for g in groups),'modules':groups,'files':files,'method':'依 JET 組件的來源檔及行號去重，合併命中；排除 obj、bin 產生碼。只計本次涵蓋率檔有列出的 C# 程式，不含 JavaScript、原生套件或人工審計。'},'unperformed':['公司正式環境與真實案件驗收','人工審計規則複核','公司 GitHub 掃描與核准']}
data['additionalChecks']=[]
if args.excel_receipt:
 ep=Path(args.excel_receipt).resolve()
 if not ep.is_relative_to((ROOT/'artifacts/harness/runs').resolve()):raise ValueError('Use a harness Excel run')
 er=load(ep)
 if er['command']!='Excel' or er.get('privateData',{}).get('pathInspected') is not False:raise ValueError('Synthetic Excel evidence required')
 fixture=next((s['excelFixture'] for s in er['steps'] if 'excelFixture' in s),{})
 formats=fixture.get('nativeSourceImports',[])
 books=[s for s in er['steps'] if s['name'] in ['excel-accountMapping','excel-criteriaSelectionReport','excel-infReport','excel-prescreenReport','excel-validationReport','excel-workingPaper']]
 data['additionalChecks'].append({'name':'原生 Excel 合成驗證','status':er['status'],'receipt':ep.relative_to(ROOT).as_posix(),'workbooks':len(books),'workbooksPassed':sum(s['status']=='passed' for s in books),'nativeImports':len(formats),'nativeImportsPassed':sum(s['status']=='passed' for s in formats)})
(SAT/'source/test-record.json').write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'status':data['status'],'tests':steps,'covered':data['coverage']['covered'],'lines':data['coverage']['total']},ensure_ascii=False))
