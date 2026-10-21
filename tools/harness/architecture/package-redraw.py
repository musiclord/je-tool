"""Package a redraw of the frozen research snapshot, binding every drawn connection to evidence."""
from pathlib import Path
from urllib.parse import unquote, urlsplit
import argparse, hashlib, html, json, re, shutil

SCOPE = "frozen-snapshot-redraw-not-current-product"
SEQUENCE_KINDS = {None: "calls", "dashed": "viaInterface", "emphasis": "indirect", "return": "return"}
CONNECTION_LISTS = ("messages", "edges", "flows", "connections", "relationships")
NODE_LISTS = ("participants", "nodes", "types", "entities", "components")
KIND_LABELS = {
    "calls": "直接呼叫：編譯時目標是具體方法",
    "viaInterface": "介面呼叫：實作依組裝推論",
    "indirect": "間接觸發：action 字串、WebMessage、委派或延遲串流",
    "implements": "實作介面",
    "injects": "組裝根建立並注入",
    "data": "資料讀寫",
    "uses": "使用或環境前提",
    "return": "回傳",
    "fact": "依已核對事實整理",
}
TYPE_LABELS = {"sequence": "循序圖", "workflow": "業務流程", "dataflow": "資料流", "class": "類別圖",
               "erd": "資料表關聯", "architecture": "部署圖"}
LOCAL_PATH = re.compile(r"(?i)\b[a-z](?::|%3a)[\\/]{1,2}users[\\/]")


def digest(path): return hashlib.sha256(path.read_bytes()).hexdigest()
def read(path): return json.loads(path.read_text(encoding="utf-8-sig"))
def escape(value): return html.escape(str(value), quote=True)


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def copy(source, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, target)


def short(name): return str(name or "").replace("T:", "").split(".")[-1]
def member_name(identifier): return re.sub(r"``\d+$", "", re.sub(r"\(.*$", "", str(identifier)).split(".")[-1])


def type_of_member(identifier):
    value = re.sub(r"^[MT]:", "", str(identifier))
    value = re.sub(r"::.*$", "", value)
    value = re.sub(r"\(.*$", "", value)
    return value.rsplit(".", 1)[0]


def compact(edge):
    keep = ("kind", "source", "target", "targetDisplay", "targetType", "dispatch", "location")
    return {key: edge[key] for key in keep if key in edge}


def portable_likec4_html(data, base_directory, site_directory):
    """Rewrite exported local file links relative to the package root and inline the favicon; the model and layout stay unchanged."""
    import base64
    base = base_directory.resolve().as_posix().casefold().rstrip("/")
    document = data.decode("utf-8")
    count = 0

    def inline_icon(match):
        icon = (site_directory / match.group(1)).read_bytes()
        return match.group(0).replace("./" + match.group(1), "data:image/svg+xml;base64," + base64.b64encode(icon).decode("ascii"))

    document = re.sub(r'<link rel="icon" type="image/svg\+xml" href="\./(favicon-[\w-]+\.svg)">', inline_icon, document)
    if re.search(r'(?:href|src)="\./', document):
        raise ValueError("The LikeC4 export still references a sibling file.")

    def rebase(match):
        nonlocal count
        value = match.group(0)
        parsed = urlsplit(value)
        path = unquote(parsed.path).lstrip("/")
        if not path.casefold().startswith(base + "/"):
            return value
        relative = path[len(base) + 1:]
        if not relative or ".." in relative.split("/") or parsed.query:
            raise ValueError("Unexpected LikeC4 link; no path was printed.")
        count += 1
        return "../" + relative + ("#" + parsed.fragment if parsed.fragment else "")

    document = re.sub(r"""file:(?://)?[^"'<>\s\x60]*""", rebase, document, flags=re.I)
    decoded = unquote(document).replace("\\\\", "/")
    if base in decoded.casefold() or LOCAL_PATH.search(decoded):
        raise ValueError("A local path remains in the LikeC4 export; no path was printed.")
    return document.encode("utf-8"), count


class Roslyn:
    def __init__(self, edges):
        self.edges = edges

    def calls(self, source, target, member):
        return [e for e in self.edges if e.get("kind") == "invokes" and short(type_of_member(e["source"])) == source
                and short(e.get("targetType")) == target and member_name(e["target"]) == member]

    def realizes(self, sub, parent):
        return [e for e in self.edges if e.get("kind") in ("implements", "inherits")
                and short(e["source"]) == sub and short(e["target"]) == parent]

    def assembled(self, type_name):
        return [e for e in self.edges if e.get("kind") == "constructs" and e["source"].startswith("M:JET.AppCompositionRoot.")
                and short(e.get("targetType")) == type_name]

    def dependency(self, source_full, target_full):
        return [e for e in self.edges if e.get("kind") in ("invokes", "constructs")
                and e["source"].startswith("M:" + source_full + ".") and e.get("targetType") == target_full]

    def full_names(self):
        names = {}
        for edge in self.edges:
            for value in (edge.get("targetType"), type_of_member(edge["source"]) if edge["source"].startswith("M:") else None,
                          edge["source"][2:] if edge["source"].startswith("T:") else None,
                          edge["target"][2:] if edge.get("kind") in ("implements", "inherits") else None):
                if value and value.startswith("JET."):
                    names.setdefault(short(value), set()).add(value)
        return names


def require(edges, description, limit=3):
    if not edges:
        raise ValueError("No Roslyn evidence for " + description)
    return [compact(e) for e in edges[:limit]]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--research-root", type=Path, required=True)
    parser.add_argument("--redraw", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--redraw-date", required=True)
    args = parser.parse_args()
    lab, work, package_dir = (p.resolve() for p in (args.research_root, args.redraw, args.output))
    # docs/architecture keeps one package named after its redraw date; it replaces earlier packages, which stay in Git history.
    def is_package(directory):
        manifest = directory / "manifest.json"
        return manifest.is_file() and read(manifest).get("scope") in (SCOPE, "frozen-research-not-current-product")
    if package_dir.exists() and not is_package(package_dir):
        raise SystemExit("Output is not a diagram package; refusing to overwrite it.")
    previous = [d for d in package_dir.parent.iterdir() if d.is_dir() and is_package(d)] if package_dir.parent.exists() else []
    out = work / "package"
    if out.exists():
        shutil.rmtree(out)
    run = lab / "runs/2026-10-16-all-diagrams"
    snapshot, baseline = run / "snapshot", run / "evidence"
    spec = read(work / "evidence-spec.json")
    source_manifest = read(baseline / "source-manifest.json")
    hashes = {f["path"]: f["sha256"] for f in source_manifest["files"]}
    roslyn = Roslyn(read(lab / "evidence/roslyn/edges.json"))
    full_names = roslyn.full_names()

    def resolve_source(ref):
        path = ref["path"]
        if path not in hashes:
            raise ValueError("Source is not in the frozen source manifest: " + path)
        source = snapshot / path
        if digest(source) != hashes[path]:
            raise ValueError("Snapshot evidence changed: " + path)
        lines = source.read_text(encoding="utf-8-sig").splitlines()
        if not 1 <= ref["line"] <= ref["endLine"] <= len(lines):
            raise ValueError("Invalid source range: " + path)
        return dict(path=path, line=ref["line"], endLine=ref["endLine"], sha256=hashes[path])

    # Facts: the frozen facts stay verbatim; this redraw adds facts it needed for the new connections.
    frozen_facts = read(baseline / "verified-facts.json")
    facts = list(frozen_facts["facts"])
    known = {f["id"] for f in facts}
    for fact in spec["facts"]:
        if fact["id"] in known:
            raise ValueError("Duplicate fact: " + fact["id"])
        known.add(fact["id"])
        facts.append(dict(id=fact["id"], title=fact["title"], claim=fact["claim"], status="source-reviewed-static",
                          addedOn=args.redraw_date, sources=[resolve_source(s) for s in fact["sources"]]))
    for fact in facts:
        for ref in fact["sources"]:
            resolve_source(ref)

    # Diagrams drawn in this redraw.
    portal = spec["portal"]
    archify_ids = [i for group in portal for i in group.get("archify", [])]
    candidates = {i: read(work / "archify" / i / "candidate.json") for i in archify_ids}
    model = read(work / "likec4-model.json")
    c4_text = (work / "likec4/jet.c4").read_text(encoding="utf-8")
    likec4_relations = {r["metadata"]["evidence"]: r for r in model["relations"].values() if (r.get("metadata") or {}).get("evidence")}
    if len(likec4_relations) != len(model["relations"]):
        raise ValueError("Every LikeC4 relation needs an evidence identifier.")

    def drawing(diagram, connection):
        if diagram == "likec4":
            relation = likec4_relations[connection]
            title = lambda fqn: model["elements"][fqn]["title"]
            return dict(fromLabel=title(relation["source"]["model"]), toLabel=title(relation["target"]["model"]),
                        label=relation.get("title", ""), drawn=relation["kind"], ends=(relation["source"]["model"], relation["target"]["model"]))
        candidate = candidates[diagram]
        nodes = {n["id"]: n for key in NODE_LISTS for n in candidate.get(key, [])}
        for key in CONNECTION_LISTS:
            for item in candidate.get(key, []):
                if item.get("id") == connection:
                    label = lambda node: nodes[node]["label"] + (f"（{nodes[node]['sublabel']}）" if nodes[node].get("sublabel") else "")
                    drawn = SEQUENCE_KINDS[item.get("variant")] if candidate["diagram_type"] == "sequence" else "fact"
                    return dict(fromLabel=label(item["from"]), toLabel=label(item["to"]), label=item.get("label", ""), drawn=drawn,
                                ends=(item["from"], item["to"]))
        raise ValueError(f"Connection not drawn: {diagram}:{connection}")

    connections = []
    by_key = {}
    for item in spec["connections"]:
        diagram, connection, kind = item["diagram"], item["id"], item["kind"]
        drawn = drawing(diagram, connection)
        if drawn["drawn"] != kind:
            raise ValueError(f"Drawn kind {drawn['drawn']} differs from evidence kind {kind}: {diagram}:{connection}")
        entry = dict(diagram=diagram, connection=connection, fromLabel=drawn["fromLabel"], toLabel=drawn["toLabel"],
                     label=drawn["label"], kind=kind, execution="not-observed")
        if item.get("calls"):
            entry["members"] = sorted({m for _, _, m in item["calls"]})
            entry["roslyn"] = [e for s, t, m in item["calls"] for e in require(roslyn.calls(s, t, m), f"{s} calls {t}.{m}")]
        if item.get("implementations"):
            entry["implementations"] = [e for s, t in item["implementations"] for e in require(roslyn.realizes(s, t), f"{s} realizes {t}", 1)]
        if item.get("assembly"):
            entry["assembly"] = [e for t in item["assembly"] for e in require(roslyn.assembled(t), f"AppCompositionRoot builds {t}", 2)]
        if kind == "implements":
            entry["roslyn"] = entry.pop("implementations")
        if kind == "injects":
            entry["roslyn"] = entry.pop("assembly")
        if item.get("returnOf"):
            entry["returnOf"] = item["returnOf"]
        entry["facts"] = list(item.get("facts", []))
        for fact in entry["facts"]:
            if fact not in known:
                raise ValueError("Unknown fact " + fact)
        key = (diagram, connection)
        if key in by_key:
            raise ValueError(f"Duplicate connection {diagram}:{connection}")
        by_key[key] = (entry, drawn["ends"])
        connections.append(entry)
    for entry in connections:
        if entry["kind"] == "return":
            call = by_key.get((entry["diagram"], entry.get("returnOf")))
            ends = by_key[(entry["diagram"], entry["connection"])][1]
            if call is None or tuple(reversed(call[1])) != ends:
                raise ValueError(f"Return does not reverse its call: {entry['diagram']}:{entry['connection']}")
    for diagram, candidate in candidates.items():
        if candidate["diagram_type"] == "class":
            continue
        for key in CONNECTION_LISTS:
            for item in candidate.get(key, []):
                if item.get("id") and (diagram, item["id"]) not in by_key:
                    raise ValueError(f"Connection without evidence: {diagram}:{item['id']}")
    for identifier in likec4_relations:
        if ("likec4", identifier) not in by_key:
            raise ValueError("LikeC4 relation without evidence: " + identifier)

    # Class diagrams keep the frozen binding format, now covering every relationship of the four diagrams.
    catalog, bindings = [], []
    for diagram, candidate in candidates.items():
        if candidate["diagram_type"] != "class":
            continue
        types = {t["id"]: t["label"] for t in candidate["types"]}
        for relation in candidate["relationships"]:
            names = [full_names.get(types[relation[end]], set()) for end in ("from", "to")]
            if any(len(n) != 1 for n in names):
                raise ValueError(f"Type name is not unique: {diagram}:{relation['id']}")
            source, target = (next(iter(n)) for n in names)
            label = relation.get("label", "")
            members = spec["classMembers"].get(f"{diagram}:{relation['id']}")
            if members is None:
                members = (["#ctor"] if "建立" in label else []) + re.findall(r"[A-Za-z_][A-Za-z0-9_]*", label)
            if relation["kind"] == "dependency":
                edges = roslyn.dependency(source, target)
                if members:
                    for member in members:
                        if not any(member_name(e["target"]) == member for e in edges):
                            raise ValueError(f"No Roslyn edge for {member}: {diagram}:{relation['id']}")
                    edges = [e for e in edges if member_name(e["target"]) in members]
                proof = require(edges, f"{diagram}:{relation['id']}", 4)
            else:
                wanted = "implements" if relation["kind"] == "realization" else "inherits"
                proof = require([e for e in roslyn.realizes(short(source), short(target)) if e["kind"] == wanted],
                                f"{diagram}:{relation['id']}", 1)
            record = dict(diagram=diagram, relationship=relation["id"], source="T:" + source, target="T:" + target,
                          kind=relation["kind"], roslyn=proof)
            catalog.append(record)
            bindings.append(dict(record, execution="not-observed", members=members))

    # Package files.
    diagrams = []
    for diagram in archify_ids:
        candidate = candidates[diagram]
        reviews = sorted((work / "archify" / diagram).glob("review-*.log"), key=lambda p: int(p.stem.split("-")[1]))
        receipt = json.loads(reviews[-1].read_text(encoding="utf-8")) if reviews else {}
        if receipt.get("ok") is not True:
            raise ValueError("Archify finalize did not pass: " + diagram)
        entry = dict(id=diagram, title=candidate["meta"]["title"], type=candidate["diagram_type"],
                     candidate=f"archify/{diagram}/candidate.json", html=f"archify/{diagram}/{diagram}.html",
                     finalize=dict(review=reviews[-1].stem, quality=candidate["meta"].get("quality_profile"), ok=True))
        copy(work / "archify" / diagram / "candidate.json", out / entry["candidate"])
        copy(work / "archify" / diagram / f"{diagram}.html", out / entry["html"])
        entry["candidateSha256"], entry["htmlSha256"] = digest(out / entry["candidate"]), digest(out / entry["html"])
        diagrams.append(entry)
    likec4_source = (work / "likec4-site/index.html").read_bytes()
    likec4_html, rewritten = portable_likec4_html(likec4_source, work, work / "likec4-site")
    (out / "likec4").mkdir(parents=True, exist_ok=True)
    (out / "likec4/index.html").write_bytes(likec4_html)
    (out / "likec4/jet.c4").write_text(c4_text, encoding="utf-8")
    views = [dict(id=v, title=model["views"][v]["title"], html=f"likec4/index.html#/view/{v}/") for group in portal for v in group.get("likec4", [])]
    copy(lab / "tools/archify-3.1.0/archify/LICENSE", out / "licenses/archify-LICENSE.txt")
    copy(lab / "tools/node/node_modules/likec4/LICENSE", out / "licenses/likec4-LICENSE.txt")
    for name in ("class-signatures.json", "sqlite-schema.json", "source-manifest.json"):
        copy(baseline / name, out / "evidence" / name)
    copy(lab / "evidence/normalized-relations.json", out / "evidence/normalized-relations.json")
    save(out / "evidence/verified-facts.json", dict(identity=frozen_facts.get("identity"), facts=facts))
    save(out / "evidence/class-relations.json", catalog)
    save(out / "evidence/diagram-bindings.json", bindings)
    save(out / "evidence/connection-evidence.json", connections)

    signatures = read(baseline / "class-signatures.json")
    schema = read(baseline / "sqlite-schema.json")
    snippets = {}

    def record(ref):
        resolved = resolve_source(ref)
        key = f"{resolved['path']}:{resolved['line']}-{resolved['endLine']}"
        if key not in snippets:
            lines = (snapshot / resolved["path"]).read_text(encoding="utf-8-sig").splitlines()
            snippets[key] = dict(resolved, excerpt="\n".join(lines[resolved["line"] - 1:resolved["endLine"]]))
        return key

    def location_ref(edge):
        location = edge["location"]
        end = max(location["line"], location.get("endLine", location["line"]))
        return dict(path=location["path"], line=location["line"], endLine=min(end, location["line"] + 6))

    for fact in facts:
        for ref in fact["sources"]:
            record(ref)
    for signature in signatures:
        record(signature["source"])
    for table in schema["tables"]:
        for ref in table["sources"]:
            record(ref)
    for entry in connections:
        for group in ("roslyn", "implementations", "assembly"):
            for edge in entry.get(group, []):
                edge["excerpt"] = record(location_ref(edge))
    for binding in bindings:
        for edge in binding["roslyn"]:
            record(location_ref(edge))
    save(out / "evidence/connection-evidence.json", connections)
    save(out / "evidence/source-excerpts.json", snippets)

    write_pages(out, portal, diagrams, views, facts, connections, bindings, signatures, schema, snippets, args.redraw_date)
    for path in out.rglob("*"):
        if path.is_file() and path.suffix in (".html", ".json", ".md", ".c4") and LOCAL_PATH.search(unquote(path.read_text(encoding="utf-8", errors="ignore"))):
            raise ValueError("A local path remains in " + path.relative_to(out).as_posix())
    files = [dict(path=p.relative_to(out).as_posix(), sha256=digest(p)) for p in sorted(out.rglob("*")) if p.is_file()]
    save(out / "manifest.json", dict(
        schemaVersion=1, scope=SCOPE, snapshotDates=["2026-10-15", "2026-10-16"], redrawDate=args.redraw_date,
        versions=dict(repowise="0.56.0", roslyn="5.9.0.0", likec4="1.59.4", archify="3.1.0"),
        diagrams=diagrams, likec4Views=len(views), likec4Model="likec4/jet.c4",
        likec4Export=dict(html="likec4/index.html", sourceSha256=hashlib.sha256(likec4_source).hexdigest(),
                          portableSha256=digest(out / "likec4/index.html"), rewrittenLocalLinks=rewritten,
                          transformation="Rewrite local links relative to the package root and inline the favicon; keep the model and layout."),
        bindings="evidence/diagram-bindings.json", checkedClassRelations=len(bindings),
        connections="evidence/connection-evidence.json", checkedConnections=len(connections),
        facts="evidence/verified-facts.json", sourceExcerpts="evidence/source-excerpts.json", files=files,
        limitations=["No runtime observation.", "Connection evidence proves sources and drawn line kinds, not business correctness.",
                     "Source excerpts and declarations belong to the frozen snapshot."]))
    for directory in previous:
        shutil.rmtree(directory)
    shutil.move(str(out), str(package_dir))
    print(json.dumps(dict(diagrams=len(diagrams), likec4Views=len(views), connections=len(connections), classRelations=len(bindings),
                          facts=len(facts), sourceRanges=len(snippets), files=len(files) + 1, rewrittenLinks=rewritten), ensure_ascii=False))


CSS = """
:root{color-scheme:light;--bg:#f4f4ef;--paper:#fff;--ink:#21342e;--muted:#506660;--border:#cad7d0;--accent:#17634e;--soft:#e8efe9}
@media(prefers-color-scheme:dark){:root{color-scheme:dark;--bg:#16231e;--paper:#1f3029;--ink:#e1ece6;--muted:#adc4b7;--border:#40574a;--accent:#92d9b7;--soft:#263a31}}
*{box-sizing:border-box}html,body{margin:0;background:var(--bg);color:var(--ink);font:16px/1.7 "Segoe UI","Microsoft JhengHei",sans-serif}
a{color:var(--accent)}nav,main{max-width:1500px;margin:auto;padding:18px 24px}nav{display:flex;gap:20px;flex-wrap:wrap;border-bottom:1px solid var(--border)}
h1{font-size:30px;line-height:1.4;margin:.4em 0}h2{font-size:22px}h3{font-size:18px;margin:.2em 0}p{max-width:90ch}
.notice,article,section.diagram{padding:16px 18px;background:var(--paper);border:1px solid var(--border);margin:14px 0;border-radius:8px}
.muted,small{color:var(--muted)}.workspace{display:grid;grid-template-columns:290px minmax(0,1fr);gap:18px}
.intro{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1.4fr);gap:14px}.intro .notice{margin:0}.intro h2{margin:0 0 .3em;font-size:18px}
.menu{display:flex;flex-direction:column;gap:3px}.menu h3{margin:12px 0 2px;font-size:14px;color:var(--muted)}
.menu button{padding:6px 10px;line-height:1.45;text-align:left;background:var(--paper);color:var(--ink);border:1px solid var(--border);border-radius:5px;font:inherit;font-size:15px;cursor:pointer}
.menu button[aria-current=true]{border:2px solid var(--accent);background:var(--soft)}iframe{width:100%;height:max(640px,calc(100vh - 60px));border:1px solid var(--border);background:var(--paper);border-radius:6px}
.toolbar{display:flex;gap:16px;flex-wrap:wrap;align-items:baseline;padding:6px 0}input{width:100%;padding:8px;font:inherit;border:1px solid var(--border);border-radius:5px;background:var(--paper);color:var(--ink)}
.chips{display:flex;flex-wrap:wrap;gap:6px;margin:8px 0 0;padding:0;list-style:none}.chips li{padding:3px 10px;background:var(--soft);border-radius:999px;font-size:14px}
pre{overflow:auto;max-height:360px;background:var(--bg);padding:12px;font-size:13px;line-height:1.5}code,pre{font-family:Consolas,"Cascadia Mono",monospace}
code,summary,h2,h3,dd,li,small{overflow-wrap:anywhere}summary{cursor:pointer}
table{width:100%;min-width:640px;border-collapse:collapse;font-size:15px}th,td{padding:8px;text-align:left;vertical-align:top;border-bottom:1px solid var(--border);overflow-wrap:anywhere}
th{color:var(--muted);font-weight:600}.table-wrap{overflow-x:auto}.conn:target,article:target{outline:2px solid var(--accent)}section,article,.conn{scroll-margin-top:14px}
ul.refs{margin:0;padding-left:1.2em}.conn{padding:12px 0;border-top:1px solid var(--border)}.conn h3{font-size:16px;font-weight:600;line-height:1.6}
.conn h3 code{margin-right:8px;color:var(--muted)}.kind{display:inline-block;margin-left:8px;padding:0 9px;border-radius:999px;background:var(--soft);font-size:13px;font-weight:500}
.conn dl{display:grid;grid-template-columns:7.5em minmax(0,1fr);gap:4px 14px;margin:6px 0 0}.conn dt{color:var(--muted)}.conn dd{margin:0}
@media(max-width:820px){.workspace,.intro,.conn dl{grid-template-columns:minmax(0,1fr)}iframe{height:620px}nav,main{padding:14px 16px}h1{font-size:25px}.conn dt{margin-top:4px}}
"""
NAV = ('<nav><a href="index.html">圖表入口</a><a href="connections.html">連線證據</a><a href="evidence.html">已核對事實</a>'
       '<a href="types.html">類別簽章</a><a href="schema.html">資料表欄位</a><a href="README.md">閱讀說明</a></nav>')
WARNING = ("這批圖是 2026-10-21 依 2026-10-15 與 2026-10-16 的固定來源快照重繪，沒有更新到之後的產品修正；"
           "程式現況仍以 src 與測試為準。所有連線都是靜態證據，沒有執行期觀察。循序圖只畫標題寫明的情境與分支。")


def page(out, name, title, body, script=""):
    (out / name).write_text('<!doctype html><html lang="zh-Hant"><head><meta charset="utf-8"><meta name="viewport" '
                            'content="width=device-width,initial-scale=1"><title>' + escape(title) + '</title><style>' + CSS +
                            '</style></head><body>' + NAV + '<main>' + body + '</main><script>' + script + '</script></body></html>',
                            encoding="utf-8")


def write_pages(out, portal, diagrams, views, facts, connections, bindings, signatures, schema, snippets, redraw_date):
    by_id = {d["id"]: d for d in diagrams}
    view_by_id = {v["id"]: v for v in views}
    options, menu = [], ""
    for group in portal:
        menu += "<h3>" + escape(group["title"]) + "</h3>"
        for view in group.get("likec4", []):
            v = view_by_id[view]
            options.append(dict(id="likec4-" + view, title="LikeC4：" + v["title"], html=v["html"], source="likec4/jet.c4", evidence="connections.html#likec4"))
            menu += f'<button data-diagram="likec4-{escape(view)}">{escape(v["title"])}</button>'
        for diagram in group.get("archify", []):
            d = by_id[diagram]
            options.append(dict(id=diagram, title=TYPE_LABELS.get(d["type"], d["type"]) + "：" + d["title"], html=d["html"],
                                source=d["candidate"], evidence="connections.html#" + diagram))
            menu += f'<button data-diagram="{escape(diagram)}">{escape(d["title"])}</button>'
    legend = "".join(f"<li>{escape(KIND_LABELS[k])}</li>" for k in ("calls", "viaInterface", "indirect", "return"))
    body = ('<h1>JET 架構與流程圖</h1><p>6 個 LikeC4 視圖與 18 張 Archify 圖。每條線都在<a href="connections.html">連線證據</a>頁列出依據：'
            'Roslyn 找到的編譯時目標、組裝根建立的實作，或已核對的程式事實。</p>'
            '<div class="intro"><section class="notice"><h2>這批圖的範圍</h2>' + escape(WARNING) + '</section>'
            '<section class="notice"><h2>怎麼讀線條</h2>循序圖與 LikeC4 用線型區分證據層級。介面呼叫只確定介面方法；'
            '圖上寫的實作類別，是依 AppCompositionRoot 的建立位置推論。間接觸發的目標要看字串查表或委派，編譯器看不到。'
            '<ul class="chips">' + legend + '</ul></section></div>'
            '<div class="workspace"><aside><label for="search">搜尋圖表</label><input id="search" type="search"><div class="menu">' + menu +
            '</div></aside><section><div class="toolbar"><strong id="currentTitle"></strong><a id="standalone" target="_blank" rel="noopener">獨立開啟</a>'
            '<a id="model">可編輯來源</a><a id="evidence">這張圖的連線證據</a></div><iframe id="diagram" title="JET 架構與流程圖"></iframe></section></div>')
    script = ('const diagrams=' + json.dumps(options, ensure_ascii=False) + ';function show(id){const d=diagrams.find(x=>x.id===id)||diagrams[0];'
              'document.getElementById("currentTitle").textContent=d.title;document.getElementById("diagram").src=d.html;'
              'document.getElementById("standalone").href=d.html;document.getElementById("model").href=d.source;'
              'document.getElementById("evidence").href=d.evidence;document.querySelectorAll("[data-diagram]").forEach(b=>'
              'b.setAttribute("aria-current",String(b.dataset.diagram===d.id)));history.replaceState(null,"","#"+d.id)}'
              'document.querySelectorAll("[data-diagram]").forEach(b=>b.onclick=()=>show(b.dataset.diagram));'
              'document.getElementById("search").oninput=e=>{const q=e.target.value.toLowerCase();document.querySelectorAll("[data-diagram]")'
              '.forEach(b=>b.hidden=!b.textContent.toLowerCase().includes(q))};show(location.hash.slice(1)||"likec4-index");')
    page(out, "index.html", "JET 架構與流程圖", body, script)

    def excerpt(key, full_path=True):
        s = snippets[key]
        lines = ' 第 ' + str(s["line"]) + (('–' + str(s["endLine"])) if s["endLine"] != s["line"] else '') + ' 行'
        name = s["path"] if full_path else s["path"].split("/")[-1]
        return ('<details><summary>' + escape(name) + lines + '</summary><p><small>' + escape(s["path"]) + '．SHA-256：' + s["sha256"] +
                '</small></p><pre>' + escape(s["excerpt"]) + '</pre></details>')

    def edge_item(edge):
        if edge["kind"] in ("implements", "inherits"):
            text = short(edge["source"]) + (" 實作 " if edge["kind"] == "implements" else " 繼承 ") + short(edge["target"])
        elif edge["kind"] == "constructs":
            text = "AppCompositionRoot." + member_name(edge["source"]) + " 建立 " + short(edge["targetType"])
        else:
            dispatch = "具體方法" if edge.get("dispatch") == "statically_bound" else "介面方法"
            text = short(type_of_member(edge["source"])) + " 呼叫 " + short(edge["targetType"]) + "." + member_name(edge["target"]) + "（" + dispatch + "）"
        return "<li>" + escape(text) + (excerpt(edge["excerpt"], False) if edge.get("excerpt") else "") + "</li>"

    titles = {d["id"]: d["title"] for d in diagrams}
    titles["likec4"] = "LikeC4 模型的全部關係"
    order = ["likec4"] + [d["id"] for d in diagrams]
    body = ('<h1>連線證據</h1><p class="notice">' + escape(WARNING) + '</p><p>共 ' + str(len(connections)) + ' 條連線。'
            '類別圖的 ' + str(len(bindings)) + ' 條關係另列在<a href="types.html#bindings">類別簽章</a>頁。'
            '直接呼叫與介面呼叫列出 Roslyn 找到的呼叫位置；「實作與組裝」列出介面背後的類別如何被建立。'
            '業務流程、資料流、部署圖與資料表關聯的線，依已核對事實整理，證據不代表業務流程已執行。</p>')
    fact_titles = {f["id"]: f["title"] for f in facts}
    body += '<p>' + "．".join('<a href="#' + escape(d) + '">' + escape(titles[d]) + '</a>' for d in order if any(c["diagram"] == d for c in connections)) + '</p>'
    for diagram in order:
        rows = [c for c in connections if c["diagram"] == diagram]
        if not rows:
            continue
        body += '<section class="diagram" id="' + escape(diagram) + '"><h2>' + escape(titles[diagram]) + '</h2>'
        for c in rows:
            anchor = "c-" + diagram + "-" + c["connection"]
            details = ""
            if c.get("roslyn"):
                details += '<dt>編譯時目標</dt><dd><ul class="refs">' + "".join(edge_item(e) for e in c["roslyn"]) + '</ul></dd>'
            if c.get("implementations") or c.get("assembly"):
                details += '<dt>實作與組裝</dt><dd><ul class="refs">' + "".join(edge_item(e) for e in c.get("implementations", []) + c.get("assembly", [])) + '</ul></dd>'
            if c["kind"] == "return":
                details += ('<dt>對應呼叫</dt><dd><a href="#c-' + escape(diagram) + '-' + escape(c["returnOf"]) + '">' + escape(c["returnOf"]) + '</a> 的回傳</dd>')
            if c["facts"]:
                details += '<dt>事實</dt><dd>' + "、".join(f'<a href="evidence.html#f-{escape(f)}">{escape(fact_titles[f])}</a>' for f in c["facts"]) + '</dd>'
            body += ('<div class="conn" id="' + escape(anchor) + '"><h3><code>' + escape(c["connection"]) + '</code>' + escape(c["fromLabel"]) +
                     ' <small>到</small> ' + escape(c["toLabel"]) + '<span class="kind">' + escape(KIND_LABELS[c["kind"]]) + '</span></h3><dl>' +
                     ('<dt>圖上文字</dt><dd>' + escape(c["label"]) + '</dd>' if c["label"] else '') + details +
                     '<dt>執行</dt><dd>未觀察</dd></dl></div>')
        body += '</section>'
    page(out, "connections.html", "JET 圖表連線證據", body)

    used = {}
    for c in connections:
        for f in c["facts"]:
            used.setdefault(f, []).append(c)
    body = ('<h1>已核對事實</h1><p class="notice">' + escape(WARNING) + '</p><p>前 22 項沿用 2026-10-16 的研究，原文未改；'
            + escape(redraw_date) + ' 重繪時另外補上新連線需要的事實。每項只列實際讀過的程式或文件範圍。</p>'
            '<section class="notice" id="call-comparison"><h2>RepoWise 與 Roslyn 的代表呼叫對照</h2><p>2026-10-16 的研究人工選定 24 處呼叫，'
            'RepoWise 相符 16 處、漏列 8 處，Roslyn 全部相符。這是選定樣本，不能換算為全專案正確率。</p>'
            '<a href="evidence/normalized-relations.json">完整對照資料</a></section>')
    status_text = {"proposal-not-executed": "建議程序，未執行", "test-source-read-not-executed": "只讀測試程式，未執行",
                   "source-and-user-requirement": "來源與使用者要求，未觀察執行"}
    for f in facts:
        refs = "".join('<li><a href="connections.html#c-' + escape(c["diagram"]) + '-' + escape(c["connection"]) + '">' +
                       escape(titles.get(c["diagram"], c["diagram"])) + ' ' + escape(c["connection"]) + '</a></li>' for c in used.get(f["id"], []))
        body += ('<article id="f-' + escape(f["id"]) + '"><h2>' + escape(f["title"]) + '</h2><p class="muted">' + escape(f["id"]) + '．' +
                 escape(status_text.get(f["status"], "已核對來源，未觀察執行期行為")) + ('．' + escape(f["addedOn"]) + ' 補充' if f.get("addedOn") else '') +
                 '</p><p>' + escape(f["claim"]) + '</p>' +
                 "".join(excerpt(f"{s['path']}:{s['line']}-{s['endLine']}") for s in f["sources"]) +
                 ('<details><summary>用到這項事實的連線（' + str(len(used.get(f["id"], []))) + '）</summary><ul class="refs">' + refs + '</ul></details>' if refs else '') +
                 '</article>')
    page(out, "evidence.html", "JET 已核對事實", body)

    body = ('<h1>類別與方法簽章</h1><p>精選 20 個方法沿用 2026-10-16 的研究。介面呼叫只確認編譯時合約；執行時實作要看組裝。</p>'
            '<section class="diagram" id="bindings"><h2>四張類別圖的關係證據</h2><p>' + str(len(bindings)) + ' 條關係都以 Roslyn 的呼叫、建立、'
            '實作或繼承紀錄核對端點與種類；依賴線上寫的方法也要出現在證據裡。</p>')
    kind_text = {"dependency": "依賴", "realization": "實作", "inheritance": "繼承"}
    for diagram in dict.fromkeys(b["diagram"] for b in bindings):
        body += ('<h3 id="bindings-' + escape(diagram) + '">' + escape(titles.get(diagram, diagram)) + '</h3><div class="table-wrap"><table>'
                 '<tr><th>關係</th><th>起點</th><th>終點</th><th>種類</th><th>方法</th><th>Roslyn 位置</th></tr>')
        for b in (b for b in bindings if b["diagram"] == diagram):
            places = "".join("<li>" + escape(e["location"]["path"].split("/")[-1]) + " 第 " + str(e["location"]["line"]) + " 行</li>" for e in b["roslyn"])
            body += ('<tr><td>' + escape(b["relationship"]) + '</td><td>' + escape(short(b["source"])) + '</td><td>' + escape(short(b["target"])) +
                     '</td><td style="white-space:nowrap">' + kind_text[b["kind"]] + '</td><td>' + escape("、".join(m.replace("#ctor", "建構子") for m in b["members"])) +
                     '</td><td><ul class="refs">' + places + '</ul></td></tr>')
        body += '</table></div>'
    body += '</section>'
    for s in signatures:
        sig = s["signature"]
        body += ('<article><h2>' + escape(s["type"]) + '</h2><code>' + escape(sig["returns"] + " " + sig["name"] + "(" + sig["parameters"] + ")") + '</code>' +
                 excerpt(f"{s['source']['path']}:{s['source']['line']}-{s['source']['endLine']}") + '</article>')
    page(out, "types.html", "JET 類別與方法簽章", body)

    body = ('<h1>SQLite 實體欄位</h1><p>27 張表、164 個欄位，沿用 2026-10-16 依快照建表與遷移定義整理的結果。這批定義未宣告外鍵；'
            '資料表關聯圖的線是程式在 SQL 裡使用的連接欄位，不代表資料庫約束。其他資料庫尚未逐項核對。</p>')
    for t in schema["tables"]:
        body += ('<article id="' + escape(t["name"]) + '"><h2>' + escape(t["name"]) + '</h2><div class="table-wrap"><table><tr><th>欄位</th><th>型別</th><th>鍵</th><th>宣告</th></tr>' +
                 "".join('<tr><td>' + escape(a["name"]) + '</td><td>' + escape(a["type"]) + '</td><td>' + escape(a.get("key", "")) + '</td><td>' +
                         escape(a.get("declaration", "")) + '</td></tr>' for a in t["attributes"]) + '</table></div>' +
                 "".join(excerpt(f"{s['path']}:{s['line']}-{s['endLine']}") for s in t["sources"]) + '</article>')
    page(out, "schema.html", "JET SQLite 欄位", body)

    counts = {kind: sum(1 for c in connections if c["kind"] == kind) for kind in KIND_LABELS}
    (out / "README.md").write_text(
        f"# JET 架構圖表：{redraw_date} 重繪\n\n"
        "開啟[圖表入口](index.html)。這一版依 2026-10-15 與 2026-10-16 的固定來源快照，重繪 6 個 LikeC4 視圖與 18 張 Archify 圖。"
        "沒有重新索引程式，也沒有更新到之後的產品修正；程式現況仍以 src 與測試為準。`docs/architecture/` 只保留這一個圖表目錄，以重繪日期命名；下次重繪會取代它，前一版留在 Git 歷史。\n\n"
        "## 怎麼讀\n\n"
        "- 循序圖與 LikeC4 用線型區分證據。灰色實線是直接呼叫，編譯時目標是具體方法；藍色或紫色虛線是介面呼叫，只確定介面方法。"
        "間接觸發在 Archify 用綠色線、在 LikeC4 用琥珀色虛線，例如 action 字串、WebMessage、委派或延遲串流。每張圖的圖例會寫明。\n"
        "- 介面背後畫出的實作類別，是依 AppCompositionRoot 的建立位置推論，不是觀察到的執行結果。\n"
        "- 循序圖的標題與分段寫出情境、前置條件與所選分支；沒畫的分支寫在圖下方的卡片。\n"
        "- 類別圖只畫編譯時存在的依賴、實作與繼承。組裝時才決定的對象，用 AppCompositionRoot 的「建立」線與卡片說明。\n"
        "- 資料表關聯圖的線是程式在 SQL 裡使用的連接欄位；這批 SQLite 建表定義沒有宣告外鍵。\n\n"
        "## 證據與檢查\n\n"
        f"[連線證據](connections.html)列出 {len(connections)} 條連線的依據：直接呼叫 {counts['calls']} 條、介面呼叫 {counts['viaInterface']} 條、"
        f"間接觸發 {counts['indirect']} 條、回傳 {counts['return']} 條，其餘是實作、建立、資料讀寫、環境前提，"
        f"以及依[已核對事實](evidence.html)整理的業務流程、資料流、部署與資料表連線。四張類別圖的 {len(bindings)} 條關係列在[類別簽章](types.html#bindings)頁。\n\n"
        "`tools/verify.ps1 -Command Documentation` 會核對這一版的檔案雜湊、來源摘錄、事實引用，以及圖上線型與證據種類是否一致。"
        "這些檢查不證明業務結果正確，也沒有執行 JET；所有連線都標成未觀察執行。\n\n"
        "## 可編輯來源\n\n"
        "LikeC4 模型在 [jet.c4](likec4/jet.c4)，每條關係以 `evidence` 標記連到連線證據。Archify 的候選 JSON 在各圖的資料夾，"
        "例如[篩選情境儲存的循序圖](archify/action-sequence/candidate.json)。離線 HTML 內的本機連結已改成包內相對連結。\n\n"
        "## 第三方授權\n\n"
        "Archify 3.1.0 與 LikeC4 1.59.4 的原始授權保留在 [Archify 授權](licenses/archify-LICENSE.txt)和 [LikeC4 授權](licenses/likec4-LICENSE.txt)。\n",
        encoding="utf-8")


if __name__ == "__main__":
    main()
