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
const paths=value=>value.split('；').map(x=>'JET/'+x).join('\n');
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
function table(s,row,heads,rows,height=54){
 const end=col(heads.length-1);s.getRange('A'+row+':'+end+row).values=[heads];s.getRange('A'+row+':'+end+row).format.fill=light;s.getRange('A'+row+':'+end+row).format.font.bold=true;s.getRange('A'+row+':'+end+row).format.rowHeight=30;
 if(rows.length){const r=s.getRange('A'+(row+1)+':'+end+(row+rows.length));r.values=rows;r.format.borders={insideHorizontal:{style:'thin',color:'#DBE2E7'}};r.format.rowHeight=height;}
 return row+rows.length;
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
 // what the author submits, the checks the reviewer makes, the findings, and the recorded decision.
 const w=Workbook.create(),ranges=[];
 const s=base(w,'審查紀錄',review.title,[200,330,150,220],2);
 let row=4;
 row=field(s,row,'軟體與版本',product,'D');
 row=field(s,row,'變更說明',review.change,'D');
 row=field(s,row,'需求依據',review.requirement,'D');
 row=field(s,row,'單元測試報告','Unit test Report.xlsx','D');
 row=field(s,row,'程式版本（提交編號）',null,'D');
 row=field(s,row,'作者',null,'D');
 row=field(s,row,'提交審查日期',null,'D');
 const checks=table(s,row+1,['審查規則','檢查項目','結果','備註'],review.checklist.map(c=>[c.rule,c.item,null,null]));
 fill(s,'C'+(row+2)+':C'+checks,['符合','不符合','不適用']);fill(s,'D'+(row+2)+':D'+checks);
 const findings=table(s,checks+2,['編號','發現事項與程式位置','處理方式','複測結果'],[1,2,3,4].map(()=>[null,null,null,null]),40);
 fill(s,'A'+(checks+3)+':D'+findings);
 row=findings+2;
 block(s,'A'+row+':A'+row,'結論',light);block(s,'B'+row+':D'+row,'',yellow);
 s.getRange('B'+row).dataValidation={rule:{type:'list',values:['通過','需修正','例外核准']}};
 row=field(s,row+1,'審查者',null,'D');
 row=field(s,row,'審查日期',null,'D');
 row=field(s,row,'核准的程式版本',null,'D');
 row=field(s,row,'後續事項',null,'D',2);
 row=field(s,row,'核准的例外',null,'D',2);
 ranges.push(['審查紀錄','A1:D'+(row-1)]);

 const f=base(w,'審查範圍','主要功能與對應測試',[170,330,330,110,200]);
 const fe=table(f,4,['功能','程式位置','單元測試','審查結果','備註'],review.scope.map(x=>[x.title,paths(x.path),x.tests.split('、').join('\n'),null,null]),84);
 fill(f,'D5:D'+fe,['符合','不符合']);fill(f,'E5:E'+fe);
 ranges.push(['審查範圍','A1:E'+fe]);

 const h=base(w,'固定值與規則','程式內的固定值與規則',[50,150,400,360,110,100,190]);
 const he=table(h,4,['編號','項目','內容','程式位置','來源','審查結果','備註'],review.hardCoded.map(x=>[x.id,x.item,x.content,paths(x.path),x.origin,null,null]),44);
 fill(h,'F5:F'+he,['可接受','已修正','例外核准']);fill(h,'G5:G'+he);
 ranges.push(['固定值與規則','A1:G'+he]);
 await save(w,'Code Review/Code review',ranges);
}
{
 const w=Workbook.create(),ranges=[];
 const s=base(w,'測試結果',tests.title,[230,110,110,110,110,150],2);
 let row=4;
 row=field(s,row,'軟體與版本',product,'F');
 row=field(s,row,'程式版本（提交編號）',null,'F');
 row=field(s,row,'執行日期',tests.date,'F');
 row=field(s,row,'測試資料',tests.scope,'F');
 row=field(s,row,'執行命令',tests.command,'F');
 const names={'public-tests':'單元、整合與架構測試','frontend-tests':'前端測試'};
 const reported=tests.steps.filter(x=>names[x.name]);
 row=field(s,row,'結果',reported.every(x=>x.status==='passed')?'通過':'未通過','F');
 const suites=reported.map(x=>[names[x.name],x.total,x.passed,x.failed,x.skipped,x.status==='passed'?'通過':'未通過']);
 const end=table(s,row+1,['測試範圍','案例數','通過','失敗','略過','結果'],suites,30);
 ranges.push([s.name,'A1:F'+end]);

 const g=base(w,'功能測試','主要功能的測試結果',[190,90,90,90,420]);
 const ge=table(g,4,['功能','案例數','通過','失敗','測試類別'],tests.reviewTests.map(r=>[r.title,r.total,r.passed,r.failed,r.classes.join('、')]),64);
 ranges.push([g.name,'A1:E'+ge]);

 const c=base(w,'程式涵蓋率','C# 程式行涵蓋率',[220,130,130,130]);
 const ce=table(c,4,['模組','已執行行數','可量測行數','行涵蓋率'],tests.coverage.modules.map(m=>[m.module,m.covered,m.total,null]),30);
 tests.coverage.modules.forEach((m,i)=>{const n=5+i;c.getRange('D'+n).formulas=[['=B'+n+'/C'+n]];});
 const n=ce+1;
 c.getRange('A'+n+':C'+n).values=[['合計',tests.coverage.covered,tests.coverage.total]];c.getRange('D'+n).formulas=[['=B'+n+'/C'+n]];
 c.getRange('A'+n+':D'+n).format.font.bold=true;c.getRange('A'+n+':D'+n).format.rowHeight=30;
 c.getRange('D5:D'+n).setNumberFormat('0.0%');
 block(c,'A'+(n+2)+':D'+(n+3),tests.coverage.method);
 ranges.push([c.name,'A1:D'+(n+3)]);
 await save(w,'Code Review/Unit test Report',ranges);
}
{
 const w=Workbook.create();
 const s=w.worksheets.add('版本更新紀錄');
 const rows=releases.rows.map(r=>[r.date,r.description,r.version]);
 const all=s.getRange('A1:C'+(1+rows.length));all.format.font={name:'Microsoft JhengHei',size:11,color:ink};all.format.wrapText=true;all.format.verticalAlignment='top';
 [120,420,110].forEach((width,i)=>s.getRange(col(i)+'1:'+col(i)+(1+rows.length)).format.columnWidthPx=width);
 s.getRange('A1:C1').values=[['Date','Description','Version']];s.getRange('A1:C1').format.fill=light;s.getRange('A1:C1').format.font.bold=true;
 s.getRange('A2:C'+(1+rows.length)).values=rows;s.getRange('A2:C'+(1+rows.length)).format.rowHeight=84;
 await save(w,'版本更新紀錄',[['版本更新紀錄','A1:C'+(1+rows.length)]]);
}
