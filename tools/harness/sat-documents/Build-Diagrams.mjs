// Build docs/SAT/Diagrams: ERD, class diagram, component diagram and activity diagram.
// Each diagram is written as PlantUML text (.txt) and as a PNG drawn with Graphviz.
// The ERD tables and columns are parsed from the product's CREATE TABLE statements. Every class,
// component, interface, relation and activity node in source/diagrams/*.json names a source file and
// a string that must be found in it. Any mismatch stops the build, so a stale diagram cannot be
// produced silently. The component diagram is checked the other way round as well: a reference between
// two components that the C# source has, and that the diagram neither shows nor lists, stops the build.
// PlantUML itself is not run here; the text keeps to documented syntax.
// The text and the picture of a diagram come from the same data and must show the same things: the
// same title, marks, line ends and order of members. A change to one output needs the other changed too.
// The system overview picture embedded in the technical document (source/diagrams/framework.png) is
// drawn here too. It is a simplified view of the component diagram and is checked against it.
// Needs Node.js with @viz-js/viz and sharp (see README.md). Usage: node Build-Diagrams.mjs <repo root>
import fs from 'node:fs/promises';
import path from 'node:path';
import { instance } from '@viz-js/viz';
import sharp from 'sharp';

const repo = path.resolve(process.argv[2] || process.cwd());
const source = path.join(repo, 'tools/harness/sat-documents/source/diagrams');
const out = path.join(repo, 'docs/SAT/Diagrams');
const structureOut = path.join(out, 'Structure Diagrams'), behaviorOut = path.join(out, 'Behavior diagrams');
for (const dir of [out, structureOut, behaviorOut]) await fs.mkdir(dir, { recursive: true });

const readJson = async file => JSON.parse(await fs.readFile(file, 'utf8'));
const fail = message => { throw new Error(message); };
const viz = await instance();
const esc = text => text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const INK = '#24323D', NAVY = '#24475D', LIGHT = '#E8EFF3', LINE = '#7A8A96', MUTED = '#5B6773', NOTE = '#FFF8DC';
// Chinese labels need a font that has the glyphs; Arial would draw empty boxes.
const CJK_FONT = 'Microsoft JhengHei';

// The deliverables describe the solution folder only, so folder names of the development repository
// must not reach the text files or the pictures. Neither may wording about how the product was developed.
const outsideSolution = [/src[\\/]+JET/i, /\btools[\\/]/i, /\bdocs[\\/]/i, /\.agents/i, /\bAI\b/, /agent/i];
const assertDeliverable = (text, name) => { for (const pattern of outsideSolution) if (pattern.test(text)) fail(name + ' contains wording that must stay out of the deliverables: ' + pattern); };

async function writeText(dir, name, lines) {
  const text = lines.join('\n') + '\n';
  assertDeliverable(text, name + '.txt');
  await fs.writeFile(path.join(dir, name + '.txt'), text, 'utf8');
}
// pixelWidth, when given, replaces density: the picture is rendered that many pixels wide. sharp scales
// an SVG sized in points by the square of density / 72, hence the square root.
async function renderPng(dot, dir, name, adjustSvg = svg => svg, density = 110, engine = 'dot', pixelWidth = 0) {
  assertDeliverable(dot, name + '.png');
  const svg = adjustSvg(viz.renderString(dot, { format: 'svg', engine }));
  if (pixelWidth) density = 72 * Math.sqrt(pixelWidth / Number((/<svg width="([\d.]+)pt"/.exec(svg) ?? fail('SVG width not found in ' + name))[1]));
  const file = path.join(dir, name + '.png');
  await sharp(Buffer.from(svg), { density }).flatten({ background: '#FFFFFF' }).png().toFile(file);
  const { width, height } = await sharp(file).metadata();
  return width + 'x' + height;
}
// Reads product source files relative to a root and checks that a string is still there.
function sourceChecker(root) {
  const cache = new Map();
  const read = async file => cache.get(file) ?? cache.set(file, await fs.readFile(path.join(root, file), 'utf8')).get(file);
  const require = async (file, text, what) => { if (!(await read(file)).includes(text)) fail(what + ': "' + text + '" not found in ' + file); };
  return { read, require };
}
const plantHeader = spec => ['@startuml', ...spec.header.map(line => "' " + line), 'title ' + spec.title, ''];

// Graphviz has no metrics for the Chinese font: it underestimates the width of a label and places
// the text from that estimate. Labels are therefore HTML-like tables whose cells carry a width
// computed here (em widths measured from the font), with the text left-aligned behind a spacer cell.
function emOf(ch) {
  if (ch.codePointAt(0) >= 0x2E80) return 1;
  if (ch === ' ' || ch === ' ') return 0.28;
  if ("ijlI.,:;'|!".includes(ch)) return 0.27;
  if ('ftrJ()[]{}'.includes(ch)) return 0.37;
  if ('-/\\_?*"s'.includes(ch)) return 0.47;
  if ('<>=+~'.includes(ch)) return 0.73;
  if (ch === 'm' || ch === 'M') return 0.95;
  if (ch === 'w') return 0.8;
  if (ch === 'W') return 1.02;
  if (ch >= 'A' && ch <= 'Z') return 0.67;
  if (ch >= '0' && ch <= '9') return 0.58;
  return 0.56;
}
const textWidth = (text, size, bold = false) => Math.ceil([...text].reduce((sum, ch) => sum + emOf(ch), 0) * size * (bold ? 1.04 : 1)) + 3;
const NESTED = '<TABLE BORDER="0" CELLBORDER="0" CELLSPACING="0" CELLPADDING="0">';
// lines: { text, size, bold, underline, color, bg, align: 'left' | 'center', indent, hr }. All rows share one width.
function htmlRows(lines, minWidth = 0) {
  const widths = lines.map(l => textWidth(l.text, l.size ?? 11, l.bold) + (l.indent ?? 0));
  const full = Math.max(minWidth, ...widths);
  return lines.map((l, index) => {
    const space = (l.indent ?? 0) + (l.align === 'left' ? 0 : Math.floor((full - widths[index]) / 2));
    let inner = esc(l.text);
    if (l.underline) inner = '<U>' + inner + '</U>';
    if (l.bold) inner = '<B>' + inner + '</B>';
    const font = '<FONT POINT-SIZE="' + (l.size ?? 11) + '"' + (l.color ? ' COLOR="' + l.color + '"' : '') + '>' + inner + '</FONT>';
    const background = l.bg ? ' BGCOLOR="' + l.bg + '"' : '';
    const row = space > 0
      ? '<TR><TD ALIGN="LEFT"' + background + '>' + NESTED + '<TR><TD WIDTH="' + space + '"></TD><TD ALIGN="LEFT" WIDTH="' + (full - space) + '">' + font + '</TD></TR></TABLE></TD></TR>'
      : '<TR><TD ALIGN="LEFT" WIDTH="' + full + '"' + background + '>' + font + '</TD></TR>';
    return row + (l.hr ? '<HR/>' : '');
  }).join('');
}
const label = (texts, style = {}, padding = 1) => '<<TABLE BORDER="0" CELLBORDER="0" CELLSPACING="0" CELLPADDING="' + padding + '">' + htmlRows(texts.map(text => ({ text, ...style }))) + '</TABLE>>';
// dot puts an edge label beside its line, sometimes far enough to read as the label of another line.
// An edge label therefore has a white background, and labelsOnLines moves it onto its own line and
// draws it after every line: the label interrupts the line it belongs to and no line runs through it.
const edgeLabel = (texts, style = {}) => '<<TABLE BORDER="0" CELLBORDER="0" CELLSPACING="0" CELLPADDING="3" BGCOLOR="#FFFFFF">' + htmlRows(texts.map(text => ({ text, ...style }))) + '</TABLE>>';
function labelsOnLines(svg) {
  const boxOf = points => {
    const xy = points.trim().split(/\s+/).map(point => point.split(',').map(Number));
    return { l: Math.min(...xy.map(p => p[0])), r: Math.max(...xy.map(p => p[0])), t: Math.min(...xy.map(p => p[1])), b: Math.max(...xy.map(p => p[1])) };
  };
  const overlaps = (a, b) => a.l < b.r && a.r > b.l && a.t < b.b && a.b > b.t;
  // What a label must not cover: the frame of a node, or its text when it has no frame, and the titles of the groups.
  const textBoxes = part => [...part.matchAll(/<text[^>]* text-anchor="(\w+)" x="([-\d.]+)" y="([-\d.]+)"[^>]* font-size="([\d.]+)"[^>]*>([^<]*)</g)].map(([, anchor, x, y, size, text]) => {
    const width = textWidth(text, Number(size)), l = Number(x) - (anchor === 'middle' ? width / 2 : anchor === 'end' ? width : 0);
    return { l, r: l + width, t: Number(y) - Number(size), b: Number(y) + Number(size) * 0.3 };
  });
  const taken = [...svg.matchAll(/<g id="node\d+" class="node">[\s\S]*?<\/g>/g)].flatMap(node => { const frame = /<polygon[^>]* points="([^"]+)"/.exec(node[0]); return frame ? [boxOf(frame[1])] : textBoxes(node[0]); });
  const GROUP = /<g id="clust\d+" class="cluster">[\s\S]*?<\/g>/g;
  for (const group of svg.matchAll(GROUP)) taken.push(...textBoxes(group[0]));
  // Points along every line and along the frame of every group, a few points apart.
  const EDGE = /<g id="edge\d+" class="edge">[\s\S]*?<\/g>/g;
  const pointsOf = part => {
    const path = /<path[^>]* d="M([-\d.,]+)C([-\d.,\s]+)"/.exec(part);
    if (!path) return [];
    const n = (path[1] + ' ' + path[2]).match(/-?\d+(?:\.\d+)?/g).map(Number), points = [];
    for (let i = 0; i + 7 < n.length; i += 6)
      for (let t = 0; t < 1; t += 0.01) { const u = 1 - t; points.push([0, 1].map(k => u * u * u * n[i + k] + 3 * u * u * t * n[i + 2 + k] + 3 * u * t * t * n[i + 4 + k] + t * t * t * n[i + 6 + k])); }
    return points;
  };
  const allLines = [...svg.matchAll(EDGE)].map(edge => pointsOf(edge[0]));
  const frames = [...svg.matchAll(GROUP)].map(group => pointsOf(group[0]));
  const labels = [];
  let index = 0;
  const lines = svg.replace(EDGE, edge => {
    const samples = allLines[index++], at = edge.indexOf('<polygon fill="#ffffff" stroke="none"');
    if (at < 0) return edge;
    if (!samples.length) fail('Edge line not found for a label');
    const labelSvg = edge.slice(at, -'</g>'.length), box = boxOf(/points="([^"]+)"/.exec(labelSvg)[1]);
    // The label goes to the point of its line nearest to where dot put it at which it covers neither
    // end of the line (the arrowheads), no node, no other label, no other line and no group frame;
    // failing that, at which it covers only a group frame. When the line has no such point, the label
    // stays where dot put it, unless it covers a node or a label there.
    const centre = [(box.l + box.r) / 2, (box.t + box.b) / 2];
    const moved = (p, margin = 0) => ({ l: box.l + p[0] - centre[0] - margin, r: box.r + p[0] - centre[0] + margin, t: box.t + p[1] - centre[1] - margin, b: box.b + p[1] - centre[1] + margin });
    const inside = (q, b) => q[0] > b.l && q[0] < b.r && q[1] > b.t && q[1] < b.b;
    const candidates = samples.filter(p => !inside(samples[0], moved(p, 10)) && !inside(samples.at(-1), moved(p, 10))).sort((p, q) => Math.hypot(p[0] - centre[0], p[1] - centre[1]) - Math.hypot(q[0] - centre[0], q[1] - centre[1]));
    const free = p => !taken.some(other => overlaps(moved(p, 2), other));
    const clearOf = (others, p) => { const b = moved(p, 3); return !others.some(other => other !== samples && other.some(q => inside(q, b))); };
    const spot = candidates.find(p => free(p) && clearOf(allLines, p) && clearOf(frames, p)) ?? candidates.find(p => free(p) && clearOf(allLines, p)) ?? (free(centre) ? centre : candidates.find(free) ?? centre);
    taken.push(moved(spot));
    labels.push('<g transform="translate(' + (spot[0] - centre[0]).toFixed(2) + ',' + (spot[1] - centre[1]).toFixed(2) + ')">\n' + labelSvg + '</g>\n');
    return edge.slice(0, at) + '</g>';
  });
  // The title of a group is drawn again after every line, on a white background: a line that crosses
  // the title passes behind it instead of through its text.
  const titles = [...lines.matchAll(GROUP)].flatMap(group => [...group[0].matchAll(/<text[^>]*>[^<]*<\/text>/g)].map(text => {
    const [box] = textBoxes(text[0]);
    return box ? '<rect x="' + (box.l - 4).toFixed(2) + '" y="' + (box.t - 2).toFixed(2) + '" width="' + (box.r - box.l + 8).toFixed(2) + '" height="' + (box.b - box.t + 4).toFixed(2) + '" fill="#ffffff" stroke="none"/>\n' + text[0] + '\n' : '';
  }));
  const end = lines.lastIndexOf('</g>');
  return lines.slice(0, end) + '<g class="group-titles">\n' + titles.join('') + '</g>\n<g class="edge-labels">\n' + labels.join('') + '</g>\n' + lines.slice(end);
}
const boxed = (lines, attributes = '') => '<<TABLE BORDER="1" CELLBORDER="0" CELLSPACING="0" CELLPADDING="4" COLOR="' + LINE + '"' + attributes + '>' + htmlRows(lines) + '</TABLE>>';
// Breaks a long line after one of the given separators so that no piece is wider than maxWidth.
function wrap(text, size, maxWidth, separators) {
  const pieces = []; let current = '';
  const tokens = text.split(new RegExp('(?<=' + separators + ')'));
  for (const token of tokens) {
    if (current && textWidth(current + token, size) > maxWidth) { pieces.push(current.trimEnd()); current = token; } else current += token;
  }
  if (current) pieces.push(current.trimEnd());
  return pieces;
}

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
      columns.push({ name: column[1], type: column[2].toUpperCase(), unique: /\bUNIQUE\b/i.test(column[3]), notNull: /\bNOT NULL\b/i.test(column[3]) });
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
  if (!(await fs.readFile(path.join(repo, spec.databaseFileEvidence.file), 'utf8')).includes(spec.databaseFileEvidence.text)) fail('Database file name not found: ' + spec.databaseFileEvidence.text);
  const grouped = spec.groups.flatMap(g => g.tables);
  const missing = [...tables.keys()].filter(t => !grouped.includes(t)), unknown = grouped.filter(t => !tables.has(t));
  if (missing.length || unknown.length) fail('erd.json groups differ from the schema. Not grouped: ' + missing + '; not in schema: ' + unknown);
  for (const r of spec.relations) {
    if (!tables.has(r.from) || !tables.has(r.to)) fail('Relation uses an unknown table: ' + r.from + ' -> ' + r.to);
    // A label lists the related columns of the child table. "a = b" names the column of the parent table
    // as well; a column written alone has the same name in the parent table or stands for its one-column primary key.
    const child = tables.get(r.from), parent = tables.get(r.to), pairs = r.label.split(',').map(item => item.split('=').map(c => c.trim()));
    for (const [column, parentColumn] of pairs) {
      if (!child.columns.some(c => c.name === column)) fail('Relation column missing: ' + r.from + '.' + column);
      if (parentColumn !== undefined) { if (!parent.columns.some(c => c.name === parentColumn)) fail('Relation column missing: ' + r.to + '.' + parentColumn); }
      else if (!parent.columns.some(c => c.name === column) && parent.primary.size !== 1)
        fail('Relation ' + r.from + ' -> ' + r.to + ': the column of ' + r.to + ' that matches ' + column + ' is not clear; write "' + column + ' = <column>"');
    }
    // Identifying relation: the related columns are part of the child table's primary key.
    r.identifying = pairs.every(([column]) => child.primary.has(column));
  }
  const mark = (table, column) => table.primary.has(column.name) ? 'PK' : column.unique ? 'UK' : '';
  const required = (table, column) => table.primary.has(column.name) || column.notNull;
  // The text and the picture list the primary key columns first, then the other columns below a line.
  const keyFirst = table => [table.columns.filter(c => table.primary.has(c.name)), table.columns.filter(c => !table.primary.has(c.name))];

  // No "skinparam linetype ortho": Graphviz does not place edge labels on orthogonal lines, and every
  // relation here carries the related column as its label.
  const plant = [...plantHeader(spec), 'hide circle', ''];
  for (const group of spec.groups) {
    plant.push('package "' + group.label + '" {');
    for (const name of group.tables) {
      const table = tables.get(name), [keys, others] = keyFirst(table);
      const line = column => '    ' + (required(table, column) ? '* ' : '') + column.name + ' : ' + column.type + (mark(table, column) ? ' <<' + mark(table, column) + '>>' : '');
      plant.push('  entity ' + name + ' {', ...keys.map(line), '    --', ...others.map(line), '  }');
    }
    plant.push('}', '');
  }
  for (const r of spec.relations)
    plant.push(r.to + ' ' + (r.toOptional ? '|o' : '||') + (r.identifying ? '--' : '..') + (r.oneToOne ? 'o|' : 'o{') + ' ' + r.from + ' : ' + r.label);
  plant.push('@enduml');
  await writeText(out, 'ERD', plant);

  // One picture per table group, drawn left to right. A table from another group that a relation
  // points at is repeated as a dashed, name-only box. ERD.png stacks the group pictures in two columns
  // under the title and the notation, which are the same as in the text file.
  const nodeHtml = name => {
    const table = tables.get(name), [keys, others] = keyFirst(table);
    const row = c => '<TR><TD>' + (required(table, c) ? '*' : ' ') + '</TD><TD ALIGN="LEFT">' + esc(c.name) + '</TD><TD ALIGN="LEFT"><FONT COLOR="' + MUTED + '">' + c.type + '</FONT></TD><TD>' + (mark(table, c) || ' ') + '</TD></TR>';
    const tableRows = keys.map(row).join('') + (keys.length && others.length ? '<HR/>' : '') + others.map(row).join('');
    return name + ' [label=<<TABLE BORDER="1" CELLBORDER="0" CELLSPACING="0" CELLPADDING="3" COLOR="' + LINE + '"><TR><TD COLSPAN="4" BGCOLOR="' + NAVY + '"><FONT COLOR="#FFFFFF"><B>' + esc(name) + '</B></FONT></TD></TR>' + tableRows + '</TABLE>>];';
  };
  // Line ends in IE notation, the shape next to the table first: two bars for exactly one, a bar and
  // a circle for zero or one, a crow's foot and a circle for zero or many.
  const END = { one: 'teetee', zeroOrOne: 'teeodot', zeroOrMany: 'crowodot' };
  const DENSITY = 120;
  const pictures = [];
  for (const group of spec.groups) {
    const own = spec.relations.filter(r => group.tables.includes(r.from) || group.tables.includes(r.to));
    const outside = [...new Set(own.flatMap(r => [r.from, r.to]).filter(t => !group.tables.includes(t)))];
    const linked = new Set(own.flatMap(r => [r.from, r.to]));
    const alone = group.tables.filter(t => !linked.has(t));
    const groupLabel = '<<TABLE BORDER="0" CELLBORDER="0" CELLSPACING="0" CELLPADDING="0"><TR><TD ALIGN="LEFT" WIDTH="' + textWidth(group.label, 15) + '"><FONT FACE="' + CJK_FONT + '" POINT-SIZE="15" COLOR="' + NAVY + '">' + esc(group.label) + '</FONT></TD></TR></TABLE>>';
    const dot = ['digraph ERD {', 'graph [rankdir=LR, nodesep=0.3, ranksep=1.0, fontname="Arial", labelloc=t, labeljust=l, label=' + groupLabel + ', pad=0.25];',
      'node [shape=plain, fontname="Arial", fontsize=11, fontcolor="' + INK + '"];', 'edge [fontname="Arial", fontsize=10, color="' + LINE + '", fontcolor="' + INK + '", dir=both];'];
    for (const name of group.tables) dot.push(nodeHtml(name));
    for (const name of outside) dot.push(name + ' [label=<<TABLE BORDER="1" CELLBORDER="0" CELLSPACING="0" CELLPADDING="5" COLOR="' + LINE + '" STYLE="dashed"><TR><TD><B>' + esc(name) + '</B></TD></TR></TABLE>>];');
    // The edge runs from the child table to the parent table: the tail is the child end.
    // A label with several columns takes one line per column, so that it does not stretch the picture.
    // It is a table with padding, which keeps its last line clear of the relation line below it.
    const relationLabel = r => r.label.includes(',')
      ? '<<TABLE BORDER="0" CELLBORDER="0" CELLSPACING="0" CELLPADDING="3"><TR><TD>' + r.label.split(',').map(item => esc(item.trim())).join(',<BR/>') + '</TD></TR></TABLE>>'
      : '"' + r.label + '"';
    for (const r of own) dot.push(r.from + ' -> ' + r.to + ' [label=' + relationLabel(r) + ', arrowtail=' + (r.oneToOne ? END.zeroOrOne : END.zeroOrMany) + ', arrowhead=' + (r.toOptional ? END.zeroOrOne : END.one) + (r.identifying ? '' : ', style=dashed') + '];');
    // Tables without a relation would pile up in one column; chain them in rows of three instead.
    for (let i = 0; i + 1 < alone.length; i++) if (i % 3 !== 2) dot.push(alone[i] + ' -> ' + alone[i + 1] + ' [style=invis];');
    dot.push('}');
    assertDeliverable(dot.join('\n'), 'ERD.png');
    const svg = viz.renderString(dot.join('\n'), { format: 'svg', engine: 'dot' });
    const png = await sharp(Buffer.from(svg), { density: DENSITY }).flatten({ background: '#FFFFFF' }).png().toBuffer();
    pictures.push({ png, ...(await sharp(png).metadata()) });
  }
  // Title and notation: the header lines of the text file after the first, without the PlantUML brackets.
  const notation = [...spec.header.slice(1).map(line => line.replace(/<<(\w+)>>/g, '$1')), spec.pictureNote];
  const headDot = 'digraph Head { graph [pad=0.05]; node [shape=plain, fontname="' + CJK_FONT + '", fontcolor="' + INK + '"]; head [label=<<TABLE BORDER="0" CELLBORDER="0" CELLSPACING="0" CELLPADDING="3">'
    + htmlRows([{ text: spec.title, size: 20, color: NAVY, align: 'left' }, ...notation.map(text => ({ text, size: 12, align: 'left' }))]) + '</TABLE>>]; }';
  assertDeliverable(headDot, 'ERD.png');
  const headPng = await sharp(Buffer.from(viz.renderString(headDot, { format: 'svg', engine: 'dot' })), { density: DENSITY }).flatten({ background: '#FFFFFF' }).png().toBuffer();
  const head = await sharp(headPng).metadata();
  const gap = 40, columns = [[], []], heights = [0, 0];
  for (const picture of pictures) { const i = heights[0] <= heights[1] ? 0 : 1; columns[i].push(picture); heights[i] += picture.height + gap; }
  const widths = columns.map(column => Math.max(0, ...column.map(p => p.width)));
  const size = { width: Math.max(widths[0] + widths[1] + gap * 3, head.width + gap * 2), height: head.height + gap * 2 + Math.max(...heights) };
  const composite = [{ input: headPng, left: gap, top: gap }];
  columns.forEach((column, i) => { let top = head.height + gap * 2; for (const p of column) { composite.push({ input: p.png, left: gap + (i ? widths[0] + gap : 0), top }); top += p.height + gap; } });
  // withMetadata records the resolution the group pictures were rendered at, as the other pictures do.
  await sharp({ create: { ...size, channels: 3, background: '#FFFFFF' } }).composite(composite).withMetadata({ density: DENSITY }).png().toFile(path.join(out, 'ERD.png'));
  console.log('ERD:', tables.size, 'tables,', spec.relations.length, 'relations,', size.width + 'x' + size.height);
}

// ---------------------------------------------------------------- Class diagram
{
  const spec = await readJson(path.join(source, 'uml.json'));
  const check = sourceChecker(path.join(repo, spec.sourceRoot));
  const names = spec.classes.map(c => c.name), groupedNames = spec.groups.flatMap(g => g.classes);
  if (names.slice().sort().join() !== groupedNames.slice().sort().join()) fail('uml.json groups and classes differ');
  for (const c of spec.classes) {
    await check.require(c.file, c.declaration, 'Class declaration');
    for (const member of c.members) await check.require(member.file ?? c.file, member.evidence, 'Member of ' + c.name);
  }
  for (const r of spec.relations) {
    if (!names.includes(r.from) || !names.includes(r.to)) fail('Relation uses an unknown class: ' + r.from + ' -> ' + r.to);
    await check.require(r.file, r.evidence, 'Relation ' + r.from + ' -> ' + r.to);
  }
  const byName = name => spec.classes.find(c => c.name === name);
  const relationLabel = r => r.kind === 'creates' ? '建立' : r.label ?? '';
  // PlantUML puts members without parentheses (fields) in a compartment above the methods. The source
  // lists them in that order so that the text and the picture show the members the same way.
  const isField = member => !member.text.includes('(');
  for (const c of spec.classes) if (c.members.some((member, index) => isField(member) && index > 0 && !isField(c.members[index - 1]))) fail('uml.json: list the fields of ' + c.name + ' before its methods');

  const arrow = { implements: '..|>', creates: '..>', uses: '-->' };
  const plant = [...plantHeader(spec), 'left to right direction', 'skinparam classAttributeIconSize 0', 'hide empty members', ''];
  for (const group of spec.groups) {
    plant.push('package "' + group.label + '" {');
    for (const name of group.classes) {
      const c = byName(name);
      plant.push('  ' + (c.stereotype === 'interface' ? 'interface ' + name : 'class ' + name + (c.stereotype ? ' <<' + c.stereotype + '>>' : '')) + ' {');
      for (const member of c.members) plant.push('    ' + member.text + (member.static ? ' {static}' : ''));
      plant.push('  }');
    }
    plant.push('}', '');
  }
  for (const r of spec.relations) plant.push(r.from + ' ' + arrow[r.kind] + ' ' + r.to + (relationLabel(r) ? ' : ' + relationLabel(r) : ''));
  plant.push('@enduml');
  await writeText(structureOut, 'Class Diagram', plant);

  // Picture: one column per group, in the order of the groups. An edge that would point back to an
  // earlier column is written the other way round with dir=back, so the arrowhead stays correct.
  const column = new Map(spec.groups.flatMap((group, index) => group.classes.map(name => [name, index])));
  const dot = ['digraph ClassDiagram {', 'graph [rankdir=LR, nodesep=0.3, ranksep=1.3, fontname="' + CJK_FONT + '", labelloc=t, label=' + label([spec.title], { size: 18, color: NAVY }, 4) + ', pad=0.3, newrank=true];',
    'node [shape=plain, fontname="' + CJK_FONT + '", fontsize=11, fontcolor="' + INK + '"];', 'edge [fontname="' + CJK_FONT + '", fontsize=10, color="' + LINE + '", fontcolor="' + INK + '"];'];
  for (const group of spec.groups) {
    dot.push('subgraph cluster_' + group.id + ' { label=' + label([group.label], { size: 13, color: NAVY }, 3) + '; style="rounded"; color="' + LINE + '";');
    for (const name of group.classes) {
      const c = byName(name);
      const lines = [...(c.stereotype ? [{ text: '«' + c.stereotype + '»', size: 9, bg: LIGHT }] : []), { text: name, bold: true, bg: LIGHT, hr: true }];
      c.members.forEach((member, at) => {
        wrap(member.text, 10, 340, ', ').forEach((piece, index) => lines.push({ text: piece, size: 10, align: 'left', underline: !!member.static, indent: index ? 14 : 0 }));
        // A line between the fields and the methods, as PlantUML draws its two compartments.
        if (isField(member) && c.members[at + 1] && !isField(c.members[at + 1])) lines.at(-1).hr = true;
      });
      dot.push(name + ' [label=' + boxed(lines) + '];');
    }
    dot.push('{ rank=same; ' + group.classes.join('; ') + '; }', '}');
  }
  const style = { implements: ['style=dashed', 'onormal'], creates: ['style=dashed', 'vee'], uses: ['style=solid', 'vee'] };
  for (const r of spec.relations) {
    const [line, head] = style[r.kind], text = relationLabel(r) ? ', label=' + edgeLabel([relationLabel(r)], { size: 10 }) : '';
    const back = column.get(r.from) > column.get(r.to), flat = column.get(r.from) === column.get(r.to);
    // tailPort and headPort name the side of the class (n, e, s, w) where the line leaves and arrives.
    const ports = (r.tailPort ? ', ' + (back ? 'headport=' : 'tailport=') + r.tailPort : '') + (r.headPort ? ', ' + (back ? 'tailport=' : 'headport=') + r.headPort : '');
    dot.push((back ? r.to + ' -> ' + r.from : r.from + ' -> ' + r.to) + ' [' + line + (back ? ', dir=back, arrowtail=' + head : ', arrowhead=' + head) + (flat ? ', constraint=false' : '') + ports + text + '];');
  }
  dot.push('}');
  const size = await renderPng(dot.join('\n'), structureOut, 'Class Diagram', labelsOnLines);
  console.log('Class Diagram:', spec.classes.length, 'classes,', spec.relations.length, 'relations,', size);
}

// ---------------------------------------------------------------- Component diagram
{
  const spec = await readJson(path.join(source, 'component.json'));
  const root = path.join(repo, spec.sourceRoot), check = sourceChecker(root);
  const ids = spec.components.map(c => c.id), placed = [...spec.groups.flatMap(g => g.components), ...spec.ungrouped];
  if (ids.slice().sort().join() !== placed.slice().sort().join()) fail('component.json groups and components differ');
  const byId = id => spec.components.find(c => c.id === id) ?? fail('Unknown component: ' + id);
  for (const c of spec.components) {
    for (const item of c.paths ?? []) await fs.access(path.join(root, item)).catch(() => fail('Component path not found: ' + item));
    for (const e of c.evidence) await check.require(e.file, e.text, 'Component ' + c.id);
    if (c.fileCount) {
      const found = (await fs.readdir(path.join(root, c.fileCount.directory))).filter(name => name.endsWith(c.fileCount.extension)).length;
      if (found !== c.fileCount.count) fail('Component ' + c.id + ' expects ' + c.fileCount.count + ' ' + c.fileCount.extension + ' files, found ' + found);
    }
  }
  for (const i of spec.interfaces) {
    for (const e of [i.declaration, ...(i.moreDeclarations ?? [])]) await check.require(e.file, e.text, 'Interface ' + i.id + ' declaration');
    for (const e of [i.providedBy, ...(i.moreProviders ?? [])]) await check.require(e.file, e.text, 'Interface ' + i.id + ' provider');
    byId(i.providedBy.component);
    for (const u of i.usedBy) { byId(u.component); for (const e of [u, ...(u.more ?? [])]) await check.require(e.file, e.text, 'Interface ' + i.id + ' user ' + u.component); }
  }
  for (const d of spec.dependencies) { byId(d.from); byId(d.to); for (const e of [d, ...(d.more ?? [])]) await check.require(e.file, e.text, 'Dependency ' + d.from + ' -> ' + d.to); }
  for (const n of spec.notes) { byId(n.of); for (const e of n.evidence) await check.require(e.file, e.text, 'Note of ' + n.of); }
  // The picture shows the main dependencies. Every reference from one component to another that the C#
  // source has must be drawn (a dependency, or the use of an interface the other component provides),
  // stated in a note ("covers") or listed under "undrawn" with a reason; and every "covers" or "undrawn"
  // entry must still be in the source. So a reference the picture is silent about stops the build, in
  // whichever direction it runs. A reference is a using directive for a namespace the other component
  // declares. Components that share a namespace are told apart by the type names one of them declares and
  // the other mentions. A file outside every component folder belongs to the component whose paths are files.
  async function sourceFiles(dir, top = true) {
    const found = [];
    for (const entry of await fs.readdir(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) { if (!(top && ['bin', 'obj'].includes(entry.name))) found.push(...await sourceFiles(full, false)); }
      else if (entry.name.endsWith('.cs')) found.push(full);
    }
    return found;
  }
  {
    const folders = c => (c.paths ?? []).filter(item => !item.endsWith('.cs'));
    const rest = spec.components.find(c => c.paths?.length && !folders(c).length) ?? fail('No component owns the source files outside the component folders');
    const code = new Map(), declares = new Map(), typeOwners = new Map();
    for (const file of await sourceFiles(root)) {
      const relative = path.relative(root, file).split(path.sep).join('/');
      const owner = (spec.components.find(c => folders(c).some(folder => relative.startsWith(folder + '/'))) ?? rest).id;
      const text = (await fs.readFile(file, 'utf8')).replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/.*$/gm, '');
      if (!code.has(owner)) code.set(owner, []);
      code.get(owner).push({ relative, text });
      for (const [, name] of text.matchAll(/^\s*namespace\s+([\w.]+)/gm)) declares.set(name, (declares.get(name) ?? new Set()).add(owner));
      for (const [, name] of text.matchAll(/\b(?:class|record\s+struct|record|struct|interface|enum)\s+([A-Z]\w*)/g)) typeOwners.set(name, (typeOwners.get(name) ?? new Set()).add(owner));
    }
    // Type names declared by exactly one component, as one pattern per component.
    const typesOf = new Map([...code.keys()].map(id => {
      const names = [...typeOwners].filter(([, owners]) => owners.size === 1 && owners.has(id)).map(([name]) => name);
      return [id, names.length ? new RegExp('\\b(?:' + names.join('|') + ')\\b') : null];
    }));
    const found = new Map();
    for (const [from, files] of code) {
      const sharing = [...declares.values()].filter(owners => owners.has(from)).flatMap(owners => [...owners]).filter(id => id !== from);
      for (const { relative, text } of files) {
        const see = (to, what) => { if (!found.has(from + ' -> ' + to)) found.set(from + ' -> ' + to, relative + ' (' + what + ')'); };
        const byType = to => { const type = typesOf.get(to)?.exec(text); if (type) see(to, type[0]); };
        for (const [, name] of text.matchAll(/^\s*(?:global\s+)?using\s+(?:static\s+)?(?:\w+\s*=\s*)?(JET(?:\.\w+)*)\s*;/gm)) {
          const namespace = [...declares.keys()].filter(known => name === known || name.startsWith(known + '.')).sort((a, b) => b.length - a.length)[0];
          const targets = namespace ? [...declares.get(namespace)].filter(id => id !== from) : [];
          if (targets.length === 1) see(targets[0], 'using ' + name); else targets.forEach(byType);
        }
        sharing.forEach(byType);
      }
    }
    const drawn = new Set([...spec.dependencies.map(d => d.from + ' -> ' + d.to), ...spec.interfaces.flatMap(i => i.usedBy.map(u => u.component + ' -> ' + i.providedBy.component))]);
    const stated = new Set();
    for (const entry of [...spec.notes.flatMap(n => n.covers ?? []), ...spec.undrawn]) {
      byId(entry.from); byId(entry.to);
      const key = entry.from + ' -> ' + entry.to;
      if (!found.has(key)) fail('component.json lists ' + key + ', which the source no longer has');
      if (drawn.has(key) || stated.has(key)) fail('component.json lists ' + key + ' more than once');
      stated.add(key);
    }
    for (const entry of spec.undrawn) if (!entry.reason) fail('An undrawn reference needs a reason: ' + entry.from + ' -> ' + entry.to);
    for (const [key, where] of found) if (!drawn.has(key) && !stated.has(key)) fail(key + ' (' + where + ') is in the source, but the component diagram neither shows nor lists it');
  }

  const pathText = c => c.paths ? spec.pathPrefix + c.paths.join('、') : '';
  const interfaceLines = i => [i.name, ...(i.definedIn ? ['（' + i.definedIn + ' 定義）'] : [])];
  const component = (c, indent) => indent + 'component "' + [c.name, pathText(c), c.role].filter(Boolean).join('\\n') + '" as ' + c.id + (c.stereotype ? ' <<' + c.stereotype + '>>' : '');
  const plant = [...plantHeader(spec)];
  for (const id of spec.ungrouped) plant.push(component(byId(id), ''));
  plant.push('');
  for (const group of spec.groups) plant.push('package "' + group.label + '" {', ...group.components.map(id => component(byId(id), '  ')), '}', '');
  for (const i of spec.interfaces) plant.push('interface "' + interfaceLines(i).join('\\n') + '" as ' + i.id);
  plant.push('');
  for (const i of spec.interfaces) {
    plant.push(i.providedBy.component + ' - ' + i.id);
    for (const u of i.usedBy) plant.push(u.component + ' ..> ' + i.id + ' : ' + u.label);
  }
  plant.push('');
  for (const d of spec.dependencies) plant.push(d.from + ' ..> ' + d.to + ' : ' + d.label);
  plant.push('');
  for (const n of spec.notes) plant.push('note ' + n.side + ' of ' + n.of, ...n.lines.map(line => '  ' + line), 'end note');
  plant.push('@enduml');
  await writeText(structureOut, 'Component Diagram', plant);

  // Rank of each component from top to bottom. An edge that would point upward is written the other
  // way round with dir=back, so the picture keeps the layers in order while the arrowhead stays correct.
  const rank = new Map(spec.ungrouped.map(id => [id, id === 'host' ? 0 : spec.groups.length]));
  spec.groups.forEach((group, index) => group.components.forEach(id => rank.set(id, index + 1)));
  const dot = ['digraph ComponentDiagram {', 'graph [rankdir=TB, nodesep=0.3, ranksep=0.8, fontname="' + CJK_FONT + '", labelloc=t, label=' + label([spec.title], { size: 20, color: NAVY }, 4) + ', pad=0.3, newrank=true];',
    'node [shape=plain, fontname="' + CJK_FONT + '", fontsize=12, fontcolor="' + INK + '"];', 'edge [fontname="' + CJK_FONT + '", fontsize=11, color="' + LINE + '", fontcolor="' + INK + '", arrowsize=0.8];'];
  const node = (c, external) => {
    const lines = [...(c.stereotype ? [{ text: '«' + c.stereotype + '»', size: 11, bg: LIGHT }] : []), { text: c.name, size: 15, bold: true, bg: external ? '#FFFFFF' : LIGHT },
      ...(pathText(c) ? wrap(pathText(c), 11, 210, '、').map(text => ({ text, size: 11, color: MUTED })) : []),
      ...(c.role ? wrap(c.role, 13, 210, '[，；]').map(text => ({ text, size: 13 })) : [])];
    return c.id + ' [label=' + boxed(lines, external ? ' STYLE="dashed"' : '') + '];';
  };
  for (const id of spec.ungrouped) dot.push(node(byId(id), false));
  for (const group of spec.groups) {
    dot.push('subgraph cluster_' + group.id + ' { label=' + label([group.label], { size: 15, bold: true, color: NAVY }, 3) + '; style="' + (group.external ? 'rounded,dashed' : 'rounded') + '"; color="' + LINE + '";' + (group.external ? ' labelloc=b;' : ''));
    for (const id of group.components) dot.push(node(byId(id), !!group.external));
    if (group.external) dot.push('{ rank=same; ' + group.components.join('; ') + '; }');
    dot.push('}');
  }
  const down = (from, to) => rank.get(from) <= rank.get(to);
  for (const i of spec.interfaces) {
    const provider = i.providedBy.component;
    // The interface is a circle with its name beside it; edges attach to the circle cell.
    dot.push(i.id + ' [label=<<TABLE BORDER="0" CELLBORDER="0" CELLSPACING="0" CELLPADDING="0"><TR><TD PORT="c" ALIGN="LEFT" WIDTH="18" HEIGHT="20" FIXEDSIZE="TRUE"><FONT POINT-SIZE="18">○</FONT></TD><TD>'
      + NESTED + htmlRows(interfaceLines(i).map(text => ({ text, size: 12, align: 'left' }))) + '</TABLE></TD></TR></TABLE>>];');
    for (const u of i.usedBy) {
      const text = 'label=' + edgeLabel([u.label], { size: 12 });
      if (down(u.component, provider)) {
        dot.push(u.component + ' -> ' + i.id + ':c [style=dashed, arrowhead=vee, ' + text + '];', i.id + ':c -> ' + provider + ' [dir=none, color="' + INK + '"];');
      } else {
        dot.push(provider + ' -> ' + i.id + ':c [dir=none, color="' + INK + '"];', i.id + ':c -> ' + u.component + ' [dir=back, style=dashed, arrowtail=vee, ' + text + '];');
      }
    }
  }
  for (const d of spec.dependencies) {
    const text = 'label=' + edgeLabel([d.label], { size: 12 });
    dot.push(down(d.from, d.to) ? d.from + ' -> ' + d.to + ' [style=dashed, arrowhead=vee, ' + text + '];' : d.to + ' -> ' + d.from + ' [dir=back, style=dashed, arrowtail=vee, ' + text + '];');
  }
  for (const n of spec.notes) {
    const id = n.of + '_note';
    dot.push(id + ' [shape=note, style=filled, fillcolor="' + NOTE + '", color="' + LINE + '", margin="0.1,0.06", label=' + label(n.lines, { size: 12, align: 'left' }) + '];',
      '{ rank=same; ' + n.of + '; ' + id + '; }', (n.side === 'left' ? id + ' -> ' + n.of : n.of + ' -> ' + id) + ' [style=dotted, arrowhead=none];');
  }
  dot.push('}');
  const size = await renderPng(dot.join('\n'), structureOut, 'Component Diagram', labelsOnLines);
  console.log('Component Diagram:', spec.components.length, 'components,', spec.interfaces.length, 'interfaces,', spec.interfaces.reduce((sum, i) => sum + 1 + i.usedBy.length, 0) + spec.dependencies.length, 'relations,', size);
}

// ---------------------------------------------------------------- System overview
// A simplified view of the component diagram, embedded in the technical document. Every layer, node
// and flow in framework.json points at components and relations of component.json, which the block
// above has just checked against the product source, or names a source file and a string of its own.
{
  const spec = await readJson(path.join(source, 'framework.json'));
  const parts = await readJson(path.join(source, spec.componentSpec));
  if (parts.sourceRoot !== spec.sourceRoot) fail('framework.json and ' + spec.componentSpec + ' use different source roots');
  const root = path.join(repo, spec.sourceRoot), check = sourceChecker(root);
  const same = (a, b) => a.slice().sort().join() === b.slice().sort().join();
  const part = id => parts.components.find(c => c.id === id) ?? fail('framework.json names an unknown component: ' + id);
  const node = id => spec.nodes.find(n => n.id === id) ?? fail('framework.json names an unknown node: ' + id);
  const ids = spec.nodes.map(n => n.id);
  if (!same(ids, [...spec.layers.flatMap(l => l.nodes), ...spec.outside])) fail('framework.json layers and nodes differ');
  for (const n of spec.nodes) if (n.at?.length !== 2 || !n.name?.length) fail('Overview node needs a name and a position: ' + n.id);
  // A layer must be a group of the component diagram, and its nodes must cover exactly that group.
  for (const layer of spec.layers) {
    const group = parts.groups.find(g => g.id === layer.group) ?? fail('framework.json names an unknown group: ' + layer.group);
    if (group.label !== layer.label) fail('Layer ' + layer.group + ' is called ' + group.label + ' in the component diagram');
    const covered = layer.nodes.flatMap(id => node(id).components ?? fail('Layer node without components: ' + id));
    covered.forEach(part);
    if (!same(covered, group.components)) fail('Layer ' + layer.group + ' does not cover the same components as the component diagram');
  }
  for (const id of spec.outside) {
    const n = node(id);
    if (n.components || !n.evidence?.length) fail('A node outside the layers needs evidence of its own: ' + id);
    for (const e of n.evidence) await check.require(e.file, e.text, 'Overview node ' + id);
  }
  // A flow runs the same way as the relation it is based on: from the user of an interface to its
  // provider, and from the depending component to the one it depends on.
  const owns = (n, componentId) => (n.components ?? []).includes(componentId);
  for (const f of spec.flows) {
    const from = node(f.from), to = node(f.to), what = 'Flow ' + f.from + ' -> ' + f.to;
    if (!f.basis?.length) fail(what + ' has no basis');
    for (const b of f.basis) {
      if (b.interface) {
        const i = parts.interfaces.find(x => x.id === b.interface) ?? fail(what + ': unknown interface ' + b.interface);
        if (!i.usedBy.some(u => u.component === b.usedBy)) fail(what + ': ' + b.usedBy + ' does not use ' + b.interface + ' in the component diagram');
        if (!owns(from, b.usedBy) || !owns(to, i.providedBy.component)) fail(what + ' runs against the use of ' + b.interface + ' in the component diagram');
      } else if (b.dependency) {
        if (!parts.dependencies.some(d => d.from === b.dependency.from && d.to === b.dependency.to)) fail(what + ': dependency ' + b.dependency.from + ' -> ' + b.dependency.to + ' is not in the component diagram');
        if (!owns(from, b.dependency.from) || !owns(to, b.dependency.to)) fail(what + ' runs against the dependency ' + b.dependency.from + ' -> ' + b.dependency.to);
      } else if (b.component) {
        part(b.component);
        if (!owns(from, b.component) && !owns(to, b.component)) fail(what + ': component ' + b.component + ' is at neither end');
      } else await check.require(b.file, b.text, what);
    }
  }

  // Picture: the document shows it 16.6 cm wide, so the layout is fixed instead of left to dot, which
  // spreads this graph too wide or too tall for the text to stay readable. Every node carries its centre
  // in points (x to the right, y downward); nodes with the same y form a row and share one height. The
  // frames of the layers and of the host are computed from the nodes they hold. neato only draws.
  const PAD = 12, HEAD = 26, FRAME_FILL = '#FBFCFD';
  const boxes = new Map(spec.nodes.map(n => {
    const lines = [...n.name.map(text => ({ text, size: 15, bold: true })),
      ...(n.showPath ? [{ text: spec.pathPrefix + n.components.flatMap(id => part(id).paths).join('、'), size: 12, color: MUTED }] : []),
      ...(n.detail ?? []).map(text => ({ text, size: 13 }))];
    return [n.id, { lines, x: n.at[0], y: n.at[1], w: Math.max(...lines.map(l => textWidth(l.text, l.size, l.bold))) + 2 * PAD, h: lines.reduce((sum, l) => sum + l.size * 1.3 + 4, 10) }];
  }));
  const rowHeight = new Map();
  for (const b of boxes.values()) rowHeight.set(b.y, Math.max(rowHeight.get(b.y) ?? 0, b.h));
  for (const b of boxes.values()) b.h = rowHeight.get(b.y);
  const edgesOf = b => ({ l: b.x - b.w / 2, t: b.y - b.h / 2, r: b.x + b.w / 2, b: b.y + b.h / 2 });
  const around = (rects, title) => {
    const l = Math.min(...rects.map(r => r.l)) - PAD;
    return { l, t: Math.min(...rects.map(r => r.t)) - HEAD, r: Math.max(l + textWidth(title, 14, true) + 2 * PAD, ...rects.map(r => r.r + PAD)), b: Math.max(...rects.map(r => r.b)) + PAD, title };
  };
  const frames = spec.layers.map(layer => ({ id: layer.group, ...around(layer.nodes.map(id => edgesOf(boxes.get(id))), layer.label) }));
  const host = part(spec.host.component);
  const hostFrame = around(frames, spec.host.label + '（' + host.paths.map(file => file.replace(/\.cs$/, '')).join('、') + '）');
  for (const id of spec.outside) {
    const o = edgesOf(boxes.get(id));
    if (o.l < hostFrame.r && o.r > hostFrame.l && o.t < hostFrame.b && o.b > hostFrame.t) fail('Overview node ' + id + ' is outside the layers but lies inside the host frame');
  }
  const inch = value => (value / 72).toFixed(3);
  const at = (x, y) => 'pos="' + inch(x) + ',' + inch(-y) + '!"';
  const sized = (w, h) => 'fixedsize=true, width=' + inch(w) + ', height=' + inch(h);
  const frame = (id, f, fill, color) => [
    id + ' [shape=box, style="rounded,filled", fillcolor="' + fill + '", color="' + color + '", label="", ' + sized(f.r - f.l, f.b - f.t) + ', ' + at((f.l + f.r) / 2, (f.t + f.b) / 2) + '];',
    id + '_title [shape=plain, label=' + label([f.title], { size: 14, bold: true, color: NAVY }, 0) + ', ' + at(f.l + PAD + textWidth(f.title, 14, true) / 2, f.t + HEAD / 2 + 1) + '];'];
  // Later nodes are drawn over earlier ones: the host frame first, then the layers, then the nodes.
  const dot = ['digraph SystemOverview {', 'graph [splines=line, outputorder=nodesfirst, pad=0.12];',
    'node [fontname="' + CJK_FONT + '", fontsize=13, fontcolor="' + INK + '"];', 'edge [color="' + INK + '", arrowhead=vee, arrowsize=0.8];',
    ...frame('host', hostFrame, '#FFFFFF', NAVY), ...frames.flatMap(f => frame('layer_' + f.id, f, FRAME_FILL, LINE))];
  for (const [id, b] of boxes) {
    const outside = spec.outside.includes(id);
    dot.push(id + ' [shape=box, style="' + (outside ? 'rounded,filled' : 'filled') + '", fillcolor="' + (outside ? '#FFFFFF' : LIGHT) + '", color="' + LINE + '", ' + sized(b.w, b.h) + ', ' + at(b.x, b.y)
      + ', label=<<TABLE BORDER="0" CELLBORDER="0" CELLSPACING="0" CELLPADDING="2">' + htmlRows(b.lines) + '</TABLE>>];');
  }
  // A flow leaves and enters at the middle of a side. Its label is a node of its own, placed where the
  // line is clear of both end nodes and of the frames around only one of them: above a horizontal
  // flow, otherwise beside the line, far enough for the corners of the label to clear it.
  const allFrames = [...frames.map((f, index) => ({ ...f, members: spec.layers[index].nodes })), { ...hostFrame, members: spec.layers.flatMap(l => l.nodes) }];
  spec.flows.forEach((f, index) => {
    const a = boxes.get(f.from), b = boxes.get(f.to), flat = a.y === b.y;
    const sides = flat ? (a.x < b.x ? ['e', 'w'] : ['w', 'e']) : (a.y < b.y ? ['s', 'n'] : ['n', 's']);
    const point = (box, side) => ({ x: box.x + (side === 'e' ? box.w / 2 : side === 'w' ? -box.w / 2 : 0), y: box.y + (side === 's' ? box.h / 2 : side === 'n' ? -box.h / 2 : 0) });
    const p = point(a, sides[0]), q = point(b, sides[1]);
    const only = (own, other) => [edgesOf(boxes.get(own)), ...allFrames.filter(frame => frame.members.includes(own) && !frame.members.includes(other))];
    const first = (flat ? a.x < b.x : a.y < b.y) ? [f.from, f.to] : [f.to, f.from];
    const clearFrom = Math.max(...only(first[0], first[1]).map(r => flat ? r.r : r.b)), clearTo = Math.min(...only(first[1], first[0]).map(r => flat ? r.l : r.t));
    const w = Math.max(...f.label.map(text => textWidth(text, 13))), h = f.label.length * 19, middle = (clearFrom + clearTo) / 2;
    if (clearTo - clearFrom < (flat ? w : h) + 4) fail('No room for the label of flow ' + f.from + ' -> ' + f.to);
    const slope = flat ? 0 : (q.x - p.x) / (q.y - p.y);
    const x = flat ? middle : p.x + slope * (middle - p.y) + (f.labelSide === 'left' ? -1 : 1) * (w / 2 + 6 + h / 2 * Math.abs(slope));
    const y = flat ? p.y - h / 2 - 4 : middle;
    dot.push(f.from + ':' + sides[0] + ' -> ' + f.to + ':' + sides[1] + ';', 'flow' + index + ' [shape=plain, label=' + label(f.label, { size: 13 }, 1) + ', ' + at(x, y) + '];');
  });
  dot.push('}');
  const size = await renderPng(dot.join('\n'), source, 'framework', svg => svg, 110, 'neato', 2000);
  console.log('System overview (' + spec.title + '):', spec.nodes.length, 'nodes,', spec.layers.length, 'layers,', spec.flows.length, 'flows,', size);
}

// ---------------------------------------------------------------- Activity diagram
{
  const spec = await readJson(path.join(source, 'activity.json'));
  const check = sourceChecker(path.join(repo, spec.sourceRoot));
  const laneLabel = id => (spec.lanes.find(lane => lane.id === id) ?? fail('Unknown lane: ' + id)).label;
  const all = [];
  async function verify(flow) {
    for (const step of flow) {
      if (all.some(known => known.id === step.id)) fail('Duplicate activity node: ' + step.id);
      all.push(step); laneLabel(step.lane);
      if (!step.evidence?.length) fail('Activity node without evidence: ' + step.id);
      for (const e of step.evidence) await check.require(e.file, e.text, 'Activity node ' + step.id);
      if (step.type === 'decision') {
        if (step.branches.length !== 2) fail('Decision needs two branches: ' + step.id);
        for (const branch of step.branches) await verify(branch.flow);
      } else if (step.type !== 'action') fail('Unknown activity node type: ' + step.type);
    }
  }
  await verify(spec.flow);

  const plant = [...plantHeader(spec)];
  let lane = null;
  const enter = id => { if (lane !== id) { plant.push('|' + laneLabel(id) + '|'); lane = id; } };
  function write(flow, indent) {
    for (const step of flow) {
      enter(step.lane);
      if (step.type === 'action') {
        plant.push(indent + ':' + step.text.join('\\n') + ';');
        if (step.note) plant.push(indent + 'note right', ...step.note.map(line => indent + '  ' + line), indent + 'end note');
      } else {
        // A line break in the picture stands for a space when it separates Latin text from Chinese text.
        const condition = step.text.reduce((joined, line) => joined + (/[A-Za-z0-9]$/.test(joined) !== /^[A-Za-z0-9]/.test(line) && joined ? ' ' : '') + line, '');
        plant.push(indent + 'if (' + condition + ') then (' + step.branches[0].guard + ')');
        write(step.branches[0].flow, indent + '  ');
        plant.push(indent + 'else (' + step.branches[1].guard + ')');
        write(step.branches[1].flow, indent + '  ');
        plant.push(indent + 'endif');
      }
    }
  }
  enter(spec.flow[0].lane);
  plant.push('start');
  write(spec.flow, '');
  enter(spec.endLane);
  plant.push('stop', '@enduml');
  await writeText(behaviorOut, 'Activity Diagram', plant);

  // Picture: one cluster per lane. Branches that meet again go through a merge node, because an
  // action with two incoming flows would mean "wait for both" in UML.
  const laneNodes = new Map(spec.lanes.map(l => [l.id, []])), edges = [], extras = [];
  let merges = 0;
  const link = (pending, target) => { for (const p of pending) edges.push(p.from + ' -> ' + target + (p.guard ? ' [label=' + label([p.guard], { size: 13, bold: true }, 2) + ']' : '') + ';'); };
  const mergeInto = (pending, laneId) => {
    if (pending.length < 2) return pending;
    const id = 'merge' + (++merges);
    laneNodes.get(laneId).push(id + ' [shape=diamond, fixedsize=true, width=0.36, height=0.36, label="", color="' + INK + '"];');
    link(pending, id);
    return [{ from: id }];
  };
  function draw(flow, pending) {
    for (const step of flow) {
      pending = mergeInto(pending, step.lane);
      if (step.type === 'action') {
        laneNodes.get(step.lane).push(step.id + ' [shape=box, style="rounded,filled", fillcolor="' + LIGHT + '", color="' + LINE + '", margin="0.1,0.07", label=' + label(step.text, { size: 14 }) + '];');
        if (step.note) {
          laneNodes.get(step.lane).push(step.id + '_note [shape=note, style=filled, fillcolor="' + NOTE + '", color="' + LINE + '", margin="0.1,0.06", label=' + label(step.note, { size: 11.5, align: 'left' }) + '];');
          extras.push('{ rank=same; ' + step.id + '; ' + step.id + '_note; }', step.id + ' -> ' + step.id + '_note [style=dotted, arrowhead=none];');
        }
        link(pending, step.id);
        pending = [{ from: step.id }];
      } else {
        laneNodes.get(step.lane).push(step.id + ' [shape=diamond, style=filled, fillcolor="#FFFFFF", color="' + INK + '", margin="0.01,0.01", label=' + label(step.text, { size: 13 }) + '];');
        link(pending, step.id);
        if (step.branches.every(branch => branch.flow.length)) extras.push('{ rank=same; ' + step.branches.map(branch => branch.flow[0].id).join('; ') + '; }');
        pending = step.branches.flatMap(branch => draw(branch.flow, [{ from: step.id, guard: branch.guard }]));
      }
    }
    return pending;
  }
  const first = spec.flow[0].lane;
  laneNodes.get(first).push('start [shape=circle, style=filled, fillcolor="' + INK + '", color="' + INK + '", fixedsize=true, width=0.26, label=""];');
  const last = mergeInto(draw(spec.flow, [{ from: 'start' }]), spec.endLane);
  laneNodes.get(spec.endLane).push('finish [shape=doublecircle, style=filled, fillcolor="' + INK + '", color="' + INK + '", fixedsize=true, width=0.22, label=""];');
  link(last, 'finish');
  const dot = ['digraph ActivityDiagram {', 'graph [rankdir=TB, nodesep=0.3, ranksep=0.2, fontname="' + CJK_FONT + '", labelloc=t, label=' + label([spec.title], { size: 18, color: NAVY }, 4) + ', pad=0.3, newrank=true];',
    'node [fontname="' + CJK_FONT + '", fontsize=13, fontcolor="' + INK + '"];', 'edge [fontname="' + CJK_FONT + '", fontsize=12, color="' + INK + '", fontcolor="' + INK + '", arrowhead=vee, arrowsize=0.8];'];
  // An invisible node level with the start makes every lane begin at the same height.
  for (const l of spec.lanes) dot.push('subgraph cluster_' + l.id + ' { label=' + label([l.label], { size: 15, bold: true, color: NAVY }, 3) + '; style="filled"; fillcolor="#FBFCFD"; color="' + LINE + '";',
    ...(l.id === first ? [] : ['top_' + l.id + ' [shape=point, style=invis, width=0.26, label=""];']), ...laneNodes.get(l.id), '}');
  dot.push('{ rank=same; ' + spec.lanes.map(l => l.id === first ? 'start' : 'top_' + l.id).join('; ') + '; }', ...extras, ...edges, '}');
  // The lanes end where their last node is. Stretch every lane frame down to the lowest one; an
  // invisible node level with the end would make Graphviz swap the lanes.
  const equalLanes = svg => {
    const frame = /(<g id="clust\d+" class="cluster">[\s\S]*?<polygon[^>]* points=")([^"]+)(")/g;
    const bottoms = [...svg.matchAll(frame)].map(match => Math.max(...match[2].trim().split(/\s+/).map(point => Number(point.split(',')[1]))));
    if (bottoms.length !== spec.lanes.length) fail('Lane frames not found in the activity picture');
    const lowest = Math.max(...bottoms);
    let index = 0;
    return svg.replace(frame, (whole, before, points, after) => {
      const own = bottoms[index++];
      return before + points.trim().split(/\s+/).map(point => { const [x, y] = point.split(','); return x + ',' + (Number(y) === own ? lowest : y); }).join(' ') + after;
    });
  };
  const size = await renderPng(dot.join('\n'), behaviorOut, 'Activity Diagram', equalLanes);
  const actions = all.filter(step => step.type === 'action').length, decisions = all.length - actions;
  console.log('Activity Diagram:', actions, 'actions,', decisions, 'decisions,', merges, 'merge nodes,', edges.length, 'flows,', size);
}
