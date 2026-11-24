import fs from 'node:fs/promises';
import path from 'node:path';
import { Workbook, SpreadsheetFile } from '@oai/artifact-tool';
const repo=path.resolve(process.argv[2]||process.cwd());
// An optional second argument names the one workbook to write, for example "Code Review/Code review"; the others are left as they are.
const only=process.argv[3]||null;
const sat=path.join(repo,'docs/SAT'), source=path.join(repo,'tools/harness/sat-documents/source');
const preview=path.join(process.env.TEMP||repo,'jet-sat-20261106','sat-preview');
await fs.mkdir(preview,{recursive:true});
const read=async n=>JSON.parse(await fs.readFile(path.join(source,n),'utf8'));
const review=await read('code-review.json'), tests=await read('test-record.json'), releases=await read('release-notes.json');
const product='JE Tool '+releases.rows[releases.rows.length-1].version;
const ink='#24323D', navy='#24475D', light='#E8EFF3', band='#F4F7F9', yellow='#FFF3CA';
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
function base(w,name,title,widths,rows=100){
 const s=w.worksheets.add(name);s.showGridLines=false;
 const last=col(widths.length-1);
 const all=s.getRange('A1:'+last+rows);all.format.font={name:'Microsoft JhengHei',size:11,color:ink};all.format.rowHeight=24;all.format.wrapText=true;all.format.verticalAlignment='center';
 widths.forEach((width,i)=>s.getRange(col(i)+'1:'+col(i)+rows).format.columnWidthPx=width);
 s.getRange('A1:'+last+'2').merge();s.getRange('A1').values=[[title]];s.getRange('A1:'+last+'2').format.fill=navy;s.getRange('A1:'+last+'2').format.font={name:'Microsoft JhengHei',size:18,bold:true,color:'#FFFFFF'};
 s.tabColor=navy;return s;
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
// A table whose rows come in groups: the columns listed in merged hold one value for the whole group, the others one value per row.
// A group may instead be a band, one row across the table that names the section the following groups belong to.
// Each row is as tall as its own cells need; when a merged value needs more, the last row of the group takes the difference.
// Returns the last row and the rows that hold values, so the reviewer's columns can be filled row by row.
function grouped(s,head,heads,widths,groups,merged,least){
 const end=col(heads.length-1);
 const h=s.getRange('A'+head+':'+end+head);h.values=[heads];h.format.fill=light;h.format.font.bold=true;h.format.rowHeight=30;
 let r=head+1;const data=[];
 for(const g of groups){
  if(g.band){
   block(s,'A'+r+':'+end+r,g.band,band);s.getRange('A'+r).format.font={name:'Microsoft JhengHei',size:11,bold:true,color:navy};
   s.getRange('A'+r+':'+end+r).format.rowHeight=28;r++;continue;
  }
  const first=r,heights=[];
  for(const values of g.rows){
   s.getRange('A'+r+':'+end+r).values=[values.map((v,i)=>merged.includes(i)?null:v)];
   heights.push(tall(Math.max(1,...values.map((v,i)=>merged.includes(i)||v==null?1:lines(v,widths[i]))),least));
   data.push(r);r++;
  }
  const need=tall(Math.max(1,...merged.map(i=>lines(g.rows[0][i],widths[i]))),least), sum=heights.reduce((a,b)=>a+b,0);
  if(sum<need)heights[heights.length-1]+=need-sum;
  heights.forEach((height,i)=>{s.getRange('A'+(first+i)+':'+end+(first+i)).format.rowHeight=height;});
  for(const i of merged)block(s,col(i)+first+':'+col(i)+(r-1),g.rows[0][i]);
  s.getRange('A'+first+':'+end+(r-1)).format.borders={top:{style:'thin',color:'#8FA3B0'},insideHorizontal:{style:'thin',color:'#DBE2E7'}};
 }
 return {end:r-1,data};
}
// Fills the given column on every data row of a grouped table, one run of adjacent rows at a time.
function fillRows(s,letter,rows,values){
 let start=null;
 rows.forEach((r,i)=>{
  if(start===null)start=r;
  if(rows[i+1]!==r+1){fill(s,letter+start+':'+letter+r,values);start=null;}
 });
}
async function save(w,name,ranges){
 if(only&&name!==only)return;
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
 const s=base(w,'複核紀錄',review.title,rw);
 let row=4;
 row=field(s,row,'軟體與版本',product,'D');
 row=field(s,row,'變更說明',review.change,'D');
 row=field(s,row,'需求依據',review.requirement,'D');
 row=field(s,row,'驗收依據與測試佐證',review.acceptance,'D');
 row=field(s,row,'人工複核的範圍',review.manualScope,'D',rowsFor(review.manualScope,rw[1]+rw[2]+rw[3]));
 row=field(s,row,'開發人員',null,'D');
 // The checks are grouped by section of the code review rule and by the rule's bold name; each check has its own result.
 const sections=[];let section=null;
 for(const c of review.checklist){
  if(c.rule!==section){sections.push({band:c.rule});section=c.rule;}
  sections.push({rows:c.items.map(item=>[c.name,item,null,null])});
 }
 const checks=grouped(s,row+1,['複核規則','檢查項目','結果','備註'],rw,sections,[0],40);
 fillRows(s,'C',checks.data,['符合','不符合','不適用']);fillRows(s,'D',checks.data);
 const findings=table(s,checks.end+2,['編號','發現事項與程式位置','處理方式','重新測試結果'],[1,2,3,4].map(()=>[null,null,null,null]),40);
 fill(s,'A'+(checks.end+3)+':D'+findings);
 row=findings+2;
 block(s,'A'+row+':A'+row,'結論',light);block(s,'B'+row+':D'+row,'',yellow);
 s.getRange('B'+row).dataValidation={rule:{type:'list',values:['通過','需修正','例外核准']}};
 row=field(s,row+1,'複核人員',null,'D');
 row=field(s,row,'複核日期',null,'D');
 row=field(s,row,'後續事項',null,'D',2);
 row=field(s,row,'例外核准事項',null,'D',2);
 ranges.push(['複核紀錄','A1:D'+(row-1)]);

 // One row for each review point of a logic; the logic, its program files and its test classes are written once for the group.
 const fw=[150,400,330,345,100,150];
 const f=base(w,'複核範圍','核心查核邏輯與對應測試',fw);
 const scope=grouped(f,4,['查核邏輯','複核重點','程式位置','測試類別','複核結果','備註'],fw,
  review.scope.map(x=>({rows:x.points.map(p=>[x.title,p,pathText(x.path,fw[2]),x.tests.split('、').join('\n'),null,null])})),[0,2,3],44);
 fillRows(f,'E',scope.data,['符合','不符合']);fillRows(f,'F',scope.data);
 ranges.push(['複核範圍','A1:F'+scope.end]);

 // One row for each value or rule of an item; the number, the item and its program files are written once for the group.
 const hw=[50,210,550,380,100,160];
 const h=base(w,'固定值與規則','程式內的固定值與規則',hw);
 const hard=grouped(h,4,['編號','項目','內容','程式位置','複核結果','備註'],hw,
  review.hardCoded.map(x=>({rows:x.details.map(d=>[x.id,x.item,d,pathText(x.path,hw[3]),null,null])})),[0,1,3],40);
 h.getRange('A4:A'+hard.end).format.horizontalAlignment='center';
 fillRows(h,'E',hard.data,['可接受','已修正','例外核准']);fillRows(h,'F',hard.data);
 ranges.push(['固定值與規則','A1:F'+hard.end]);

 await save(w,'Code Review/Code review',ranges);
}
{
 const w=Workbook.create(),ranges=[];
 const tw=[230,120,120,120,120,150], valueWidth=tw.slice(1).reduce((a,b)=>a+b,0);
 const s=base(w,'測試結果',tests.title,tw);
 const env=tests.environment;
 let row=4;
 row=field(s,row,'軟體與版本',product,'F');
 row=field(s,row,'執行日期',tests.date,'F');
 // The record keeps the major versions of the SDK and Node.js; they are written the way the technical document names them.
 row=textField(s,row,'測試環境',['作業系統：'+env.os,'建置工具：.NET '+env.dotnetSdk+' SDK','測試框架：'+env.testFramework,'前端測試：Node.js '+env.node].join('\n'),'F',valueWidth);
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
 const g=base(w,'核心查核邏輯','核心查核邏輯的測試結果',gw);
 const core=tests.reviewTests.map(r=>[r.title,r.total,r.passed,r.failed,r.classes.join('\n')]);
 const ge=table(g,4,['查核邏輯','測試案例數','通過','失敗','測試類別'],core,64);
 core.forEach((x,i)=>{
  g.getRange('A'+(5+i)+':E'+(5+i)).format.rowHeight=tall(lines(x[4],gw[4]),44);
 });
 numbers(g,['B','C','D'],4,ge,'center');
 ranges.push([g.name,'A1:E'+ge]);

 // The result of every test case of the six core audit logics. Cases are listed under their logic and, within it, under their group;
 // each logic heading carries the counts for that logic. A result recorded for another version of the case file is shown as not run.
 const cases=review.cases, caseSource=review.caseSource, record=tests.reviewCases;
 const ran=record&&record.definitionSha256===caseSource.sha256?record:{results:{}};
 const outcome=id=>({passed:'通過',failed:'失敗'})[ran.results[id]]||'未執行';
 const tally=list=>{
  const n=r=>list.filter(c=>outcome(c.id)===r).length;
  return ['共 '+list.length+' 個案例','通過 '+n('通過')+' 個']
   .concat(n('失敗')?['失敗 '+n('失敗')+' 個']:[],n('未執行')?['未執行 '+n('未執行')+' 個']:[]).join('，');
 };
 const kw=[160,490,100];
 const logics=[...new Set(cases.map(c=>c.logic))], groups=new Set(cases.map(c=>c.logic+'\n'+c.group));
 const k=base(w,'案例清單','核心查核邏輯各案例的結果',kw,12+cases.length+logics.length+groups.size);
 let krow=4;
 krow=field(k,krow,'自動化測試',caseSource.testFile,'C');
 krow=field(k,krow,'測試資料與預期結果',caseSource.file,'C');
 krow=field(k,krow,'執行日期',ran.date||'','C');
 krow=field(k,krow,'結果',tally(cases),'C');
 const head=krow+1;
 const kh=k.getRange('A'+head+':C'+head);kh.values=[['案例編號','案例名稱','結果']];kh.format.fill=light;kh.format.font.bold=true;kh.format.rowHeight=30;
 kh.format.horizontalAlignment='center';
 let at=head;
 cases.forEach((c,i)=>{
  const before=cases[i-1];
  if(!before||before.logic!==c.logic){
   at++;block(k,'A'+at+':C'+at,c.logic+'：'+tally(cases.filter(x=>x.logic===c.logic)),band);
   const r=k.getRange('A'+at+':C'+at);r.format.font={name:'Microsoft JhengHei',size:11,bold:true,color:navy};r.format.rowHeight=30;
   r.format.borders={top:{style:'thin',color:navy}};
  }
  if(!before||before.logic!==c.logic||before.group!==c.group){
   at++;block(k,'A'+at+':C'+at,c.group);
   const r=k.getRange('A'+at+':C'+at);r.format.font={name:'Microsoft JhengHei',size:11,bold:true,color:ink};r.format.rowHeight=26;
   r.format.borders={bottom:{style:'thin',color:'#8FA3B0'}};
  }
  at++;
  const r=k.getRange('A'+at+':C'+at);r.values=[[c.id,c.name,outcome(c.id)]];
  r.format.rowHeight=Math.max(24,lines(c.name,kw[1])*17+7);
  const next=cases[i+1];
  if(next&&next.logic===c.logic&&next.group===c.group)r.format.borders={bottom:{style:'thin',color:'#DBE2E7'}};
  k.getRange('A'+at).format.horizontalAlignment='center';k.getRange('C'+at).format.horizontalAlignment='center';
  if(outcome(c.id)==='失敗')k.getRange('C'+at).format.font={name:'Microsoft JhengHei',size:11,bold:true,color:'#B3261E'};
 });
 // The preview image shows the top of the sheet only; the whole list is too tall to draw as one picture.
 ranges.push([k.name,'A1:C'+Math.min(at,head+36)]);

 const cw=[340,130,130,130];
 const c=base(w,'程式碼涵蓋率','後端程式碼行涵蓋率',cw);
 const ce=table(c,4,['原始碼模組','已涵蓋行數','可執行行數','行涵蓋率'],tests.coverage.modules.map(m=>[m.module,m.covered,m.total,null]),30);
 tests.coverage.modules.forEach((m,i)=>{const n=5+i;c.getRange('D'+n).formulas=[['=B'+n+'/C'+n]];});
 const n=ce+1;
 c.getRange('A'+n+':C'+n).values=[['合計',tests.coverage.covered,tests.coverage.total]];c.getRange('D'+n).formulas=[['=B'+n+'/C'+n]];
 c.getRange('A'+n+':D'+n).format.font.bold=true;c.getRange('A'+n+':D'+n).format.rowHeight=30;
 numbers(c,['B','C','D'],4,n,'right');
 c.getRange('D5:D'+n).setNumberFormat('0.0%');
 ranges.push([c.name,'A1:D'+n]);
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
