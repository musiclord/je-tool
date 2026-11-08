"""Set print-only OOXML properties not exposed by the workbook authoring API."""
from pathlib import Path
import zipfile,io,re
from lxml import etree as E
SAT=Path(__file__).resolve().parents[1]
N='http://schemas.openxmlformats.org/spreadsheetml/2006/main'
ns={'s':N}
order='sheetPr dimension sheetViews sheetFormatPr cols sheetData sheetCalcPr sheetProtection protectedRanges scenarios autoFilter sortState dataConsolidate customSheetViews mergeCells phoneticPr conditionalFormatting dataValidations hyperlinks printOptions pageMargins pageSetup headerFooter rowBreaks colBreaks customProperties cellWatches ignoredErrors smartTags drawing legacyDrawing legacyDrawingHF picture oleObjects controls webPublishItems tableParts extLst'.split()
def insert(root,node):
 rank=order.index(E.QName(node).localname)
 for i,c in enumerate(root):
  name=E.QName(c).localname
  if name in order and order.index(name)>rank:root.insert(i,node);return
 root.append(node)
for name in ['Code Review','Code and Explanation','測試紀錄表','第三方元件','Workflow']:
 p=SAT/(name+'.xlsx')
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
  last='E' if (name=='Code Review' and sh.get('name')=='審查總表') or (name=='測試紀錄表' and sh.get('name') in ['程式涵蓋率','未覆蓋重點']) else 'F'
  if name=='Workflow':end=29
  for tag in ['pageMargins','pageSetup']:
   existing=root.find('s:'+tag,ns)
   if existing is not None:root.remove(existing)
  pr=root.find('s:sheetPr',ns)
  if pr is None:pr=E.Element('{'+N+'}sheetPr');root.insert(0,pr)
  setup=pr.find('s:pageSetUpPr',ns)
  if setup is None:setup=E.SubElement(pr,'{'+N+'}pageSetUpPr')
  setup.set('fitToPage','1')
  insert(root,E.Element('{'+N+'}pageMargins',left='0.3',right='0.3',top='0.35',bottom='0.35',header='0.15',footer='0.15'))
  insert(root,E.Element('{'+N+'}pageSetup',paperSize='8' if name=='第三方元件' else '9',orientation='landscape' if name in ['Workflow','第三方元件'] else 'portrait',fitToWidth='1',fitToHeight='1' if name in ['Workflow','Code Review'] or (name=='Code and Explanation' and sh.get('name')=='說明1') else '0'))
  sname="'"+sh.get('name').replace("'","''")+"'!"
  E.SubElement(defined,'{'+N+'}definedName',name='_xlnm.Print_Area',localSheetId=str(idx)).text=sname+'$A$1:$'+last+'$'+str(end)
  if name=='第三方元件':E.SubElement(defined,'{'+N+'}definedName',name='_xlnm.Print_Titles',localSheetId=str(idx)).text=sname+'$6:$6'
  if name=='Code and Explanation':E.SubElement(defined,'{'+N+'}definedName',name='_xlnm.Print_Titles',localSheetId=str(idx)).text=sname+'$1:$8'
  if name=='測試紀錄表' and sh.get('name')=='未覆蓋重點':E.SubElement(defined,'{'+N+'}definedName',name='_xlnm.Print_Titles',localSheetId=str(idx)).text=sname+'$1:$6'
  data[part]=E.tostring(root,xml_declaration=True,encoding='UTF-8',standalone=True)
 data['xl/workbook.xml']=E.tostring(book,xml_declaration=True,encoding='UTF-8',standalone=True)
 b=io.BytesIO()
 with zipfile.ZipFile(b,'w') as z:
  for i in infos:z.writestr(i,data[i.filename])
 p.write_bytes(b.getvalue());print(name,'print setup ready')
