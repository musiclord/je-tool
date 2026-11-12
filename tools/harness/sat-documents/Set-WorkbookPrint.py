"""Set print-only OOXML properties not exposed by the workbook authoring API, and neutral package metadata."""
from pathlib import Path
import zipfile,io,re
from lxml import etree as E
SAT=Path(__file__).resolve().parents[3]/'docs'/'SAT'
N='http://schemas.openxmlformats.org/spreadsheetml/2006/main'
A='http://schemas.openxmlformats.org/drawingml/2006/main'
ns={'s':N}
order='sheetPr dimension sheetViews sheetFormatPr cols sheetData sheetCalcPr sheetProtection protectedRanges scenarios autoFilter sortState dataConsolidate customSheetViews mergeCells phoneticPr conditionalFormatting dataValidations hyperlinks printOptions pageMargins pageSetup headerFooter rowBreaks colBreaks customProperties cellWatches ignoredErrors smartTags drawing legacyDrawing legacyDrawingHF picture oleObjects controls webPublishItems tableParts extLst'.split()
def insert(root,node):
 rank=order.index(E.QName(node).localname)
 for i,c in enumerate(root):
  name=E.QName(c).localname
  if name in order and order.index(name)>rank:root.insert(i,node);return
 root.append(node)
# Workbook key -> path under docs/SAT. The keys are used by the print rules below.
BOOKS={'Code review':'Code Review/Code review','Unit test Report':'Code Review/Unit test Report','版本更新紀錄':'版本更新紀錄'}
# Sheet name -> last printed column. A sheet that is not listed here stops the script, so a renamed sheet cannot print with the wrong range.
LAST={'複核紀錄':'D','複核範圍':'E','固定值與規則':'G','測試結果':'F','核心審計規則':'E','程式碼涵蓋率':'D','版本更新紀錄':'C'}
# Landscape sheets, and sheets that must fit on one page. Every other sheet is one page wide and as many pages tall as it needs.
WIDE=['複核範圍','固定值與規則']
ONE_PAGE=['複核範圍']
# Sheet name -> text in column A of the heading row that repeats at the top of every printed page.
TITLE_ROW={'固定值與規則':'編號','複核紀錄':'複核規則'}
# Sheet name -> text in column A; the first row with that text starts a new printed page, so no table is cut at an arbitrary row.
BREAK_BEFORE={'複核紀錄':'4. Supporting Review Rules'}
# Sheet name -> columns whose text is indented one step from the heading row down, so it does not touch the text of the column before it.
INDENT={'固定值與規則':['D']}
# Page footer of every sheet, centred, with the same wording, font and colour as the footer of the Word documents.
# The footer keeps its own size when the sheet is scaled to the page width.
FOOTER='&C&"Microsoft JhengHei,Regular"&9&K5B6773第 &P 頁，共 &N 頁'
# The authoring package leaves a font that Windows and Office do not have as the workbook default (the Normal style).
# Excel shows that name for every cell outside the tables, so the default is set to the font the tables use.
FONT='Microsoft JhengHei'
# Excel measures a column width in digits of the default font. The authoring package stores a width as pixels / 8,
# and a digit of the font above is 8.5 px wide at 11 pt, so the stored widths are rescaled when the default font is set;
# the columns then keep the pixel widths the row heights were computed for.
PACKAGE_DIGIT,FONT_DIGIT=8,8.5
# The authoring package names the embedded theme after itself. The colours and fonts are the Office defaults, so the theme is named Office.
THEME_NAME='Office'
# Product names that must not appear anywhere inside a delivered workbook.
FORBIDDEN=['chatgpt','openai','codex','claude','anthropic','copilot']
# Document properties that name a person or organisation; when the package has them they are set to the product name or left empty.
CORE={'{http://purl.org/dc/elements/1.1/}creator':'JET','{http://schemas.openxmlformats.org/package/2006/metadata/core-properties}lastModifiedBy':'JET'}
APP=['Company','Manager']
def column_a(root,strings):
 # Row number -> text of the cell in column A.
 found={}
 for c in root.findall('.//s:sheetData/s:row/s:c',ns):
  if not re.fullmatch(r'A\d+',c.get('r') or ''):continue
  v=c.find('s:v',ns);inline=c.find('s:is',ns)
  if c.get('t')=='s' and v is not None:text=strings[int(v.text)]
  elif inline is not None:text=''.join(inline.itertext())
  else:text=v.text if v is not None else ''
  found[int(c.get('r')[1:])]=text or ''
 return found
def row_of(texts,wanted,sheet):
 rows=sorted(r for r,t in texts.items() if t==wanted)
 if not rows:raise RuntimeError('Row not found on '+sheet+': '+wanted)
 return rows[0]
def indented(styles,made,index):
 # Index of a cell format equal to the given one plus a left indent of one step; each format is copied once.
 if index not in made:
  formats=styles.find('s:cellXfs',ns)
  copy=E.fromstring(E.tostring(formats[index]))
  align=copy.find('s:alignment',ns)
  if align is None:align=E.Element('{'+N+'}alignment');copy.insert(0,align)
  align.set('horizontal','left');align.set('indent','1');copy.set('applyAlignment','1')
  formats.append(copy);formats.set('count',str(len(formats)));made[index]=len(formats)-1
 return made[index]
for name,relative in BOOKS.items():
 p=SAT/(relative+'.xlsx')
 with zipfile.ZipFile(p) as z: infos=z.infolist();data={i.filename:z.read(i) for i in infos}
 strings=[''.join(si.itertext()) for si in E.fromstring(data['xl/sharedStrings.xml']).findall('s:si',ns)] if 'xl/sharedStrings.xml' in data else []
 book=E.fromstring(data['xl/workbook.xml']);rels={r.get('Id'):r.get('Target') for r in E.fromstring(data['xl/_rels/workbook.xml.rels'])}
 styles=E.fromstring(data['xl/styles.xml']);made={}
 fonts=styles.findall('s:fonts/s:font',ns)
 if not fonts or fonts[0].find('s:name',ns) is None:raise RuntimeError('Default font not found in '+relative+'.xlsx')
 # A workbook that already has the default font was rescaled by an earlier run.
 rescale=fonts[0].find('s:name',ns).get('val')!=FONT
 fonts[0].find('s:name',ns).set('val',FONT)
 other=sorted({f.find('s:name',ns).get('val') for f in fonts if f.find('s:name',ns) is not None}-{FONT})
 if other:raise RuntimeError('Unexpected font in '+relative+'.xlsx: '+', '.join(other))
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
  root=E.fromstring(data[part]);end=1
  for c in root.findall('.//s:sheetData/s:row/s:c',ns):
   if any(E.QName(ch).localname in ['v','is','f'] for ch in c):end=max(end,int(re.search(r'\d+',c.get('r'))[0]))
  for m in root.findall('s:mergeCells/s:mergeCell',ns):end=max(end,int(re.search(r'\d+$',m.get('ref'))[0]))
  sheet=sh.get('name')
  if sheet not in LAST:raise RuntimeError('Sheet has no print rule: '+sheet)
  last=LAST[sheet]
  wide=sheet in WIDE
  for tag in ['pageMargins','pageSetup','headerFooter','rowBreaks']:
   existing=root.find('s:'+tag,ns)
   if existing is not None:root.remove(existing)
  pr=root.find('s:sheetPr',ns)
  if pr is None:pr=E.Element('{'+N+'}sheetPr');root.insert(0,pr)
  setup=pr.find('s:pageSetUpPr',ns)
  if setup is None:setup=E.SubElement(pr,'{'+N+'}pageSetUpPr')
  setup.set('fitToPage','1')
  # Excel shows a sheet that has no sheet view at half its stored row height, which cuts off wrapped text.
  if root.find('s:sheetViews',ns) is None:
   views=E.Element('{'+N+'}sheetViews');E.SubElement(views,'{'+N+'}sheetView',workbookViewId='0');insert(root,views)
  # The bottom margin leaves room for the page footer below the last printed row.
  insert(root,E.Element('{'+N+'}pageMargins',left='0.3',right='0.3',top='0.35',bottom='0.5',header='0.15',footer='0.2'))
  insert(root,E.Element('{'+N+'}pageSetup',paperSize='9',orientation='landscape' if wide else 'portrait',fitToWidth='1',fitToHeight='1' if sheet in ONE_PAGE else '0'))
  if rescale:
   for column in root.findall('s:cols/s:col',ns):column.set('width',format(float(column.get('width'))*PACKAGE_DIGIT/FONT_DIGIT,'.4f'))
  footer=E.Element('{'+N+'}headerFooter',scaleWithDoc='0')
  E.SubElement(footer,'{'+N+'}oddFooter').text=FOOTER
  insert(root,footer)
  texts=column_a(root,strings)
  for letter in INDENT.get(sheet,[]):
   if sheet not in TITLE_ROW:raise RuntimeError('Indented sheet has no heading row: '+sheet)
   head=row_of(texts,TITLE_ROW[sheet],sheet);count=0
   for c in root.findall('.//s:sheetData/s:row/s:c',ns):
    hit=re.fullmatch(r'([A-Z]+)(\d+)',c.get('r') or '')
    if hit and hit[1]==letter and head<=int(hit[2])<=end:c.set('s',str(indented(styles,made,int(c.get('s','0')))));count+=1
   if count!=end-head+1:raise RuntimeError('Indent column is incomplete on '+sheet+': '+letter)
  if sheet in BREAK_BEFORE:
   first=row_of(texts,BREAK_BEFORE[sheet],sheet)
   breaks=E.Element('{'+N+'}rowBreaks',count='1',manualBreakCount='1')
   E.SubElement(breaks,'{'+N+'}brk',id=str(first-1),max='16383',man='1')
   insert(root,breaks)
  sname="'"+sheet.replace("'","''")+"'!"
  E.SubElement(defined,'{'+N+'}definedName',name='_xlnm.Print_Area',localSheetId=str(idx)).text=sname+'$A$1:$'+last+'$'+str(end)
  if sheet in TITLE_ROW:
   head=str(row_of(texts,TITLE_ROW[sheet],sheet))
   E.SubElement(defined,'{'+N+'}definedName',name='_xlnm.Print_Titles',localSheetId=str(idx)).text=sname+'$'+head+':$'+head
  data[part]=E.tostring(root,xml_declaration=True,encoding='UTF-8',standalone=True)
 data['xl/workbook.xml']=E.tostring(book,xml_declaration=True,encoding='UTF-8',standalone=True)
 data['xl/styles.xml']=E.tostring(styles,xml_declaration=True,encoding='UTF-8',standalone=True)
 for member in [m for m in data if re.fullmatch(r'xl/theme/theme\d+\.xml',m)]:
  theme=E.fromstring(data[member])
  for node in [theme]+[theme.find('{'+A+'}themeElements/{'+A+'}'+tag) for tag in ['clrScheme','fontScheme','fmtScheme']]:
   if node is None:raise RuntimeError('Theme part is incomplete: '+member)
   node.set('name',THEME_NAME)
  data[member]=E.tostring(theme,xml_declaration=True,encoding='UTF-8',standalone=True)
 if 'docProps/core.xml' in data:
  core=E.fromstring(data['docProps/core.xml'])
  for tag,value in CORE.items():
   for node in core.iter(tag):node.text=value
  data['docProps/core.xml']=E.tostring(core,xml_declaration=True,encoding='UTF-8',standalone=True)
 if 'docProps/app.xml' in data:
  app=E.fromstring(data['docProps/app.xml'])
  for node in app:
   if E.QName(node).localname in APP:node.text=''
  data['docProps/app.xml']=E.tostring(app,xml_declaration=True,encoding='UTF-8',standalone=True)
 for member,content in data.items():
  text=(member+'\n'+content.decode('utf-8',errors='replace')).lower()
  hits=[word for word in FORBIDDEN if word in text]
  if hits:raise RuntimeError('Product name left in '+relative+'.xlsx, '+member+': '+', '.join(hits))
 b=io.BytesIO()
 with zipfile.ZipFile(b,'w') as z:
  for i in infos:z.writestr(i,data[i.filename])
 p.write_bytes(b.getvalue());print(relative,'print setup ready')
