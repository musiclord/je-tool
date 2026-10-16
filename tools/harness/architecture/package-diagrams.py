"""Package the reviewed, frozen research artifacts without rerendering their diagrams."""
from pathlib import Path
import argparse, hashlib, html, json, re, shutil

def digest(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def read(p): return json.loads(p.read_text(encoding="utf-8-sig"))
def save(p, value):
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(json.dumps(value, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
def copy(a,b):
    b.parent.mkdir(parents=True, exist_ok=True); shutil.copyfile(a,b)
def escape(x): return html.escape(str(x), quote=True)


def portable_likec4_html(data, source_directory):
    """Rebase exported source links without changing the model or rendered diagram."""
    from urllib.parse import unquote, urlsplit
    source = source_directory.resolve().as_posix().casefold().rstrip('/')
    document = data.decode('utf-8')
    count = 0
    def rebase(match):
        nonlocal count
        value = match.group(0)
        parsed = urlsplit(value)
        path = unquote(parsed.path).lstrip('/')
        if not path.casefold().startswith(source + '/'):
            return value
        relative = path[len(source)+1:]
        if not relative or '..' in relative.split('/') or parsed.query:
            raise ValueError('Unexpected LikeC4 source link; no path was printed.')
        count += 1
        return '../' + relative + ('#' + parsed.fragment if parsed.fragment else '')
    document = re.sub(r"""file:(?://)?[^"'<>\s\x60]*""", rebase, document, flags=re.I)
    decoded = unquote(document).replace('\\\\', '/')
    if source in decoded.casefold():
        raise ValueError('A local LikeC4 source path remains; no path was printed.')
    return document.encode('utf-8'), count


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--research-root",type=Path,required=True)
    parser.add_argument("--output",type=Path,required=True)
    args=parser.parse_args()
    lab=args.research_root.resolve(); out=args.output.resolve()
    if out.exists(): raise SystemExit("Output already exists; preserve the previous package.")
    run=lab/"runs/2026-10-16-all-diagrams"
    snapshot=run/"snapshot"
    index=read(run/"evidence/diagram-index.json")
    old=[
        ("filter-types","篩選儲存的類別與介面","class"),
        ("filter-save","篩選情境儲存的業務分支","workflow"),
        ("tb-import","TB 重新匯入與下游資料","workflow"),
        ("workpaper","正式底稿產生與發布","workflow")]
    diagrams=[]
    for identifier,title,kind in old:
        diagrams.append(dict(id=identifier,title=title,type=kind,round=1,original=f"output/archify/{identifier}"))
    for d in index:
        diagrams.append(dict(id=d["id"],title=d["title"],type=d["type"],round=2,original=f"runs/2026-10-16-all-diagrams/output/{d['id']}"))
    for d in diagrams:
        original=lab/d["original"]; destination=out/"archify"/d["id"]
        d["candidate"]=f"archify/{d['id']}/candidate.json"
        d["html"]=f"archify/{d['id']}/{d['id']}.html"
        copy(original/"candidate.json",out/d["candidate"])
        copy(original/(d["id"]+".html"),out/d["html"])
        d["candidateSha256"]=digest(out/d["candidate"]);d["htmlSha256"]=digest(out/d["html"])
    likec4_source = (lab/"output/likec4/index.html").read_bytes()
    likec4_html, rewritten_links = portable_likec4_html(likec4_source, lab/"output/likec4")
    (out/"likec4").mkdir(parents=True, exist_ok=True)
    (out/"likec4/index.html").write_bytes(likec4_html)
    likec4_export = dict(html="likec4/index.html", sourceSha256=hashlib.sha256(likec4_source).hexdigest(),
                        portableSha256=digest(out/"likec4/index.html"), rewrittenLocalLinks=rewritten_links,
                        transformation="Rebase local source links to package-relative links; preserve the model and diagram.")
    copy(lab/"output/likec4/source/jet.c4",out/"likec4/jet.c4")
    copy(lab/"tools/archify-3.1.0/archify/LICENSE",out/"licenses/archify-LICENSE.txt")
    copy(lab/"tools/node/node_modules/likec4/LICENSE",out/"licenses/likec4-LICENSE.txt")
    for name in ["verified-facts.json","class-relations.json","class-signatures.json","sqlite-schema.json"]:
        copy(run/"evidence"/name,out/"evidence"/name)
    copy(lab/"evidence/normalized-relations.json",out/"evidence/normalized-relations.json")
    copy(run/"evidence/source-manifest.json",out/"evidence/source-manifest.json")
    facts=read(out/"evidence/verified-facts.json")["facts"]
    signatures=read(out/"evidence/class-signatures.json")
    schema=read(out/"evidence/sqlite-schema.json")
    snippets={}
    def record(ref):
        key=f"{ref['path']}:{ref['line']}-{ref['endLine']}"
        if key in snippets: return
        source=snapshot/ref["path"]
        if digest(source)!=ref["sha256"]: raise ValueError("Snapshot evidence changed: "+ref["path"])
        lines=source.read_text(encoding="utf-8-sig").splitlines()
        if not 1<=ref["line"]<=ref["endLine"]<=len(lines): raise ValueError("Invalid source range")
        snippets[key]=dict(ref, excerpt="\n".join(lines[ref["line"]-1:ref["endLine"]]))
    for fact in facts:
        for ref in fact["sources"]: record(ref)
    for s in signatures: record(s["source"])
    for t in schema["tables"]:
        for ref in t["sources"]: record(ref)
    save(out/"evidence/source-excerpts.json",snippets)
    bindings=[]
    for d in diagrams:
        relations=[r for r in read(out/"evidence/class-relations.json") if r["diagram"]==d["id"]]
        if not relations: continue
        candidate=read(out/d["candidate"])
        if len(relations)!=len(candidate["relationships"]): raise ValueError("Relation count mismatch")
        for relation,proof in zip(candidate["relationships"],relations):
            bindings.append(dict(diagram=d["id"],relationship=relation["id"],source=proof["source"],target=proof["target"],kind=proof["kind"],execution="not-observed",roslyn=proof["roslyn"]))
    save(out/"evidence/diagram-bindings.json",bindings)
    css="""
:root{color-scheme:light dark;--bg:#f5f5f0;--paper:#fff;--ink:#223630;--muted:#52665f;--border:#cbd8d1;--accent:#17634e}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:16px/1.7 "Segoe UI","Microsoft JhengHei",sans-serif}a{color:var(--accent)}nav,main{max-width:1450px;margin:auto;padding:20px 24px}nav{display:flex;gap:22px;flex-wrap:wrap;border-bottom:1px solid var(--border)}h1{font-size:32px;line-height:1.4}h2{font-size:23px}p{max-width:88ch}.notice,article{padding:18px;background:var(--paper);border:1px solid var(--border);margin:16px 0;border-radius:8px}.muted,small{color:var(--muted)}.workspace{display:grid;grid-template-columns:280px minmax(0,1fr);gap:18px}.menu{display:flex;flex-direction:column;gap:6px}.menu button{padding:9px 12px;text-align:left;background:var(--paper);color:var(--ink);border:1px solid var(--border);border-radius:5px;font:inherit;cursor:pointer}.menu button[aria-current=true]{border:2px solid var(--accent)}iframe{width:100%;height:840px;border:1px solid var(--border);background:var(--paper)}.toolbar{display:flex;gap:15px;flex-wrap:wrap;padding:12px 0}input{width:100%;padding:10px;font:inherit}pre{overflow:auto;max-height:430px;background:var(--bg);padding:14px;font-size:13px}code{overflow-wrap:anywhere}summary{cursor:pointer;overflow-wrap:anywhere}table{width:100%;border-collapse:collapse}th,td{padding:9px;text-align:left;border-bottom:1px solid var(--border);overflow-wrap:anywhere}section,article{scroll-margin-top:15px}@media(prefers-color-scheme:dark){:root{--bg:#172520;--paper:#203129;--ink:#e0ebe5;--muted:#aec5b8;--border:#41584b;--accent:#92d9b7}}@media(max-width:800px){.workspace{grid-template-columns:1fr}iframe{height:620px}nav,main{padding:15px}h1{font-size:26px}}
"""
    nav='<nav><a href="index.html">圖表總覽</a><a href="evidence.html">來源證據</a><a href="types.html">類別簽章</a><a href="schema.html">資料表欄位</a><a href="../README.md">閱讀說明</a></nav>'
    def page(name,title,body,js=""):
        (out/name).write_text('<!doctype html><html lang="zh-Hant"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>'+escape(title)+'</title><style>'+css+'</style>'+nav+'<main>'+body+'</main><script>'+js+'</script></html>',encoding="utf-8")
    warning="這批圖依據 2026-10-15 與 2026-10-16 的未提交來源快照，保留當時的分析結果。它們沒有更新到本次產品修正；程式現況仍以 src 與測試為準。循序圖描述選定情境，不表示每次操作都走相同路徑。"
    options=[dict(id="likec4",title="LikeC4：六個架構視圖",html="likec4/index.html",candidate="likec4/jet.c4")]+diagrams
    buttons=''.join('<button data-diagram="'+d["id"]+'">'+escape(d["title"])+'</button>' for d in options)
    body='<h1>JET 架構與流程圖</h1><p>6 個 LikeC4 視圖與 18 張 Archify 圖。原圖與模型保留，這個入口只整理閱讀方式。</p><p class="notice">'+warning+'</p><div class="workspace"><aside><label for="search">搜尋圖表</label><input id="search" type="search"><div class="menu">'+buttons+'</div></aside><section><div class="toolbar"><strong id="currentTitle"></strong><a id="standalone" target="_blank" rel="noopener">獨立開啟</a><a id="model">可編輯來源</a></div><iframe id="diagram" title="JET 架構與流程圖"></iframe></section></div>'
    js='const diagrams='+json.dumps(options,ensure_ascii=False)+';function show(id){const d=diagrams.find(x=>x.id===id)||diagrams[0];document.getElementById("currentTitle").textContent=d.title;document.getElementById("diagram").src=d.html;document.getElementById("standalone").href=d.html;document.getElementById("model").href=d.candidate;document.querySelectorAll("[data-diagram]").forEach(b=>b.setAttribute("aria-current",String(b.dataset.diagram===d.id)));history.replaceState(null,"","#"+d.id)}document.querySelectorAll("[data-diagram]").forEach(b=>b.onclick=()=>show(b.dataset.diagram));document.getElementById("search").oninput=e=>document.querySelectorAll("[data-diagram]").forEach(b=>b.hidden=!b.textContent.toLowerCase().includes(e.target.value.toLowerCase()));show(location.hash.slice(1)||"action-sequence");'
    page("index.html","JET 架構與流程圖",body,js)
    def source(ref):
        s=snippets[f"{ref['path']}:{ref['line']}-{ref['endLine']}"]
        return '<details><summary>'+escape(s["path"])+':'+str(s["line"])+'–'+str(s["endLine"])+'</summary><p><small>SHA-256：'+s["sha256"]+'</small></p><pre>'+escape(s["excerpt"])+'</pre></details>'
    body='<h1>來源證據</h1><p class="notice">'+warning+'</p><section id="call-comparison"><h2>RepoWise 與 Roslyn 的代表呼叫對照</h2><p>人工選定的 24 處呼叫中，RepoWise 相符 16 處、漏列 8 處，Roslyn 全部相符。這是選定樣本，不能換算為全專案正確率。沒有執行業務流程。</p><a href="evidence/normalized-relations.json">完整對照資料</a></section>'
    for f in facts:
        body+='<article id="'+escape(f["id"])+'"><h2>'+escape(f["title"])+'</h2><p class="muted">'+('建議程序，未執行' if f["status"]=="proposal-not-executed" else '已核對來源，未觀察執行期行為')+'</p><p>'+escape(f["claim"])+'</p>'+''.join(source(s) for s in f["sources"])+'</article>'
    page("evidence.html","JET 圖表來源證據",body)
    body='<h1>類別與方法簽章</h1><p>精選 20 個方法。介面呼叫只確認編譯時合約；執行時實作與流程條件須另外核對。</p><a href="evidence/diagram-bindings.json">17 組類別關係的編譯證據</a>'
    for s in signatures:
        sig=s["signature"]
        body+='<article><h2>'+escape(s["type"])+'</h2><code>'+escape(sig["returns"]+" "+sig["name"]+"("+sig["parameters"]+")")+'</code>'+source(s["source"])+'</article>'
    page("types.html","JET 類別與方法簽章",body)
    body='<h1>SQLite 實體欄位</h1><p>27 張表、164 個欄位，依快照中的建表與遷移定義整理。這批定義未宣告外鍵；ERD 的邏輯關聯不代表資料庫約束。其他資料庫尚未逐項核對。</p>'
    for t in schema["tables"]:
        body+='<article id="'+escape(t["name"])+'"><h2>'+escape(t["name"])+'</h2><table><tr><th>欄位</th><th>型別</th><th>鍵</th><th>宣告</th></tr>'+''.join('<tr><td>'+escape(a["name"])+'</td><td>'+escape(a["type"])+'</td><td>'+escape(a.get("key",""))+'</td><td>'+escape(a.get("declaration",""))+'</td></tr>' for a in t["attributes"])+'</table>'+''.join(source(s) for s in t["sources"])+'</article>'
    page("schema.html","JET SQLite 欄位",body)
    (out/"README.md").write_text("# 固定快照圖表\n\n開啟 [圖表入口](index.html)。原圖與候選 JSON 未重繪，LikeC4 模型也保持原樣。離線 HTML 中的本機來源連結已改為相對連結，避免帶入私人絕對路徑。\n\n"+warning+"\n\n本輪只保存閱讀成果、選定關係與必要來源摘錄。沒有收入工具安裝、套件快取、私人案件、索引資料庫或整份程式快照。\n\n## 驗證的邊界\n\n原研究已完成圖表工具與瀏覽器檢查。納入儲存庫後另核對檔案雜湊、來源摘錄、本機連結及 17 組類別關係的種類。\n這不是全部圖表的業務正確性證明；LikeC4、循序圖與業務說明仍須人工查證，沒有執行期觀察。\n\n## 第三方授權\n\nArchify 3.1.0 與 LikeC4 1.59.4 的原始授權分別保留在 [Archify 授權](licenses/archify-LICENSE.txt)和 [LikeC4 授權](licenses/likec4-LICENSE.txt)。\n",encoding="utf-8")
    files=[dict(path=p.relative_to(out).as_posix(),sha256=digest(p)) for p in sorted(out.rglob("*")) if p.is_file()]
    save(out/"manifest.json",dict(schemaVersion=1,scope="frozen-research-not-current-product",snapshotDates=["2026-10-15","2026-10-16"],versions=dict(repowise="0.56.0",roslyn="5.9.0.0",likec4="1.59.4",archify="3.1.0"),diagrams=diagrams,likec4Views=6,likec4Export=likec4_export,bindings="evidence/diagram-bindings.json",checkedClassRelations=17,sourceExcerpts="evidence/source-excerpts.json",files=files,limitations=["No runtime observation.","No complete cross-diagram semantic verifier.","Source excerpts and declarations belong to the frozen snapshot."]))
    print(json.dumps(dict(diagrams=len(diagrams),files=len(files)+1,bytes=sum(p.stat().st_size for p in out.rglob("*") if p.is_file()),sourceRanges=len(snippets)),ensure_ascii=False))
if __name__=="__main__":main()
