"""Package a redraw drawn against one research snapshot, binding every drawn connection to evidence."""
from pathlib import Path
from urllib.parse import unquote, urlsplit
import argparse, hashlib, html, json, re, shutil

SCOPE = "frozen-snapshot-redraw-not-current-product"
SEQUENCE_KINDS = {None: "calls", "dashed": "viaInterface", "emphasis": "indirect", "return": "return"}
CONNECTION_LISTS = ("messages", "edges", "flows", "connections", "relationships")
NODE_LISTS = ("participants", "nodes", "types", "entities", "components")
KIND_LABELS = {
    "calls": "直接呼叫",
    "viaInterface": "介面呼叫",
    "indirect": "間接觸發",
    "implements": "實作介面",
    "injects": "啟動時建立",
    "data": "資料讀寫",
    "uses": "使用或環境前提",
    "return": "回傳",
    "fact": "已核對事實",
}
# Shown once on the connections page and as hover text; the labels above stay short.
KIND_NOTES = {
    "calls": "程式碼直接呼叫這個類別的方法。",
    "viaInterface": "程式碼只呼叫介面；實際執行哪個類別，看 AppCompositionRoot 在啟動時建立哪一個。",
    "indirect": "不是程式碼裡的直接呼叫，例如畫面用 action 名稱送出的訊息、事先交出的函式，或逐列讀取的資料。",
    "injects": "AppCompositionRoot 在啟動時建立物件，再交給使用它的類別。",
    "return": "對應同一張圖裡方向相反的呼叫。",
}
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


def with_theme(document, css):
    """Insert the JET colour overrides right after <head>; only colour variables, a heading font and decorations change, never geometry.
    The rules use more specific selectors, so they win over the tool's own stylesheets that follow them."""
    head = re.search(r"<head(?:\s[^>]*)?>", document)
    if not head or 'id="jet-theme"' in document:
        raise ValueError("Unexpected document head; the theme was not inserted.")
    return document[:head.end()] + '\n<style id="jet-theme">\n' + css + "</style>" + document[head.end():]


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
    parser.add_argument("--run", required=True, help="snapshot run under <research-root>/runs, e.g. 2026-10-22-head-07b97b3")
    parser.add_argument("--redraw", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--redraw-date", required=True)
    args = parser.parse_args()
    lab, work, package_dir = (p.resolve() for p in (args.research_root, args.redraw, args.output))
    # docs/architecture keeps one package in a fixed directory; each redraw overwrites it and removes any other package, and earlier ones stay in Git history.
    def is_package(directory):
        manifest = directory / "manifest.json"
        return manifest.is_file() and read(manifest).get("scope") in (SCOPE, "frozen-research-not-current-product")
    if package_dir.exists() and not is_package(package_dir):
        raise SystemExit("Output is not a diagram package; refusing to overwrite it.")
    previous = [d for d in package_dir.parent.iterdir() if d.is_dir() and is_package(d)] if package_dir.parent.exists() else []
    out = work / "package"
    if out.exists():
        shutil.rmtree(out)
    # A run holds one snapshot of the product with its own Roslyn, RepoWise and verified facts; the 2026-10-16 research
    # kept Roslyn at the research root, later runs keep everything under the run directory.
    run = lab / "runs" / args.run
    snapshot, baseline = run / "snapshot", run / "evidence"
    spec = read(work / "evidence-spec.json")
    source_manifest = read(baseline / "source-manifest.json")
    hashes = {f["path"]: f["sha256"] for f in source_manifest["files"]}
    roslyn_dir = baseline / "roslyn" if (baseline / "roslyn/edges.json").is_file() else lab / "evidence/roslyn"
    roslyn = Roslyn(read(roslyn_dir / "edges.json"))
    full_names = roslyn.full_names()
    comparison_path = baseline / "normalized-relations.json" if (baseline / "normalized-relations.json").is_file() else lab / "evidence/normalized-relations.json"

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

    # Facts: the run's verified facts come first, as written there; the spec may add facts a redraw needed for new connections.
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
    if isinstance(model, list):
        # With likec4.config.json the export lists the project once per workspace entry; every copy must agree.
        projects = [p for p in model if p.get("projectId") == "jet"]
        if not projects or any(p["relations"] != projects[0]["relations"] or p["views"] != projects[0]["views"] for p in projects):
            raise ValueError("Unexpected LikeC4 export projects.")
        model = projects[0]
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
                    # Only the node name: the drawing already shows each node's caption, and joining it here buried titles in parentheses.
                    label = lambda node: nodes[node]["label"]
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
    archify_theme = (work / "archify-theme.css").read_text(encoding="utf-8")
    likec4_theme = (work / "likec4/jet-theme.css").read_text(encoding="utf-8")
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
        finalized = work / "archify" / diagram / f"{diagram}.html"
        (out / entry["html"]).parent.mkdir(parents=True, exist_ok=True)
        (out / entry["html"]).write_text(with_theme(finalized.read_text(encoding="utf-8"), archify_theme), encoding="utf-8", newline="\n")
        entry["finalizedHtmlSha256"] = digest(finalized)
        entry["candidateSha256"], entry["htmlSha256"] = digest(out / entry["candidate"]), digest(out / entry["html"])
        diagrams.append(entry)
    likec4_source = (work / "likec4-site/index.html").read_bytes()
    likec4_html, rewritten = portable_likec4_html(likec4_source, work, work / "likec4-site")
    likec4_html = with_theme(likec4_html.decode("utf-8"), likec4_theme).encode("utf-8")
    (out / "likec4").mkdir(parents=True, exist_ok=True)
    (out / "likec4/index.html").write_bytes(likec4_html)
    (out / "likec4/jet.c4").write_text(c4_text, encoding="utf-8")
    for name in ("likec4.config.json", "jet-theme.css"):
        copy(work / "likec4" / name, out / "likec4" / name)
    copy(work / "archify-theme.css", out / "archify/jet-theme.css")
    relation_evidence = {rid: (r.get("metadata") or {}).get("evidence") for rid, r in model["relations"].items()}

    def view_evidence(view):
        # The evidence identifiers of the model relations this view draws, in drawing order.
        found = []
        for edge in model["views"][view].get("edges", []):
            for rid in edge.get("relations", []):
                if relation_evidence.get(rid) and relation_evidence[rid] not in found:
                    found.append(relation_evidence[rid])
        return found
    views = [dict(id=v, title=model["views"][v]["title"], html=f"likec4/index.html#/view/{v}/", evidence=view_evidence(v))
             for group in portal for v in group.get("likec4", [])]
    copy(lab / "tools/archify-3.1.0/archify/LICENSE", out / "licenses/archify-LICENSE.txt")
    copy(lab / "tools/node/node_modules/likec4/LICENSE", out / "licenses/likec4-LICENSE.txt")
    for name in ("class-signatures.json", "sqlite-schema.json", "source-manifest.json"):
        copy(baseline / name, out / "evidence" / name)
    copy(comparison_path, out / "evidence/normalized-relations.json")
    comparison = read(comparison_path)
    save(out / "evidence/verified-facts.json", dict(identity=frozen_facts.get("identity"), verifiedOn=frozen_facts.get("verifiedOn"), facts=facts))
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

    # Every member drawn on a class diagram has exactly one declaration range in the snapshot.
    member_sources = {}
    for diagram, candidate in candidates.items():
        if candidate["diagram_type"] != "class":
            continue
        drawn = {(t["label"], m["name"]): m for t in candidate["types"] for m in t.get("attributes", []) + t.get("methods", [])}
        entries = spec.get("memberSources", {}).get(diagram, [])
        listed = [(e["type"], e["member"]) for e in entries]
        if len(set(listed)) != len(listed) or set(listed) != set(drawn):
            raise ValueError("Member sources differ from the drawn members: " + diagram)
        member_sources[diagram] = []
        for e in entries:
            m = drawn[(e["type"], e["member"])]
            shown = m["name"] + (f"({m.get('parameters', '')})" if "parameters" in m or "returns" in m else "") + \
                (": " + (m.get("returns") or m.get("type")) if (m.get("returns") or m.get("type")) else "")
            member_sources[diagram].append(dict(type=e["type"], member=e["member"], shown=shown, visibility=m.get("visibility", ""),
                                                static=bool(m.get("static")), excerpt=record(dict(path=e["path"], line=e["line"], endLine=e["endLine"]))))
    save(out / "evidence/connection-evidence.json", connections)
    save(out / "evidence/member-sources.json", member_sources)
    save(out / "evidence/source-excerpts.json", snippets)

    commit = source_manifest.get("commit")
    snapshot_label = f"提交 {commit[:7]} " if commit else "2026-10-15 與 2026-10-16 的未提交工作檔"
    write_pages(out, portal, diagrams, views, facts, connections, bindings, member_sources, signatures, schema, snippets, args.redraw_date, comparison, snapshot_label)
    for path in out.rglob("*"):
        if path.is_file() and path.suffix in (".html", ".json", ".md", ".c4") and LOCAL_PATH.search(unquote(path.read_text(encoding="utf-8", errors="ignore"))):
            raise ValueError("A local path remains in " + path.relative_to(out).as_posix())
    files = [dict(path=p.relative_to(out).as_posix(), sha256=digest(p)) for p in sorted(out.rglob("*")) if p.is_file()]
    save(out / "manifest.json", dict(
        schemaVersion=1, scope=SCOPE, snapshotRun=args.run, snapshot=dict(source=source_manifest.get("source"), commit=source_manifest.get("commit"),
                                                                            createdUtc=source_manifest.get("createdUtc")), redrawDate=args.redraw_date,
        versions=dict(repowise="0.56.0", roslyn="5.9.0.0", likec4="1.59.4", archify="3.1.0"),
        diagrams=diagrams, likec4Views=len(views), likec4Model="likec4/jet.c4",
        likec4Export=dict(html="likec4/index.html", sourceSha256=hashlib.sha256(likec4_source).hexdigest(),
                          portableSha256=digest(out / "likec4/index.html"), rewrittenLocalLinks=rewritten,
                          transformation="Rewrite local links relative to the package root, inline the favicon and insert likec4/jet-theme.css; keep the model and layout.",
                          themeConfig="likec4/likec4.config.json"),
        archifyTheme=dict(css="archify/jet-theme.css", preset="editorial",
                          transformation="Insert the colour, heading-font and decoration overrides after finalize; geometry is unchanged. finalizedHtmlSha256 keeps each finalize output."),
        bindings="evidence/diagram-bindings.json", checkedClassRelations=len(bindings),
        connections="evidence/connection-evidence.json", checkedConnections=len(connections),
        memberSources="evidence/member-sources.json", checkedMembers=sum(len(v) for v in member_sources.values()),
        facts="evidence/verified-facts.json", sourceExcerpts="evidence/source-excerpts.json", files=files,
        limitations=["No runtime observation.", "Connection evidence proves sources and drawn line kinds, not business correctness.",
                     "Source excerpts and declarations belong to the snapshot named above, not to the working tree."]))
    for directory in previous:
        shutil.rmtree(directory)
    shutil.move(str(out), str(package_dir))
    print(json.dumps(dict(diagrams=len(diagrams), likec4Views=len(views), connections=len(connections), classRelations=len(bindings),
                          facts=len(facts), sourceRanges=len(snippets), files=len(files) + 1, rewrittenLinks=rewritten), ensure_ascii=False))


CSS = """
:root{color-scheme:light;--bg:#eae5e1;--paper:#f4f1ec;--sunk:#e3ded7;--select:#ead7b5;--ink:#080a08;--muted:#6b6961;--line:#d3cdc4;--rule:#aaa59a;--accent:#7a4a12;--hover:#9b7247;--signal:#a8742a;--caution:#fffa00;--on-caution:#191919;
--on-ink:#f4f1ed;--display:Bahnschrift,"Segoe UI","Microsoft JhengHei UI","Microsoft JhengHei",sans-serif;
--k-calls:#4a4843;--k-viaInterface:#3f6c8c;--k-indirect:#b9772e;--k-return:#a29d93;--k-implements:#8a6a3e;--k-injects:#b9772e;--k-data:#5f7f96;
--k-uses:#8b867c;--k-fact:#5b6b78;--k-dependency:#4a4843;--k-realization:#8a6a3e;--k-inheritance:#8a6a3e}
@media(prefers-color-scheme:dark){:root{color-scheme:dark;--bg:#11181b;--paper:#182125;--sunk:#263136;--select:#3a3324;--ink:#e0e3dc;--muted:#a6b0b1;--line:#2c3a3f;--rule:#536166;--hover:#c5a16b;
--accent:#c5a16b;--signal:#c5a16b;--on-ink:#11181b;--k-calls:#c9ccc5;--k-viaInterface:#7fb0d4;--k-indirect:#d8a565;--k-return:#7d898b;--k-implements:#cfa874;
--k-injects:#d8a565;--k-data:#9fb6c8;--k-uses:#98a3a4;--k-fact:#9fb0bd;--k-dependency:#c9ccc5;--k-realization:#cfa874;--k-inheritance:#cfa874}}
*{box-sizing:border-box}html,body{margin:0;background:var(--bg);color:var(--ink);font:15px/1.7 "Segoe UI","Microsoft JhengHei UI","Microsoft JhengHei",sans-serif}
a{color:var(--accent);text-decoration-thickness:1px;text-underline-offset:3px;transition:color .15s}a:hover{color:var(--ink);text-decoration-color:var(--hover)}
:focus-visible{outline:2px solid var(--signal);outline-offset:2px}
code,pre{font-family:Consolas,"Cascadia Mono",monospace}code,summary,h2,h3,dd,li,small,td{overflow-wrap:anywhere}
.top{position:sticky;top:0;z-index:5;background:var(--bg);border-bottom:1px solid var(--rule)}
.top-inner{max-width:1720px;margin:auto;padding:10px 20px;display:flex;gap:6px 22px;align-items:center;flex-wrap:wrap}
.brand{display:flex;flex-direction:column;margin-right:auto;color:var(--ink);text-decoration:none;line-height:1.15;white-space:nowrap}
.brand b{font:600 19px/1.1 var(--display);letter-spacing:.12em}.brand small{font-size:11.5px;letter-spacing:.08em;color:var(--muted);margin-top:3px}
.top nav{display:flex;gap:2px 20px;flex-wrap:wrap}.top nav a{position:relative;padding:4px 0;text-decoration:none;color:var(--muted);font-size:14px}
.top nav a:hover{color:var(--ink)}.top nav a[aria-current=page]{color:var(--ink);font-weight:600}
.top nav a[aria-current=page]::after{content:"";position:absolute;left:0;right:0;bottom:-11px;height:2px;background:var(--ink)}
main{max-width:1720px;margin:auto;padding:22px 20px 56px}
h1{font:600 26px/1.3 var(--display);letter-spacing:.02em;margin:.1em 0 .4em}h2{font-size:18px;line-height:1.45;margin:.15em 0 .45em;font-weight:600}
h3{font-size:15px;margin:.15em 0}p{max-width:95ch}.muted,small{color:var(--muted)}
.sr-only{position:absolute;width:1px;height:1px;overflow:hidden;clip-path:inset(50%);white-space:nowrap}
.card{background:var(--paper);border:1px solid var(--line)}
.chips{display:flex;flex-wrap:wrap;gap:6px 14px;margin:8px 0 0;padding:0;list-style:none}
.chip{display:inline-flex;align-items:center;gap:7px;padding:0;font-size:12.5px;line-height:1.7;max-width:100%;color:var(--ink)}
.chip::before{content:"";flex:none;width:12px;height:3px;background:var(--kc,var(--muted))}
[data-kind=calls]{--kc:var(--k-calls)}[data-kind=viaInterface]{--kc:var(--k-viaInterface)}[data-kind=indirect]{--kc:var(--k-indirect)}
[data-kind=return]{--kc:var(--k-return)}[data-kind=implements]{--kc:var(--k-implements)}[data-kind=injects]{--kc:var(--k-injects)}
[data-kind=data]{--kc:var(--k-data)}[data-kind=uses]{--kc:var(--k-uses)}[data-kind=fact]{--kc:var(--k-fact)}
[data-kind=dependency]{--kc:var(--k-dependency)}[data-kind=realization]{--kc:var(--k-realization)}[data-kind=inheritance]{--kc:var(--k-inheritance)}
.viewer{display:grid;grid-template-columns:250px minmax(0,1fr) 330px;gap:18px;align-items:start}
.menu,.panel{position:sticky;top:72px;max-height:calc(100vh - 88px);overflow:auto;padding:14px 14px 16px}
input,select{width:100%;padding:6px 2px;font:inherit;border:0;border-bottom:1px solid var(--rule);border-radius:0;background:transparent;color:var(--ink)}
input:focus,select:focus{outline:0;border-bottom-color:var(--ink)}select{cursor:pointer}select option{background:var(--paper)}
.menu h3{font:500 12px/1.5 var(--display);letter-spacing:.12em;color:var(--muted);margin:18px 0 4px;padding-bottom:4px;border-bottom:1px solid var(--line)}
.menu button{position:relative;display:flex;justify-content:space-between;align-items:baseline;gap:10px;width:100%;padding:5px 8px 5px 16px;margin:1px 0;border:0;
border-radius:0;background:none;color:var(--muted);font:inherit;font-size:14px;line-height:1.45;text-align:left;cursor:pointer}
.menu button::before{content:"";position:absolute;left:4px;top:50%;width:1px;height:8px;background:var(--rule);transform:translateY(-50%);transition:width .18s,height .18s,background-color .18s}.menu button:hover{color:var(--ink)}.menu button:hover::before{height:12px;background:var(--muted)}.menu button[aria-current=true]{color:var(--ink);font-weight:600;background:var(--select)}.menu button[aria-current=true]::before{width:3px;height:16px;background:var(--signal)}
.stage{overflow:hidden}.stage-head{justify-content:flex-end;padding:8px 18px;border-bottom:1px solid var(--line)}
.actions{display:flex;gap:18px;flex-wrap:wrap;font-size:13.5px}.actions a{text-decoration:none}
.actions a::after{content:" \\2197";font-family:var(--display)}.actions a:hover{text-decoration:underline}
iframe{display:block;width:100%;height:calc(100vh - 165px);min-height:560px;border:0;background:var(--paper)}
.panel h2{font-size:16px;margin:0 0 6px}.panel h2 small{font:400 13px var(--display);letter-spacing:.04em}.panel p{font-size:13.5px;margin:.3em 0}
.conn-list{list-style:none;margin:8px 0 14px;padding:0}.conn-list li{margin:0;padding:7px 0 7px 12px;border-left:2px solid var(--kc,var(--line));border-bottom:1px solid var(--line)}
.conn-list a{display:block;text-decoration:none;color:var(--ink)}.conn-list a:hover .what{text-decoration:underline}
.conn-list .what{font-size:14px}.conn-list .note{display:block;color:var(--muted);font-size:12.5px}
.facts summary{font-size:14px;font-weight:600;padding:2px 0}.facts-list{margin:4px 0 0;padding-left:1.1em;font-size:14px}
.legend{margin:4px 0 0;font-size:14px}.legend summary{color:var(--accent)}.legend dl{display:grid;grid-template-columns:max-content minmax(0,1fr);gap:4px 16px;margin:8px 0 0}.legend dd{margin:0}
h3.section{margin:16px 0 4px;font-size:13.5px;font-weight:600;color:var(--muted)}.group-body article{padding:10px 0;border-bottom:1px solid var(--line)}
.filters{position:sticky;top:57px;z-index:3;display:grid;grid-template-columns:minmax(0,1.3fr) minmax(0,1fr) minmax(0,1.3fr) auto;gap:14px;align-items:end;padding:12px 16px;margin:12px 0 14px}
.filters label{display:block;font:500 11.5px/1.5 var(--display);letter-spacing:.14em;color:var(--muted)}
.filters output{font:400 13px var(--display);letter-spacing:.04em;color:var(--muted);white-space:nowrap;padding-bottom:7px}
details.group{margin:10px 0;padding:0}details.group>summary{display:flex;gap:12px;align-items:baseline;justify-content:space-between;padding:12px 18px;cursor:pointer;list-style:none}
details.group>summary::-webkit-details-marker{display:none}details.group>summary h2{margin:0;font-size:16.5px}details.group>summary:hover h2{color:var(--accent)}
details.group>summary::after{content:"+";font:400 18px/1 var(--display);color:var(--muted)}details.group[open]>summary::after{content:"\\2212"}
details.group[open]>summary{border-bottom:1px solid var(--line)}.group-body{padding:4px 18px 14px}
.conn{padding:12px 0 12px 14px;margin:0;border-left:2px solid var(--kc,var(--line));border-bottom:1px solid var(--line)}.conn h3{font-size:15px;font-weight:600;line-height:1.6}
.conn h3 .chip{margin-left:10px;font-weight:400}
.conn dl{display:grid;grid-template-columns:7.5em minmax(0,1fr);gap:3px 14px;margin:6px 0 0;font-size:14px}.conn dt{color:var(--muted);font-size:13px}.conn dd{margin:0}
ul.refs{margin:0;padding-left:1.15em}
pre{overflow:auto;max-height:360px;background:var(--sunk);padding:10px 14px;font-size:12.5px;line-height:1.55;border-left:2px solid var(--rule)}
summary{cursor:pointer}.conn:target,article:target,tr:target{outline:1px solid var(--signal);outline-offset:4px;background:var(--select)}
section,article,.conn,details.group,tr{scroll-margin-top:160px}
article.card{padding:14px 18px;margin:10px 0}article.card h2{font-size:16.5px}
table{width:100%;border-collapse:collapse;font-size:14px}th,td{padding:7px 8px;text-align:left;vertical-align:top;border-bottom:1px solid var(--line)}
th{font:500 11.5px/1.5 var(--display);letter-spacing:.12em;color:var(--muted);white-space:nowrap;border-bottom-color:var(--rule)}
.members td:first-child{white-space:nowrap}.table-wrap{overflow-x:auto}.table-wrap table{min-width:620px}
[hidden]{display:none!important}
@media(max-width:1600px){.viewer{grid-template-columns:240px minmax(0,1fr)}.panel{grid-column:2;position:static;max-height:none}
.panel .conn-list{columns:3 300px;column-gap:28px}.panel .conn-list li{break-inside:avoid}.panel .facts-list{columns:3 300px;column-gap:28px}}
@media(prefers-reduced-motion:reduce){*{transition:none!important}}
@media(max-width:860px){.viewer,.filters{grid-template-columns:minmax(0,1fr)}.panel{grid-column:auto}.menu{position:static;max-height:44vh}.filters{position:static}
.top nav a[aria-current=page]::after{bottom:-2px}.legend dl{grid-template-columns:minmax(0,1fr)}
iframe{height:72vh;min-height:420px}main{padding:14px 16px 36px}.top-inner{padding:8px 16px}.conn dl{grid-template-columns:minmax(0,1fr)}.conn dt{margin-top:4px}h1{font-size:23px}}
"""
PAGES = (("index.html", "圖表入口"), ("connections.html", "連線證據"), ("evidence.html", "已核對事實"), ("types.html", "類別與成員"),
         ("schema.html", "資料表欄位"), ("README.md", "閱讀說明"))
HASH_OPEN = ('function openHash(){const id=decodeURIComponent(location.hash.slice(1));if(!id)return;const t=document.getElementById(id);if(!t)return;'
             'let d=t.closest("details");while(d){d.open=true;d=d.parentElement.closest("details")}if(t.tagName==="DETAILS")t.open=true;'
             'requestAnimationFrame(()=>t.scrollIntoView({block:"start"}))}addEventListener("hashchange",openHash);openHash();')


def page(out, name, title, body, redraw_date, script=""):
    nav = "".join(f'<a href="{href}"' + (' aria-current="page"' if href == name else '') + f'>{escape(label)}</a>' for href, label in PAGES)
    (out / name).write_text(
        '<!doctype html><html lang="zh-Hant"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">'
        '<title>' + escape(title) + '</title><style>' + CSS + '</style></head><body><header class="top"><div class="top-inner">'
        '<a class="brand" href="index.html"><b>JET ARCHITECTURE</b><small>架構與流程圖．' + escape(redraw_date) + ' 重繪</small></a><nav>' + nav + '</nav></div></header>'
        '<main>' + body + '</main><script>' + script + '</script></body></html>', encoding="utf-8")


def write_pages(out, portal, diagrams, views, facts, connections, bindings, member_sources, signatures, schema, snippets, redraw_date, comparison, snapshot_label):
    by_id = {d["id"]: d for d in diagrams}
    view_by_id = {v["id"]: v for v in views}
    fact_titles = {f["id"]: f["title"] for f in facts}
    titles = {d["id"]: d["title"] for d in diagrams}
    titles["likec4"] = "LikeC4 模型的全部關係"
    by_diagram = {}
    for c in connections:
        by_diagram.setdefault(c["diagram"], []).append(c)
    kind_text = {"dependency": "依賴", "realization": "實作介面", "inheritance": "繼承"}

    def conn_summary(c):
        return dict(id=c["connection"], d=c["diagram"], f=c["fromLabel"], t=c["toLabel"], l=c["label"], k=c["kind"])

    # Entry page: the menu, the drawing, and this drawing's own connections side by side.
    options, menu = [], ""
    for group in portal:
        menu += "<h3>" + escape(group["title"]) + "</h3>"
        for view in group.get("likec4", []):
            v = view_by_id[view]
            rows = [c for c in by_diagram.get("likec4", []) if c["connection"] in v["evidence"]]
            rows.sort(key=lambda c: v["evidence"].index(c["connection"]))
            options.append(dict(id="likec4-" + view, html=v["html"], evidence="connections.html#likec4", conns=[conn_summary(c) for c in rows],
                                facts=sorted({f for c in rows for f in c["facts"]})))
            menu += f'<button data-diagram="likec4-{escape(view)}">{escape(v["title"])}</button>'
        for diagram in group.get("archify", []):
            d = by_id[diagram]
            entry = dict(id=diagram, html=d["html"])
            if d["type"] == "class":
                rows = [b for b in bindings if b["diagram"] == diagram]
                entry.update(evidence="types.html#m-" + diagram,
                             conns=[dict(id=b["relationship"], f=short(b["source"]), t=short(b["target"]), k=b["kind"], kl=kind_text[b["kind"]],
                                         l="、".join(m.replace("#ctor", "建構子") for m in b["members"]), href="types.html#b-" + diagram + "-" + b["relationship"])
                                    for b in rows], facts=[])
            else:
                rows = by_diagram.get(diagram, [])
                entry.update(evidence="connections.html#" + diagram if rows else "", conns=[conn_summary(c) for c in rows],
                             facts=sorted({f for c in rows for f in c["facts"]}))
            options.append(entry)
            menu += f'<button data-diagram="{escape(diagram)}">{escape(d["title"])}</button>'
    # The drawing comes first; line types are explained once on the connections page.
    body = ('<h1 class="sr-only">JET 架構與流程圖</h1>'
            '<div class="viewer"><aside class="menu card"><input id="search" type="search" placeholder="搜尋圖表" aria-label="搜尋圖表">'
            + menu + '</aside><section class="stage card"><div class="stage-head actions"><a id="evidence">完整證據</a>'
            '<a id="standalone" target="_blank" rel="noopener">在新分頁開啟</a></div><iframe id="diagram" title="目前選取的圖"></iframe></section>'
            '<aside class="panel card" aria-live="polite"><h2>這張圖的線 <small id="connCount"></small></h2><div class="chips" id="kindChips"></div>'
            '<ol class="conn-list" id="connList"></ol><details id="factBlock" class="facts"><summary>引用的事實 <small id="factCount"></small></summary>'
            '<ul class="facts-list" id="factList"></ul></details></aside></div>')
    script = ('const diagrams=' + json.dumps(options, ensure_ascii=False) + ';const kinds=' + json.dumps(KIND_LABELS, ensure_ascii=False) +
              ';const notes=' + json.dumps(KIND_NOTES, ensure_ascii=False) + ';const facts=' + json.dumps(fact_titles, ensure_ascii=False) +
              ';const $=id=>document.getElementById(id);'
              'function el(tag,attrs,text){const e=document.createElement(tag);for(const k in attrs)e.setAttribute(k,attrs[k]);if(text!=null)e.textContent=text;return e}'
              'function show(id){const d=diagrams.find(x=>x.id===id)||diagrams[0];'
              'const src=d.html.endsWith(".html")?d.html+"?theme="+(matchMedia("(prefers-color-scheme: dark)").matches?"dark":"light"):d.html;'
              'if($("diagram").getAttribute("src")!==src)$("diagram").src=src;$("standalone").href=d.html;$("evidence").href=d.evidence;$("evidence").hidden=!d.evidence;'
              'document.querySelectorAll("[data-diagram]").forEach(b=>b.setAttribute("aria-current",String(b.dataset.diagram===d.id)));'
              'const list=$("connList");list.replaceChildren();const counts={};'
              'for(const c of d.conns){counts[c.k]=(counts[c.k]||0)+1;const label=c.kl||kinds[c.k];const li=el("li",{"data-kind":c.k,title:label});'
              'const a=el("a",{href:c.href||("connections.html#c-"+c.d+"-"+c.id)});a.append(el("span",{class:"what"},c.f+" 到 "+c.t));'
              'if(c.l)a.append(el("span",{class:"note"},c.l));li.append(a);list.append(li)}'
              '$("connCount").textContent=d.conns.length+" 條";const chips=$("kindChips");chips.replaceChildren();'
              'for(const k in counts){const label=d.conns.find(c=>c.k===k).kl||kinds[k];chips.append(el("span",{class:"chip","data-kind":k,title:notes[k]?label+"："+notes[k]:label},label+" "+counts[k]))}'
              'const fl=$("factList");fl.replaceChildren();for(const f of d.facts){const li=el("li");li.append(el("a",{href:"evidence.html#f-"+f},facts[f]||f));fl.append(li)}'
              '$("factCount").textContent=d.facts.length+" 項";$("factBlock").hidden=!d.facts.length;history.replaceState(null,"","#"+d.id)}'
              'document.querySelectorAll("[data-diagram]").forEach(b=>b.onclick=()=>{show(b.dataset.diagram);'
              'if(matchMedia("(max-width:860px)").matches)document.querySelector(".stage").scrollIntoView({block:"start"})});'
              '$("search").oninput=e=>{const q=e.target.value.toLowerCase();document.querySelectorAll(".menu button").forEach(b=>b.hidden=!b.textContent.toLowerCase().includes(q))};'
              'show(location.hash.slice(1)||"likec4-index");')
    page(out, "index.html", "JET 架構與流程圖", body, redraw_date, script)

    def excerpt(key, full_path=True):
        s = snippets[key]
        lines = ' 第 ' + str(s["line"]) + (('–' + str(s["endLine"])) if s["endLine"] != s["line"] else '') + ' 行'
        name = s["path"] if full_path else s["path"].split("/")[-1]
        where = '' if full_path else '<p><small>' + escape(s["path"]) + '</small></p>'
        return '<details><summary>' + escape(name) + lines + '</summary>' + where + '<pre>' + escape(s["excerpt"]) + '</pre></details>'

    def edge_item(edge):
        if edge["kind"] in ("implements", "inherits"):
            text = short(edge["source"]) + (" 實作 " if edge["kind"] == "implements" else " 繼承 ") + short(edge["target"])
        elif edge["kind"] == "constructs":
            text = "AppCompositionRoot." + member_name(edge["source"]) + " 建立 " + short(edge["targetType"])
        else:
            text = short(type_of_member(edge["source"])) + " 呼叫 " + short(edge["targetType"]) + "." + member_name(edge["target"])
        return "<li>" + escape(text) + (excerpt(edge["excerpt"], False) if edge.get("excerpt") else "") + "</li>"

    # Connections: one collapsed group per drawing, with filters for drawing, kind and text.
    order = ["likec4"] + [d["id"] for d in diagrams]
    present = [d for d in order if by_diagram.get(d)]
    used_kinds = [k for k in KIND_LABELS if any(c["kind"] == k for c in connections)]
    by_key = {(c["diagram"], c["connection"]): c for c in connections}

    def kind_chip(kind):
        title = KIND_LABELS[kind] + ("：" + KIND_NOTES[kind] if kind in KIND_NOTES else "")
        return f'<span class="chip" data-kind="{kind}" title="{escape(title)}">{escape(KIND_LABELS[kind])}</span>'

    body = ('<h1>連線證據</h1><p class="muted">每條線列出程式裡的呼叫位置，或依據的已核對事實；這些都來自閱讀程式碼，沒有實際執行。'
            '類別圖的關係在<a href="types.html">類別與成員</a>頁。</p>'
            '<details class="legend"><summary>線的種類怎麼分</summary><dl>' +
            "".join(f'<dt>{kind_chip(k)}</dt><dd>{escape(KIND_NOTES[k])}</dd>' for k in used_kinds if k in KIND_NOTES) + '</dl></details>'
            '<div class="filters card"><div><label for="fDiagram">圖表</label><select id="fDiagram"><option value="">全部圖表</option>' +
            "".join(f'<option value="{escape(d)}">{escape(titles[d])}</option>' for d in present) + '</select></div>'
            '<div><label for="fKind">線的種類</label><select id="fKind"><option value="">全部種類</option>' +
            "".join(f'<option value="{k}">{escape(KIND_LABELS[k])}</option>' for k in used_kinds) +
            '</select></div><div><label for="fText">搜尋類別、方法或事實</label><input id="fText" type="search"></div><output id="fCount"></output></div>')
    for diagram in present:
        rows = by_diagram[diagram]
        body += ('<details class="group card" id="' + escape(diagram) + '"><summary><h2>' + escape(titles[diagram]) + '</h2></summary><div class="group-body">')
        for c in rows:
            details = ""
            if c.get("roslyn"):
                details += '<dt>編譯時目標</dt><dd><ul class="refs">' + "".join(edge_item(e) for e in c["roslyn"]) + '</ul></dd>'
            if c.get("implementations") or c.get("assembly"):
                details += '<dt>實際執行的類別</dt><dd><ul class="refs">' + "".join(edge_item(e) for e in c.get("implementations", []) + c.get("assembly", [])) + '</ul></dd>'
            if c["kind"] == "return":
                call = by_key[(diagram, c["returnOf"])]
                details += ('<dt>對應呼叫</dt><dd><a href="#c-' + escape(diagram) + '-' + escape(c["returnOf"]) + '">' + escape(call["fromLabel"]) + ' 到 ' +
                            escape(call["toLabel"]) + (' ' + escape(call["label"]) if call["label"] else '') + '</a></dd>')
            if c["facts"]:
                details += '<dt>事實</dt><dd>' + "、".join(f'<a href="evidence.html#f-{escape(f)}">{escape(fact_titles[f])}</a>' for f in c["facts"]) + '</dd>'
            body += ('<div class="conn" data-kind="' + c["kind"] + '" id="c-' + escape(diagram) + '-' + escape(c["connection"]) + '"><h3>' +
                     escape(c["fromLabel"]) + ' <small>到</small> ' + escape(c["toLabel"]) + kind_chip(c["kind"]) + '</h3><dl>' +
                     ('<dt>圖上文字</dt><dd>' + escape(c["label"]) + '</dd>' if c["label"] else '') + details + '</dl></div>')
        body += '</div></details>'
    script = ('const groups=[...document.querySelectorAll("details.group")];function apply(){const d=fDiagram.value,k=fKind.value,q=fText.value.trim().toLowerCase();'
              'let n=0;for(const g of groups){let any=0;for(const c of g.querySelectorAll(".conn")){const ok=(!k||c.dataset.kind===k)&&(!q||c.textContent.toLowerCase().includes(q));'
              'c.hidden=!ok;if(ok)any++}g.hidden=(d&&g.id!==d)||!any;if(!g.hidden){n+=any;if(d||k||q)g.open=true}}'
              'fCount.textContent=(d||k||q)?"符合 "+n+" 條":"共 "+' + str(len(connections)) + '+" 條，點標題展開"}'
              'for(const e of [fDiagram,fKind])e.onchange=apply;fText.oninput=apply;apply();' + HASH_OPEN)
    page(out, "connections.html", "JET 圖表連線證據", body, redraw_date, script)

    # Verified facts, searchable.
    used = {}
    for c in connections:
        for f in c["facts"]:
            used.setdefault(f, []).append(c)
    status_text = {"proposal-not-executed": "建議程序，未執行", "test-source-read-not-executed": "只讀測試程式，未執行",
                   "source-and-user-requirement": "依專案文件與使用者要求"}
    body = ('<h1>已核對事實</h1><p class="muted">每項事實只列實際讀過的程式或文件範圍；沒有另外標示的，都只讀了程式或文件，沒有實際執行。</p>'
            '<div class="filters card" style="grid-template-columns:minmax(0,1fr) auto"><div><label for="fText">搜尋事實</label><input id="fText" type="search"></div>'
            '<output id="fCount"></output></div>')
    for f in facts:
        refs = "".join('<li><a href="connections.html#c-' + escape(c["diagram"]) + '-' + escape(c["connection"]) + '">' +
                       escape(titles.get(c["diagram"], c["diagram"])) + '：' + escape(c["fromLabel"]) + ' 到 ' + escape(c["toLabel"]) + '</a></li>'
                       for c in used.get(f["id"], []))
        meta = [status_text[f["status"]]] if f["status"] in status_text else []
        meta += [f["addedOn"] + " 補充"] if f.get("addedOn") else []
        body += ('<article class="card fact" id="f-' + escape(f["id"]) + '"><h2>' + escape(f["title"]) + '</h2>' +
                 ('<p class="muted">' + escape("．".join(meta)) + '</p>' if meta else '') +
                 '<p>' + escape(f["claim"]) + '</p>' + "".join(excerpt(f"{s['path']}:{s['line']}-{s['endLine']}") for s in f["sources"]) +
                 ('<details><summary>用到這項事實的連線</summary><ul class="refs">' + refs + '</ul></details>' if refs else '') +
                 '</article>')
    calls = [r for r in comparison["relations"] if r["kind"] == "invokes"]
    matched = sum(r["repowiseStatus"] == "matched" for r in calls)
    other = sum(r["repowiseStatus"] == "different_target" for r in calls)
    missing = len(calls) - matched - other
    body += ('<details class="group card" id="call-comparison"><summary><h2>RepoWise 與 Roslyn 的代表呼叫對照</h2></summary><div class="group-body"><p>'
             f'2026-10-16 的研究人工選定 {len(calls)} 處呼叫，這份快照再比對一次：RepoWise 相符 {matched} 處、漏列 {missing} 處' +
             (f'、指到別的方法 {other} 處' if other else '') + '，Roslyn 全部相符。這是選定樣本，不能換算為全專案正確率。'
             '<a href="evidence/normalized-relations.json">完整對照資料</a></p></div></details>')
    script = ('const items=[...document.querySelectorAll("article.fact")];function apply(){const q=fText.value.trim().toLowerCase();let n=0;'
              'for(const a of items){a.hidden=!!q&&!a.textContent.toLowerCase().includes(q);if(!a.hidden)n++}fCount.textContent=(q?"符合 ":"共 ")+n+" 項"}'
              'fText.oninput=apply;apply();' + HASH_OPEN)
    page(out, "evidence.html", "JET 已核對事實", body, redraw_date, script)

    # Class diagrams: member declarations, relationship evidence, and the frozen selected signatures.
    class_ids = [d["id"] for d in diagrams if d["type"] == "class"]
    visibility = {"public": "+", "private": "-", "package": "~", "protected": "#"}
    body = '<h1>類別與成員</h1><p class="muted">每張類別圖上成員的宣告位置，以及每條關係的 Roslyn 紀錄。</p>'
    for diagram in class_ids:
        body += ('<details class="group card" id="m-' + escape(diagram) + '"><summary><h2>' + escape(titles[diagram]) + '</h2></summary>'
                 '<div class="group-body"><h3 class="section">成員的宣告位置</h3><div class="table-wrap"><table class="members">'
                 '<tr><th>類別</th><th>圖上的成員</th><th>宣告位置與摘錄</th></tr>')
        for m in member_sources.get(diagram, []):
            mark = visibility.get(m["visibility"], "") + (" static" if m["static"] else "")
            body += ('<tr><td>' + escape(m["type"]) + '</td><td><code>' + escape((mark + " " if mark else "") + m["shown"]) + '</code></td><td>' +
                     excerpt(m["excerpt"], False) + '</td></tr>')
        body += ('</table></div><h3 class="section" id="bindings-' + escape(diagram) + '">關係</h3><div class="table-wrap"><table>'
                 '<tr><th>起點</th><th>終點</th><th>種類</th><th>方法</th><th>Roslyn 位置</th></tr>')
        for b in (b for b in bindings if b["diagram"] == diagram):
            places = "".join("<li>" + escape(e["location"]["path"].split("/")[-1]) + " 第 " + str(e["location"]["line"]) + " 行</li>" for e in b["roslyn"])
            body += ('<tr id="b-' + escape(diagram) + '-' + escape(b["relationship"]) + '"><td>' + escape(short(b["source"])) +
                     '</td><td>' + escape(short(b["target"])) + '</td><td><span class="chip" data-kind="' + b["kind"] + '">' + kind_text[b["kind"]] + '</span></td><td>' +
                     escape("、".join(m.replace("#ctor", "建構子") for m in b["members"])) + '</td><td><ul class="refs">' + places + '</ul></td></tr>')
        body += '</table></div></div></details>'
    body += '<details class="group card" id="signatures"><summary><h2>類別圖引用的方法簽章</h2></summary><div class="group-body">'
    for s in signatures:
        sig = s["signature"]
        body += ('<article><h3>' + escape(s["type"]) + '</h3><code>' + escape(sig["returns"] + " " + sig["name"] + "(" + sig["parameters"] + ")") + '</code>' +
                 excerpt(f"{s['source']['path']}:{s['source']['line']}-{s['source']['endLine']}") + '</article>')
    body += '</div></details>'
    page(out, "types.html", "JET 類別與成員", body, redraw_date, HASH_OPEN)

    # SQLite tables, with a table of contents and a search box.
    tables = schema["tables"]
    body = ('<h1>SQLite 資料表欄位</h1><p class="muted">依這份快照的建表與遷移定義整理。這批定義沒有宣告外鍵；'
            '資料表關聯圖的線是程式在 SQL 裡使用的連接欄位。</p><div class="filters card" style="grid-template-columns:minmax(0,1fr) auto">'
            '<div><label for="fText">搜尋表名或欄位</label><input id="fText" type="search"></div><output id="fCount"></output></div>')
    for t in tables:
        body += ('<details class="group card table" id="' + escape(t["name"]) + '"><summary><h2>' + escape(t["name"]) + '</h2></summary><div class="group-body">'
                 '<div class="table-wrap"><table><tr><th>欄位</th><th>型別</th><th>鍵</th><th>宣告</th></tr>' +
                 "".join('<tr><td>' + escape(a["name"]) + '</td><td>' + escape(a["type"]) + '</td><td>' + escape(a.get("key", "")) + '</td><td>' +
                         escape(a.get("declaration", "")) + '</td></tr>' for a in t["attributes"]) + '</table></div>' +
                 "".join(excerpt(f"{s['path']}:{s['line']}-{s['endLine']}") for s in t["sources"]) + '</div></details>')
    script = ('const items=[...document.querySelectorAll("details.table")];function apply(){const q=fText.value.trim().toLowerCase();let n=0;'
              'for(const a of items){const text=a.querySelector("h2").textContent+" "+[...a.querySelectorAll("td:first-child")].map(td=>td.textContent).join(" ");'
              'a.hidden=!!q&&!text.toLowerCase().includes(q);if(q&&!a.hidden)a.open=true;if(!a.hidden)n++}fCount.textContent=(q?"符合 ":"共 ")+n+" 張表"}'
              'fText.oninput=apply;apply();' + HASH_OPEN)
    page(out, "schema.html", "JET SQLite 資料表欄位", body, redraw_date, script)

    (out / "README.md").write_text(
        f"# JET 架構圖表\n\n"
        f"開啟[圖表入口](index.html)。這些圖在 {redraw_date} 依{snapshot_label}的程式快照重繪，沒有反映之後的修改；"
        "程式現況以 src 與測試為準。\n\n"
        "## 怎麼讀\n\n"
        "循序圖與 LikeC4 用三種線條區分呼叫方式：\n\n"
        "- 灰色實線是直接呼叫：程式碼直接呼叫那個類別的方法。\n"
        "- 鋼藍色虛線是介面呼叫：程式碼只呼叫介面；實際執行哪個類別，看 AppCompositionRoot 在啟動時建立哪一個。\n"
        "- 琥珀色線是間接觸發，例如畫面用 action 名稱送出的訊息、事先交出的函式，或逐列讀取的資料。Archify 畫成較粗的實線，LikeC4 畫成虛線。\n\n"
        "其他圖面：\n\n"
        "- 方框顏色兩種工具相同：石墨灰是元件與類別，鋼藍是資料庫，青銅是畫面與介面型別，暖灰是外部資源與檔案。\n"
        "- 循序圖只畫標題寫明的情境與分支，沒畫的分支寫在圖下方的卡片；生命線上的長條是該類別處理呼叫的期間。\n"
        "- 類別圖只畫編譯時存在的依賴、實作與繼承；實作類別不重複列出介面已有的成員。\n"
        "- 資料表關聯圖的線是程式在 SQL 裡使用的連接欄位；這批 SQLite 建表定義沒有宣告外鍵。\n"
        "- 所有連線都來自閱讀程式碼，沒有實際執行。每條線的依據在[連線證據](connections.html)，類別圖的關係與成員在[類別與成員](types.html)。\n\n"
        "## 可編輯來源\n\n"
        "LikeC4 模型在 [jet.c4](likec4/jet.c4)；Archify 的候選 JSON 在各圖的資料夾，例如[篩選情境儲存的循序圖](archify/action-sequence/candidate.json)。"
        "配色檔與封裝方式見儲存庫的 `tools/harness/architecture/README.md`。\n\n"
        "## 第三方授權\n\n"
        "Archify 3.1.0 與 LikeC4 1.59.4 的原始授權保留在 [Archify 授權](licenses/archify-LICENSE.txt)和 [LikeC4 授權](licenses/likec4-LICENSE.txt)。\n",
        encoding="utf-8")


if __name__ == "__main__":
    main()
