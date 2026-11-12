// Build docs/SAT/Diagrams: ERD and UML class diagram, each as Mermaid text (.txt) and PNG.
// The ERD tables and columns are parsed from the product's CREATE TABLE statements; the UML
// classes and relations in source/diagrams/uml.json are checked against the current source.
// Any mismatch stops the build, so a stale diagram cannot be produced silently.
// Needs Node.js with @viz-js/viz and sharp (see scripts/README.md). Usage: node Build-Diagrams.mjs <repo root>
import fs from 'node:fs/promises';
import path from 'node:path';
import { instance } from '@viz-js/viz';
import sharp from 'sharp';

const repo = path.resolve(process.argv[2] || process.cwd());
const sat = path.join(repo, 'docs/SAT'), source = path.join(repo, 'tools/harness/sat-documents/source/diagrams'), out = path.join(sat, 'Diagrams');
await fs.mkdir(out, { recursive: true });
const readJson = async file => JSON.parse(await fs.readFile(file, 'utf8'));
const fail = message => { throw new Error(message); };
const viz = await instance();
const esc = text => text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const INK = '#24323D', NAVY = '#24475D', LIGHT = '#E8EFF3', LINE = '#7A8A96';

async function render(dot, name) {
  const svg = viz.renderString(dot, { format: 'svg', engine: 'dot' });
  await sharp(Buffer.from(svg), { density: 110 }).flatten({ background: '#FFFFFF' }).png().toFile(path.join(out, name + '.png'));
}
const writeText = (name, lines) => fs.writeFile(path.join(out, name + '.txt'), lines.join('\n') + '\n', 'utf8');

// ---------------------------------------------------------------- ERD
function splitTopLevel(body) {
  const parts = []; let depth = 0, current = '';
  for (const ch of body) {
    if (ch === '(') depth++;
    if (ch === ')') depth--;
    if (ch === ',' && depth === 0) { parts.push(current.trim()); current = ''; } else current += ch;
  }
  if (current.trim()) parts.push(current.trim());
  return parts;
}
function parseSchema(text) {
  const start = text.indexOf('SchemaSql'), open = text.indexOf('"""', start), close = text.indexOf('"""', open + 3);
  if (start < 0 || open < 0 || close < 0) fail('SchemaSql block not found');
  const sql = text.slice(open + 3, close).split('\n').map(line => line.replace(/--.*$/, '')).join('\n');
  const tables = new Map();
  for (const match of sql.matchAll(/CREATE TABLE IF NOT EXISTS (\w+)\s*\(([\s\S]*?)\);/g)) {
    const columns = [], primary = new Set();
    for (const item of splitTopLevel(match[2])) {
      const flat = item.replace(/\s+/g, ' ');
      const key = /^PRIMARY KEY \(([^)]*)\)/i.exec(flat);
      if (key) { key[1].split(',').forEach(c => primary.add(c.trim())); continue; }
      if (/^CHECK\b/i.test(flat)) continue;
      const column = /^(\w+) (TEXT|INTEGER)\b(.*)$/i.exec(flat) || fail('Unrecognized column in ' + match[1] + ': ' + flat);
      if (/PRIMARY KEY/i.test(column[3])) primary.add(column[1]);
      columns.push({ name: column[1], type: column[2].toUpperCase(), unique: /\bUNIQUE\b/i.test(column[3]) });
    }
    tables.set(match[1], { columns, primary });
  }
  for (const match of sql.matchAll(/CREATE UNIQUE INDEX IF NOT EXISTS \w+\s+ON (\w+) \(([^)]*)\)/g)) {
    const names = match[2].split(',').map(c => c.trim());
    if (names.length === 1) tables.get(match[1]).columns.find(c => c.name === names[0]).unique = true;
  }
  return tables;
}
{
  const spec = await readJson(path.join(source, 'erd.json'));
  const tables = parseSchema(await fs.readFile(path.join(repo, spec.schemaSource), 'utf8'));
  const grouped = spec.groups.flatMap(g => g.tables);
  const missing = [...tables.keys()].filter(t => !grouped.includes(t)), unknown = grouped.filter(t => !tables.has(t));
  if (missing.length || unknown.length) fail('erd.json groups differ from the schema. Not grouped: ' + missing + '; not in schema: ' + unknown);
  for (const r of spec.relations) {
    if (!tables.has(r.from) || !tables.has(r.to)) fail('Relation uses an unknown table: ' + r.from + ' -> ' + r.to);
    for (const column of r.label.split(',').map(c => c.trim()))
      if (!tables.get(r.from).columns.some(c => c.name === column)) fail('Relation column missing: ' + r.from + '.' + column);
  }
  const mark = (table, column) => table.primary.has(column.name) ? 'PK' : column.unique ? 'UK' : '';
  const mermaid = ['erDiagram'];
  for (const group of spec.groups) {
    mermaid.push('    %% ' + group.label);
    for (const name of group.tables) {
      const table = tables.get(name);
      mermaid.push('    ' + name + ' {');
      for (const column of table.columns) mermaid.push(('        ' + column.type + ' ' + column.name + ' ' + mark(table, column)).trimEnd());
      mermaid.push('    }');
    }
  }
  for (const r of spec.relations)
    mermaid.push('    ' + r.to + ' ' + (r.toOptional ? '|o' : '||') + '--' + (r.oneToOne ? 'o|' : 'o{') + ' ' + r.from + ' : "' + r.label + '"');
  await writeText('ERD', mermaid);

  // One picture per table group, drawn left to right. A table from another group that a relation
  // points at is repeated as a dashed, name-only box. ERD.png stacks the group pictures in two columns.
  const nodeHtml = name => {
    const table = tables.get(name);
    const rows = table.columns.map(c => '<TR><TD ALIGN="LEFT">' + esc(c.name) + '</TD><TD ALIGN="LEFT"><FONT COLOR="#5B6773">' + c.type + '</FONT></TD><TD>' + (mark(table, c) || ' ') + '</TD></TR>').join('');
    return name + ' [label=<<TABLE BORDER="1" CELLBORDER="0" CELLSPACING="0" CELLPADDING="3" COLOR="' + LINE + '"><TR><TD COLSPAN="3" BGCOLOR="' + NAVY + '"><FONT COLOR="#FFFFFF"><B>' + esc(name) + '</B></FONT></TD></TR>' + rows + '</TABLE>>];';
  };
  const pictures = [];
  for (const group of spec.groups) {
    const own = spec.relations.filter(r => group.tables.includes(r.from) || group.tables.includes(r.to));
    const outside = [...new Set(own.flatMap(r => [r.from, r.to]).filter(t => !group.tables.includes(t)))];
    const linked = new Set(own.flatMap(r => [r.from, r.to]));
    const alone = group.tables.filter(t => !linked.has(t));
    const dot = ['digraph ERD {', 'graph [rankdir=LR, nodesep=0.3, ranksep=1.0, fontname="Arial", fontsize=15, fontcolor="' + NAVY + '", labelloc=t, labeljust=l, label="' + group.label + '", pad=0.25];',
      'node [shape=plain, fontname="Arial", fontsize=11, fontcolor="' + INK + '"];', 'edge [fontname="Arial", fontsize=10, color="' + LINE + '", fontcolor="' + INK + '", arrowhead=none, arrowtail=crow, dir=both];'];
    for (const name of group.tables) dot.push(nodeHtml(name));
    for (const name of outside) dot.push(name + ' [label=<<TABLE BORDER="1" CELLBORDER="0" CELLSPACING="0" CELLPADDING="5" COLOR="' + LINE + '" STYLE="dashed"><TR><TD><B>' + esc(name) + '</B></TD></TR></TABLE>>];');
    for (const r of own) dot.push(r.from + ' -> ' + r.to + ' [label="' + r.label + '"' + (r.oneToOne ? ', arrowtail=none' : '') + '];');
    // Tables without a relation would pile up in one column; chain them in rows of three instead.
    for (let i = 0; i + 1 < alone.length; i++) if (i % 3 !== 2) dot.push(alone[i] + ' -> ' + alone[i + 1] + ' [style=invis];');
    dot.push('}');
    const svg = viz.renderString(dot.join('\n'), { format: 'svg', engine: 'dot' });
    const png = await sharp(Buffer.from(svg), { density: 120 }).flatten({ background: '#FFFFFF' }).png().toBuffer();
    pictures.push({ png, ...(await sharp(png).metadata()) });
  }
  const gap = 40, columns = [[], []], heights = [0, 0];
  for (const picture of pictures) { const i = heights[0] <= heights[1] ? 0 : 1; columns[i].push(picture); heights[i] += picture.height + gap; }
  const widths = columns.map(column => Math.max(0, ...column.map(p => p.width)));
  const composite = [];
  columns.forEach((column, i) => { let top = gap; for (const p of column) { composite.push({ input: p.png, left: gap + (i ? widths[0] + gap : 0), top }); top += p.height + gap; } });
  await sharp({ create: { width: widths[0] + widths[1] + gap * 3, height: Math.max(...heights) + gap, channels: 3, background: '#FFFFFF' } }).composite(composite).png().toFile(path.join(out, 'ERD.png'));
  console.log('ERD', tables.size, 'tables', spec.relations.length, 'relations');
}

// ---------------------------------------------------------------- UML
{
  const spec = await readJson(path.join(source, 'uml.json'));
  const root = path.join(repo, spec.sourceRoot), cache = new Map();
  const text = async file => cache.get(file) ?? cache.set(file, await fs.readFile(path.join(root, file), 'utf8')).get(file);
  const names = spec.classes.map(c => c.name), groupedNames = spec.groups.flatMap(g => g.classes);
  if (names.slice().sort().join() !== groupedNames.slice().sort().join()) fail('uml.json groups and classes differ');
  for (const c of spec.classes) if (!(await text(c.file)).includes(c.declaration)) fail('Declaration not found: ' + c.declaration + ' in ' + c.file);
  for (const r of spec.relations) {
    if (!names.includes(r.from) || !names.includes(r.to)) fail('Relation uses an unknown class: ' + r.from + ' -> ' + r.to);
    if (!(await text(r.file)).includes(r.evidence)) fail('Evidence not found: ' + r.evidence + ' in ' + r.file);
  }
  const arrow = { implements: '..|>', creates: '..>', uses: '-->' };
  const mermaid = ['classDiagram'];
  for (const group of spec.groups) {
    mermaid.push('    namespace ' + group.id + ' {');
    for (const name of group.classes) {
      const c = spec.classes.find(x => x.name === name);
      mermaid.push('        class ' + name + ' {');
      if (c.stereotype) mermaid.push('            <<' + c.stereotype + '>>');
      for (const member of c.members) mermaid.push('            ' + member);
      mermaid.push('        }');
    }
    mermaid.push('    }');
  }
  for (const r of spec.relations) mermaid.push('    ' + r.from + ' ' + arrow[r.kind] + ' ' + r.to + (r.label ? ' : ' + r.label : ''));
  await writeText('UML Diagram', mermaid);

  const plain = member => esc(member.replace(/~([^~]*)~/g, '<$1>'));
  const dot = ['digraph UML {', 'graph [rankdir=LR, nodesep=0.35, ranksep=1.0, fontname="Arial", fontsize=16, fontcolor="' + NAVY + '", labelloc=t, label="JET action handling - main classes", pad=0.3, newrank=true];',
    'node [shape=plain, fontname="Arial", fontsize=11, fontcolor="' + INK + '"];', 'edge [fontname="Arial", fontsize=10, color="' + LINE + '", fontcolor="' + INK + '"];'];
  for (const group of spec.groups) {
    dot.push('subgraph cluster_' + group.id + ' { label="' + group.label + '"; style="rounded"; color="' + LINE + '"; fontname="Arial"; fontsize=13; fontcolor="' + NAVY + '";');
    for (const name of group.classes) {
      const c = spec.classes.find(x => x.name === name);
      const head = (c.stereotype ? '<FONT POINT-SIZE="9">&laquo;' + c.stereotype + '&raquo;</FONT><BR/>' : '') + '<B>' + esc(name) + '</B>';
      const rows = c.members.map(m => '<TR><TD ALIGN="LEFT">' + plain(m) + '</TD></TR>').join('');
      dot.push(name + ' [label=<<TABLE BORDER="1" CELLBORDER="0" CELLSPACING="0" CELLPADDING="4" COLOR="' + LINE + '"><TR><TD BGCOLOR="' + LIGHT + '">' + head + '</TD></TR><HR/>' + rows + '</TABLE>>];');
    }
    dot.push('}');
  }
  const style = { implements: 'style=dashed, arrowhead=onormal', creates: 'style=dashed, arrowhead=vee', uses: 'arrowhead=vee' };
  for (const r of spec.relations) dot.push(r.from + ' -> ' + r.to + ' [' + style[r.kind] + (r.label ? ', label=" ' + r.label + '"' : '') + '];');
  dot.push('}');
  await render(dot.join('\n'), 'UML Diagram');
  console.log('UML', spec.classes.length, 'classes', spec.relations.length, 'relations');
}
