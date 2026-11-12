import fs from 'node:fs/promises';
import path from 'node:path';
import { Workbook, SpreadsheetFile } from '@oai/artifact-tool';
const repo=path.resolve(process.argv[2]||process.cwd());
const sat=path.join(repo,'docs/SAT'), source=path.join(repo,'tools/harness/sat-documents/source');
const preview=path.join(process.env.TEMP||repo,'jet-sat-20261106','sat-preview');
await fs.mkdir(preview,{recursive:true});
const read=async n=>JSON.parse(await fs.readFile(path.join(source,n),'utf8'));
const review=await read('code-review.json'), tests=await read('test-record.json'), releases=await read('release-notes.json');
const product='JET '+releases.rows[releases.rows.length-1].version;
const ink='#24323D', navy='#24475D', light='#E8EFF3', yellow='#FFF3CA';
const col=i=>String.fromCharCode(65+i);
// Paths are written from the folder that holds JET.slnx, which is what gets submitted.
// A path wider than the column breaks after its last folder, so the file name stays in one piece.
const pathText=(value,width)=>value.split('；').map(x=>{
 const full='JET/'+x, cut=full.lastIndexOf('/')+1;
 return lines(full,width)>1?full.slice(0,cut)+'\n'+full.slice(cut):full;
}).join('\n');
// Lines a text needs in a column of the given pixel width: a full-width character takes about 16 px at 11 pt, others about half.
// Line breaks already in the text are counted as written.
function lines(text,width,wide=16,narrow=8.2){
 return String(text).split('\n').reduce((n,part)=>n+Math.max(1,Math.ceil([...part].reduce((w,ch)=>w+(ch.charCodeAt(0)>0x2E7F?wide:narrow),0)/(width-12))),0);
}
// Row height in points for the given number of text lines.
const tall=(count,least)=>Math.max(least,count*20+12);
function base(w,name,title,widths,freeze=4){
 const s=w.worksheets.add(name);s.showGridLines=false;
 const last=col(widths.length-1);
 const all=s.getRange('A1:'+last+'100');all.format.font={name:'Microsoft JhengHei',size:11,color:ink};all.format.rowHeight=24;all.format.wrapText=true;all.format.verticalAlignment='center';
 widths.forEach((width,i)=>s.getRange(col(i)+'1:'+col(i)+'100').format.columnWidthPx=width);
 s.getRange('A1:'+last+'2').merge();s.getRange('A1').values=[[title]];s.getRange('A1:'+last+'2').format.fill=navy;s.getRange('A1:'+last+'2').format.font={name:'Microsoft JhengHei',size:18,bold:true,color:'#FFFFFF'};
 s.freezePanes.freezeRows(freeze);s.tabColor=navy;return s;
}
function block(s,range,text,fill){
 s.getRange(range).merge();s.getRange(range.split(':')[0]).values=[[text]];
 if(fill)s.getRange(range).format.fill=fill;
 if(fill===yellow)s.getRange(range).format.borders={preset:'outside',style:'thin',color:'#C9BA8A'};
}
// One labelled line: the label in column A, the value merged across the remaining columns.
function field(s,row,label,value,last,rows=1){
 const end=row+rows-1;
 block(s,'A'+row+':A'+end,label,light);
 block(s,'B'+row+':'+last+end,value===null?'':value,value===null?yellow:undefined);
 return end+1;
}
// One labelled line whose value runs over several lines of text: a single row as tall as the text needs.
// Merging 24 pt rows instead leaves the text with an empty band above and below it.
function textField(s,row,label,value,last,width){
 block(s,'A'+row+':A'+row,label,light);
 block(s,'B'+row+':'+last+row,value);
 s.getRange('A'+row+':'+last+row).format.rowHeight=Math.max(24,lines(value,width,15,7.4)*16+8);
 return row+1;
}
// Rows of 24 pt that a merged value needs when it is as wide as the given pixel width.
// These values are mostly commands and tool names; the generous character widths used for table rows would leave them several empty rows.
const rowsFor=(text,width)=>Math.max(1,Math.ceil((lines(text,width,15,7.4)*19+6)/24));
function table(s,row,heads,rows,height=54){
 const end=col(heads.length-1);s.getRange('A'+row+':'+end+row).values=[heads];s.getRange('A'+row+':'+end+row).format.fill=light;s.getRange('A'+row+':'+end+row).format.font.bold=true;s.getRange('A'+row+':'+end+row).format.rowHeight=30;
 if(rows.length){const r=s.getRange('A'+(row+1)+':'+end+(row+rows.length));r.values=rows;r.format.borders={insideHorizontal:{style:'thin',color:'#DBE2E7'}};r.format.rowHeight=height;}
 return row+rows.length;
}
// Number columns: the heading and the numbers share one alignment, and counts show a thousands separator.
function numbers(s,columns,head,last,alignment){
 for(const c of columns){
  s.getRange(c+head+':'+c+last).format.horizontalAlignment=alignment;
  s.getRange(c+(head+1)+':'+c+last).setNumberFormat('#,##0');
 }
}
function fill(s,range,values){
 s.getRange(range).format.fill=yellow;
 if(values)s.getRange(range).dataValidation={rule:{type:'list',values}};
}
async function save(w,name,ranges){
 w.recalculate();
 const e=await w.inspect({kind:'match',searchTerm:'#REF!|#DIV/0!|#VALUE!|#NAME\\?|#NUM!',options:{useRegex:true,maxResults:20},maxChars:1000});
 console.log(name,e.ndjson);
 const flat=path.basename(name);
 for(const [sheetName,range] of ranges){
 const img=await w.render({sheetName,range,scale:1.5,format:'png'});
 await fs.writeFile(path.join(preview,flat+'-'+sheetName+'.png'),new Uint8Array(await img.arrayBuffer()));
 }
 await fs.mkdir(path.dirname(path.join(sat,name)),{recursive:true});
 const out=await SpreadsheetFile.exportXlsx(w);await out.save(path.join(sat,name+'.xlsx'));
 await fs.rename(path.join(sat,name+'.xlsx.inspect.ndjson'),path.join(preview,flat+'.inspect.ndjson')).catch(()=>{});
 console.log('Saved',name,ranges.length,'sheets');
}
{
 // Code review record. The layout follows section 6 of the code review rule:
 // what the developer submits, the checks the reviewer makes, the findings, and the recorded decision.
 const w=Workbook.create(),ranges=[];
 const rw=[200,420,120,200];
 const s=base(w,'複核紀錄',review.title,rw,2);
 let row=4;
 row=field(s,row,'軟體與版本',product,'D');
 row=field(s,row,'變更說明',review.change,'D',2);
 row=field(s,row,'需求依據',review.requirement,'D');
 row=field(s,row,'單元測試報告','Unit test Report.xlsx','D');
 row=field(s,row,'提交識別碼（commit ID）',null,'D');
 row=field(s,row,'開發人員',null,'D');
 row=field(s,row,'送交複核日期',null,'D');
 const checks=table(s,row+1,['複核規則','檢查項目','結果','備註'],review.checklist.map(c=>[c.rule,c.item,null,null]),52);
 review.checklist.forEach((c,i)=>{
  s.getRange('A'+(row+2+i)+':D'+(row+2+i)).format.rowHeight=tall(Math.max(lines(c.rule,rw[0]),lines(c.item,rw[1])),44);
 });
 fill(s,'C'+(row+2)+':C'+checks,['符合','不符合','不適用']);fill(s,'D'+(row+2)+':D'+checks);
 const findings=table(s,checks+2,['編號','發現事項與程式位置','處理方式','重新測試結果'],[1,2,3,4].map(()=>[null,null,null,null]),40);
 fill(s,'A'+(checks+3)+':D'+findings);
 row=findings+2;
 block(s,'A'+row+':A'+row,'結論',light);block(s,'B'+row+':D'+row,'',yellow);
 s.getRange('B'+row).dataValidation={rule:{type:'list',values:['通過','需修正','例外核准']}};
 row=field(s,row+1,'複核人員',null,'D');
 row=field(s,row,'複核日期',null,'D');
 row=field(s,row,'核准的提交識別碼',null,'D');
 row=field(s,row,'後續事項',null,'D',2);
 row=field(s,row,'例外核准事項',null,'D',2);
 ranges.push(['複核紀錄','A1:D'+(row-1)]);

 const fw=[170,400,380,110,200];
 const f=base(w,'複核範圍','核心審計規則與對應測試',fw);
 const scope=review.scope.map(x=>[x.title,pathText(x.path,fw[1]),x.tests.split('、').join('\n')]);
 const fe=table(f,4,['審計規則','程式位置','測試類別','複核結果','備註'],scope.map(x=>[...x,null,null]),64);
 scope.forEach((x,i)=>{
  f.getRange('A'+(5+i)+':E'+(5+i)).format.rowHeight=tall(Math.max(lines(x[1],fw[1]),lines(x[2],fw[2])),64);
 });
 fill(f,'D5:D'+fe,['符合','不符合']);fill(f,'E5:E'+fe);
 ranges.push(['複核範圍','A1:E'+fe]);

 const hw=[50,210,460,380,90,100,160];
 const h=base(w,'固定值與規則','程式內的固定值與規則',hw);
 const hard=review.hardCoded.map(x=>[x.id,x.item,x.content,pathText(x.path,hw[3]),x.origin]);
 const he=table(h,4,['編號','項目','內容','程式位置','來源','複核結果','備註'],hard.map(x=>[...x,null,null]),44);
 // Row height follows the longest cell of the row so no text is cut off when printed.
 hard.forEach((x,i)=>{
  h.getRange('A'+(5+i)+':G'+(5+i)).format.rowHeight=tall(Math.max(lines(x[1],hw[1]),lines(x[2],hw[2]),lines(x[3],hw[3])),44);
 });
 h.getRange('A4:A'+he).format.horizontalAlignment='center';
 fill(h,'F5:F'+he,['可接受','已修正','例外核准']);fill(h,'G5:G'+he);
 ranges.push(['固定值與規則','A1:G'+he]);
 await save(w,'Code Review/Code review',ranges);
}
{
 const w=Workbook.create(),ranges=[];
 const tw=[230,120,120,120,120,150], valueWidth=tw.slice(1).reduce((a,b)=>a+b,0);
 const s=base(w,'測試結果',tests.title,tw,2);
 const env=tests.environment;
 let row=4;
 row=field(s,row,'軟體與版本',product,'F');
 row=field(s,row,'提交識別碼（commit ID）',null,'F');
 row=field(s,row,'執行日期',tests.date,'F');
 row=textField(s,row,'測試環境',['作業系統：'+env.os,'.NET SDK：'+env.dotnetSdk,'測試框架：'+env.testFramework,'Node.js：'+env.node].join('\n'),'F',valueWidth);
 row=field(s,row,'測試資料',tests.scope,'F');
 row=textField(s,row,'執行命令',tests.command,'F',valueWidth);
 row=textField(s,row,'涵蓋率量測方式',tests.coverage.measure,'F',valueWidth);
 row=field(s,row,'驗收條件',tests.exitCriteria,'F');
 const names={'public-tests':'後端測試（tests/JET.Tests）','frontend-tests':'前端測試（tests/frontend）'};
 const reported=tests.steps.filter(x=>names[x.name]);
 row=field(s,row,'結果',reported.every(x=>x.status==='passed')?'通過':'未通過','F');
 const suites=reported.map(x=>[names[x.name],x.total,x.passed,x.failed,x.skipped,x.status==='passed'?'通過':'未通過']);
 const end=table(s,row+1,['測試專案','測試案例數','通過','失敗','略過','結果'],suites,30);
 numbers(s,['B','C','D','E'],row+1,end,'center');
 s.getRange('F'+(row+1)+':F'+end).format.horizontalAlignment='center';
 ranges.push([s.name,'A1:F'+end]);

 const gw=[190,110,90,90,420];
 const g=base(w,'核心審計規則','核心審計規則的測試結果',gw);
 const core=tests.reviewTests.map(r=>[r.title,r.total,r.passed,r.failed,r.classes.join('\n')]);
 const ge=table(g,4,['審計規則','測試案例數','通過','失敗','測試類別'],core,64);
 core.forEach((x,i)=>{
  g.getRange('A'+(5+i)+':E'+(5+i)).format.rowHeight=tall(lines(x[4],gw[4]),44);
 });
 numbers(g,['B','C','D'],4,ge,'center');
 ranges.push([g.name,'A1:E'+ge]);

 const cw=[340,130,130,130];
 const c=base(w,'程式碼涵蓋率','後端程式碼行涵蓋率',cw);
 const ce=table(c,4,['模組','已涵蓋行數','可執行行數','行涵蓋率'],tests.coverage.modules.map(m=>[m.module,m.covered,m.total,null]),30);
 tests.coverage.modules.forEach((m,i)=>{const n=5+i;c.getRange('D'+n).formulas=[['=B'+n+'/C'+n]];});
 const n=ce+1;
 c.getRange('A'+n+':C'+n).values=[['合計',tests.coverage.covered,tests.coverage.total]];c.getRange('D'+n).formulas=[['=B'+n+'/C'+n]];
 c.getRange('A'+n+':D'+n).format.font.bold=true;c.getRange('A'+n+':D'+n).format.rowHeight=30;
 numbers(c,['B','C','D'],4,n,'right');
 c.getRange('D5:D'+n).setNumberFormat('0.0%');
 const noteRows=rowsFor(tests.coverage.method,cw.reduce((a,b)=>a+b,0));
 block(c,'A'+(n+2)+':D'+(n+1+noteRows),tests.coverage.method);
 ranges.push([c.name,'A1:D'+(n+1+noteRows)]);
 await save(w,'Code Review/Unit test Report',ranges);
}
{
 const w=Workbook.create();
 const s=w.worksheets.add('版本更新紀錄');
 const vw=[120,420,110];
 const rows=releases.rows.map(r=>[r.date,r.description,r.version]);
 const all=s.getRange('A1:C'+(1+rows.length));all.format.font={name:'Microsoft JhengHei',size:11,color:ink};all.format.wrapText=true;all.format.verticalAlignment='top';
 vw.forEach((width,i)=>s.getRange(col(i)+'1:'+col(i)+(1+rows.length)).format.columnWidthPx=width);
 s.getRange('A1:C1').values=[['Date','Description','Version']];s.getRange('A1:C1').format.fill=light;s.getRange('A1:C1').format.font.bold=true;s.getRange('A1:C1').format.rowHeight=24;
 s.getRange('A2:C'+(1+rows.length)).values=rows;
 // Each row is as tall as its description needs, so every line shows.
 rows.forEach((r,i)=>{s.getRange('A'+(2+i)+':C'+(2+i)).format.rowHeight=tall(lines(r[1],vw[1]),24);});
 await save(w,'版本更新紀錄',[['版本更新紀錄','A1:C'+(1+rows.length)]]);
}
