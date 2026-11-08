import fs from 'node:fs/promises';
import path from 'node:path';
import { Workbook, SpreadsheetFile } from '@oai/artifact-tool';
const repo=path.resolve(process.argv[2]||process.cwd());
const sat=path.join(repo,'docs/SAT'), source=path.join(sat,'source');
const preview=path.join(process.env.TEMP||repo,'jet-sat-20261106','sat-preview');
await fs.mkdir(preview,{recursive:true});
const read=async n=>JSON.parse(await fs.readFile(path.join(source,n),'utf8'));
const review=await read('code-review.json'), explanations=await read('code-explanation.json'), tests=await read('test-record.json'), dependencies=await read('dependencies.json');
const ink='#24323D', navy='#24475D', light='#E8EFF3', yellow='#FFF3CA';
const col=i=>String.fromCharCode(65+i);
function base(w,name,title,widths=[120,120,120,120,120,120]){
 const s=w.worksheets.add(name);s.showGridLines=false;
 const last=col(widths.length-1);
 const all=s.getRange('A1:'+last+'100');all.format.font={name:'Microsoft JhengHei',size:11,color:ink};all.format.rowHeight=24;all.format.wrapText=true;all.format.verticalAlignment='center';
 widths.forEach((width,i)=>s.getRange(col(i)+'1:'+col(i)+'100').format.columnWidthPx=width);
 s.getRange('A1:'+last+'2').merge();s.getRange('A1').values=[[title]];s.getRange('A1:'+last+'2').format.fill=navy;s.getRange('A1:'+last+'2').format.font={name:'Microsoft JhengHei',size:18,bold:true,color:'#FFFFFF'};
 s.freezePanes.freezeRows(4);s.tabColor=navy;return s;
}
function block(s,range,text,fill){
 s.getRange(range).merge();s.getRange(range.split(':')[0]).values=[[text]];
 if(fill)s.getRange(range).format.fill=fill;
 if(fill===yellow)s.getRange(range).format.borders={preset:'outside',style:'thin',color:'#C9BA8A'};
}
function table(s,row,heads,rows,widths){
 const end=col(heads.length-1);s.getRange('A'+row+':'+end+row).values=[heads];s.getRange('A'+row+':'+end+row).format.fill=light;s.getRange('A'+row+':'+end+row).format.font.bold=true;s.getRange('A'+row+':'+end+row).format.rowHeight=30;
 if(rows.length){const r=s.getRange('A'+(row+1)+':'+end+(row+rows.length));r.values=rows;r.format.borders={insideHorizontal:{style:'thin',color:'#DBE2E7'}};r.format.rowHeight=54;}
 if(widths)widths.forEach((v,i)=>s.getRange(col(i)+'1:'+col(i)+(row+rows.length)).format.columnWidthPx=v);
 return row+rows.length;
}
async function save(w,name,ranges){
 w.recalculate();
 const e=await w.inspect({kind:'match',searchTerm:'#REF!|#DIV/0!|#VALUE!|#NAME\\?|#NUM!',options:{useRegex:true,maxResults:20},maxChars:1000});
 console.log(name,e.ndjson);
 for(const [sheetName,range] of ranges){
 const img=await w.render({sheetName,range,scale:1.5,format:'png'});
 await fs.writeFile(path.join(preview,name+'-'+sheetName+'.png'),new Uint8Array(await img.arrayBuffer()));
 }
 const out=await SpreadsheetFile.exportXlsx(w);await out.save(path.join(sat,name+'.xlsx'));
 await fs.rename(path.join(sat,name+'.xlsx.inspect.ndjson'),path.join(preview,name+'.inspect.ndjson')).catch(()=>{});
 console.log('Saved',name,ranges.length,'sheets');
}
{
 const w=Workbook.create();const ranges=[];
 const s=base(w,'審查總表',review.title,[80,210,155,125,150]);
 block(s,'A3:E4',review.note);
 table(s,6,['編號','人工複核範圍','目前結論','複核人員','處理事項'],review.rows.map((r,i)=>[r.id,r.title,null,null,null]));

 block(s,'A14:E16','本表只記錄關鍵審計規則的人工複核。一般程式處理由 Agent 維護自動測試；ITS 與 NITSO 的安全評估另見安全檢查摘要。');
 ranges.push(['審查總表','A1:E16']);
 for(const r of review.rows){
  const p=base(w,r.id,r.id+'　'+r.title);
  block(p,'A3:F4',r.question,light);
  block(p,'A5:F7','驗算例子：'+r.example);
  block(p,'A8:F9','複核重點：'+r.focus);
  block(p,'A10:F11','程式位置：src/JET/JET/'+r.path);
  block(p,'A12:F13','對應測試：'+r.tests+'。結果見測試紀錄表，不能代填人工結論。');
  block(p,'A14:A15','規則依據');block(p,'B14:F15','',yellow);
  block(p,'A16:A18','獨立答案與實際結果');block(p,'B16:F18','',yellow);
  block(p,'A19:A21','發現問題與處理');block(p,'B19:F21','',yellow);
  p.getRange('A22').values=[['結論']];block(p,'B22:C22','',yellow);
  p.getRange('B22').dataValidation={rule:{type:'list',values:['通過','需修正','不適用','待複核']}};
  p.getRange('D22').values=[['複核人員']];block(p,'E22:F22','',yellow);
  block(p,'A23:A25','複測與結案');block(p,'B23:F25','',yellow);
  ranges.push([r.id,'A1:F25']);
 }
 review.rows.forEach((r,i)=>{
  s.getRange('C'+(7+i)).formulas=[['=IF(\''+r.id+'\'!B22="","待複核",\''+r.id+'\'!B22)']];
  s.getRange('D'+(7+i)).formulas=[['=IF(\''+r.id+'\'!E22="","",\''+r.id+'\'!E22)']];
 });
 await save(w,'Code Review',ranges);
}
{
 const w=Workbook.create(),ranges=[];
 for(let i=0;i<explanations.items.length;i++){
  const e=explanations.items[i],s=base(w,'說明'+(i+1),e.title);
  block(s,'A3:F4',e.explanation,light);block(s,'A5:F6',e.example);
  block(s,'A7:F8',e.path+'，第 '+e.start+' 行起。'+e.review);
  for(let j=0;j<e.code.length;j++){
   const n=j+10;
   block(s,'A'+n+':F'+n,(e.start+j)+'  '+e.code[j]);
   s.getRange('A'+n+':F'+n).format.font={name:'Consolas',size:10,color:ink};
   s.getRange('A'+n+':F'+n).format.rowHeight=e.code[j].length>105?34:22;
  }
  ranges.push([s.name,'A1:F'+(9+e.code.length)]);
 }
 await save(w,'Code and Explanation',ranges);
}
{
 const w=Workbook.create(),ranges=[];
 const s=base(w,'本次結果','JET 自動測試結果',[180,110,110,110,110,150]);
 block(s,'A3:F4',tests.scope+' 本次狀態：'+(tests.status==='passed'?'通過':'未通過')+'。');
 const end=table(s,6,['測試範圍','案例數','通過','失敗','略過','結果'],tests.steps.map(x=>[x.name==='public-tests'?'C# 單元、整合及架構測試':x.name==='public-frontend-preview'?'前端預覽環境測試':'前端行為測試',x.total,x.passed,x.failed,x.skipped,x.status==='passed'?'通過':'未通過']));
 block(s,'A'+(end+2)+':F'+(end+3),'C# 涵蓋率只表示執行到的程式行，不代表審計規則已經人工確認。全部測試均使用合成資料。');
 table(s,end+5,['複核編號','規則範圍','案例數','通過','失敗','用途'],tests.reviewTests.map(r=>[r.id,r.title,r.total,r.passed,r.failed,'人工複核參考']));
 block(s,'A'+(end+13)+':F'+(end+15),(tests.additionalChecks||[]).map(x=>x.name+'：'+(x.status==='passed'?'通過':'未通過')+'，'+x.workbooksPassed+'/'+x.workbooks+' 份工作簿、'+x.nativeImportsPassed+'/'+x.nativeImports+' 種匯入。').join(' ')+'\n本次識別與原始紀錄位置見 source/test-record.json。各規則測試可能重疊。\n'+tests.command);
 ranges.push([s.name,'A1:F'+(end+15)]);
 const c=base(w,'程式涵蓋率','C# 程式涵蓋率',[190,115,115,120,180]);
 block(c,'A3:E5',tests.coverage.method);
 table(c,7,['模組','已執行行數','可量測行數','行涵蓋率','後續檢查'],tests.coverage.modules.map(m=>[m.module,m.covered,m.total,null,['Bridge','桌面啟動'].includes(m.module)?'需搭配桌面操作驗證':'追蹤未覆蓋部分']));
 tests.coverage.modules.forEach((m,i)=>{const n=8+i;c.getRange('D'+n).formulas=[['=B'+n+'/C'+n]];c.getRange('D'+n).setNumberFormat('0.0%');});
 const n=8+tests.coverage.modules.length;
 c.getRange('A'+n+':C'+n).values=[['合計',tests.coverage.covered,tests.coverage.total]];c.getRange('D'+n).formulas=[['=B'+n+'/C'+n]];c.getRange('D'+n).setNumberFormat('0.0%');
 block(c,'A'+(n+2)+':E'+(n+4),'前端測試不計入本表。桌面視窗、WebView2 與公司操作環境仍需另外驗證。完整逐檔結果保存在 source/test-record.json。');
 ranges.push([c.name,'A1:E'+(n+4)]);
 const g=base(w,'未覆蓋重點','未覆蓋程式與交接',[330,100,100,110,200]);
 const low=tests.coverage.files.filter(x=>x.covered<x.total).sort((a,b)=>(b.total-b.covered)-(a.total-a.covered)).slice(0,20);
 block(g,'A3:E4','依尚未執行的行數排序，列出前 20 個檔案。這不是風險評等，也不是把其餘程式視為安全。');
 table(g,6,['程式','已執行','可量測','未執行','處理'],low.map(x=>[x.path,x.covered,x.total,x.total-x.covered,'依變更風險補測；桌面操作另驗證']));
 ranges.push([g.name,'A1:E26']);
 const h=base(w,'公司交接','公司測試與複核交接');
 block(h,'A3:F4','黃色欄位由公司接手人員填寫。開發測試通過，不會自動完成下列事項。');
 const tasks=['送審版本與封裝位置','Windows、.NET 與 WebView2 版本','在公司執行測試的命令與結果','測試答案與未覆蓋範圍的複核','AuditCore 人工複核紀錄','Excel 與一般使用者操作結果','問題、處理人與複測結論','最終交接人員及日期'];
 tasks.forEach((t,i)=>{const row=6+i*3;block(h,'A'+row+':B'+(row+2),t,light);block(h,'C'+row+':F'+(row+2),'',yellow);});
 ranges.push([h.name,'A1:F29']);
 await save(w,'測試紀錄表',ranges);
}
{
 const w=Workbook.create(),ranges=[];
 for(const [sheetName,rows] of [['產品元件',dependencies.filter(x=>x.application_relation||x.ecosystem!=='NuGet')],['僅供測試',dependencies.filter(x=>!x.application_relation&&x.ecosystem==='NuGet')]]){
 const s=base(w,sheetName,'第三方元件：'+sheetName,[210,100,145,190,260,310]);
 block(s,'A3:F4','授權依套件宣告與來源條款整理。商用不等於可省略授權聲明；Windows、Office 與 Runtime 的使用權由公司確認。');
 table(s,6,['元件','版本','授權','商業使用','散布義務','來源'],rows.map(x=>[x.package,x.version,x.license,x.commercial_use,x.redistribution,x.license_source]));
 s.getRange('A7:F'+(6+rows.length)).format.rowHeight=64;s.getRange('F7:F'+(6+rows.length)).format.font.size=10;
 s.tables.add('A6:F'+(6+rows.length),true,sheetName==='產品元件'?'ProductComponents':'TestComponents');
 ranges.push([s.name,'A1:F'+Math.min(13,6+rows.length)]);
 }
 await save(w,'第三方元件',ranges);
}
{
 const w=Workbook.create(),ranges=[];
 for(const {sheet:name,source:slug,note} of (await read('workflow.json')).diagrams){
 const s=base(w,name,name+'流程圖',[160,160,160,160,160,160]);
 block(s,'A3:F4',note);
 const image=await fs.readFile(path.join(source,'diagrams',slug+'.png'));
 const width=image.readUInt32BE(16),height=image.readUInt32BE(20),display=960;
 s.images.add({dataUrl:'data:image/png;base64,'+image.toString('base64'),anchor:{from:{row:5,col:0},extent:{widthPx:display,heightPx:display*height/width}}});
 ranges.push([name,'A1:F'+Math.ceil(7+display*height/width/32)]);
 }
 await save(w,'Workflow',ranges);
}
