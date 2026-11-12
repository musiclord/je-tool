"""Set print-only OOXML properties not exposed by the workbook authoring API."""
from pathlib import Path
import zipfile,io,re
from lxml import etree as E
SAT=Path(__file__).resolve().parents[3]/'docs'/'SAT'
N='http://schemas.openxmlformats.org/spreadsheetml/2006/main'
ns={'s':N}
order='sheetPr dimension sheetViews sheetFormatPr cols sheetData sheetCalcPr sheetProtection protectedRanges scenarios autoFilter sortState dataConsolidate customSheetViews mergeCells phoneticPr conditionalFormatting dataValidations hyperlinks printOptions pageMargins pageSetup headerFooter rowBreaks colBreaks customProperties cellWatches ignoredErrors smartTags drawing legacyDrawing legacyDrawingHF picture oleObjects controls webPublishItems tableParts extLst'.split()
def insert(root,node):
 rank=order.index(E.QName(node).localname)
 for i,c in enumerate(root):
  name=E.QName(c).localname
  if name in order and order.index(name)>rank:root.insert(i,node);return
 root.append(node)
# Workbook key -> path under docs/SAT. The keys are used by the print rules below.
BOOKS={'Code Review':'Code Review/Code review','測試紀錄表':'Code Review/Unit test Report','版本更新紀錄':'版本更新紀錄'}
# Sheet name -> last printed column.
LAST={'審查紀錄':'D','審查範圍':'E','固定值與規則':'G','測試結果':'F','功能測試':'E','程式涵蓋率':'D','版本更新紀錄':'C'}
for name,relative in BOOKS.items():
 p=SAT/(relative+'.xlsx')
 with zipfile.ZipFile(p) as z: infos=z.infolist();data={i.filename:z.read(i) for i in infos}
 book=E.fromstring(data['xl/workbook.xml']);rels={r.get('Id'):r.get('Target') for r in E.fromstring(data['xl/_rels/workbook.xml.rels'])}
 defined=book.find('s:definedNames',ns)
 if defined is None:
  defined=E.Element('{'+N+'}definedNames')
  calc=book.find('s:calcPr',ns)
  if calc is not None:book.insert(book.index(calc),defined)
  else:book.append(defined)
 for old in list(defined):
  if old.get('name') in ['_xlnm.Print_Area','_xlnm.Print_Titles']:defined.remove(old)
 for idx,sh in enumerate(book.find('s:sheets',ns)):
  part=rels[sh.get('{http://schemas.openxmlformats.org/officeDocument/2006/relationships}id')]
  part=part.lstrip('/') if part.startswith('/') else 'xl/'+part
  root=E.fromstring(data[part]);end=4
  for c in root.findall('.//s:sheetData/s:row/s:c',ns):
   if any(E.QName(ch).localname in ['v','is','f'] for ch in c):end=max(end,int(re.search(r'\d+',c.get('r'))[0]))
  for m in root.findall('s:mergeCells/s:mergeCell',ns):end=max(end,int(re.search(r'\d+$',m.get('ref'))[0]))
  last=LAST.get(sh.get('name'),'F')
  wide=sh.get('name') in ['審查範圍','固定值與規則']
  for tag in ['pageMargins','pageSetup']:
   existing=root.find('s:'+tag,ns)
   if existing is not None:root.remove(existing)
  pr=root.find('s:sheetPr',ns)
  if pr is None:pr=E.Element('{'+N+'}sheetPr');root.insert(0,pr)
  setup=pr.find('s:pageSetUpPr',ns)
  if setup is None:setup=E.SubElement(pr,'{'+N+'}pageSetUpPr')
  setup.set('fitToPage','1')
  insert(root,E.Element('{'+N+'}pageMargins',left='0.3',right='0.3',top='0.35',bottom='0.35',header='0.15',footer='0.15'))
  insert(root,E.Element('{'+N+'}pageSetup',paperSize='9',orientation='landscape' if wide else 'portrait',fitToWidth='1',fitToHeight='1' if sh.get('name') in ['審查紀錄','審查範圍'] else '0'))
  sname="'"+sh.get('name').replace("'","''")+"'!"
  E.SubElement(defined,'{'+N+'}definedName',name='_xlnm.Print_Area',localSheetId=str(idx)).text=sname+'$A$1:$'+last+'$'+str(end)
  if sh.get('name')=='固定值與規則':E.SubElement(defined,'{'+N+'}definedName',name='_xlnm.Print_Titles',localSheetId=str(idx)).text=sname+'$4:$4'
  data[part]=E.tostring(root,xml_declaration=True,encoding='UTF-8',standalone=True)
 data['xl/workbook.xml']=E.tostring(book,xml_declaration=True,encoding='UTF-8',standalone=True)
 b=io.BytesIO()
 with zipfile.ZipFile(b,'w') as z:
  for i in infos:z.writestr(i,data[i.filename])
 p.write_bytes(b.getvalue());print(relative,'print setup ready')
