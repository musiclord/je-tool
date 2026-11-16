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
// All lines run horizontally and vertically only. Every picture has a fixed layout: the place of every
// box is in the JSON source ("layout", or "at" in framework.json), the lines are routed here or follow
// the corner points the source gives, and the build stops when a box has no place, boxes overlap, a line
// runs through a box, a label, a title or the frame of a group it does not belong to, or the lines cross
// more often than the JSON source allows. Graphviz only draws; each picture is read back afterwards, and
// a line that Graphviz drew slanted or curved stops the build as well.
// Needs Node.js with @viz-js/viz and sharp (see README.md).
// Usage: node Build-Diagrams.mjs <repo root> [--output <folder>] [--source <folder>] [--debug <folder>] [--only <names>]
//   --only    builds some of the pictures only: names from erd, class, component, overview, activity,
//             separated by commas. The overview reads component.json, so both must be up to date.
//   --output  every output goes to this folder: ERD and the "Structure Diagrams" and "Behavior diagrams"
//             folders as in docs/SAT/Diagrams, and framework.png next to them. Without it the diagrams go
//             to docs/SAT/Diagrams and framework.png to the folder of the JSON sources.
//   --source  folder of the JSON sources, instead of tools/harness/sat-documents/source/diagrams.
//   --debug   keeps the DOT text and the SVG of every picture and the computed layout of the fixed
//             pictures in this folder, and the picture of a layout that was rejected.
import fs from 'node:fs/promises';
import path from 'node:path';
import { instance } from '@viz-js/viz';
import sharp from 'sharp';

const fail = message => { throw new Error(message); };
const args = process.argv.slice(2);
function folderOption(name) {
  const at = args.indexOf(name);
  if (at < 0) return null;
  const value = args[at + 1];
  if (!value || value.startsWith('--')) fail(name + ' needs a folder');
  args.splice(at, 2);
  return path.resolve(value);
}
const outputOption = folderOption('--output'), sourceOption = folderOption('--source'), debugOut = folderOption('--debug');
const DIAGRAMS = ['erd', 'class', 'component', 'overview', 'activity'];
let only = DIAGRAMS;
if (args.includes('--only')) {
  const at = args.indexOf('--only');
  only = (args[at + 1] ?? '').split(',');
  if (!only.every(name => DIAGRAMS.includes(name))) fail('--only takes names from: ' + DIAGRAMS.join(', '));
  args.splice(at, 2);
}
if (args.length > 1 || args.some(arg => arg.startsWith('--'))) fail('Usage: node Build-Diagrams.mjs <repo root> [--output <folder>] [--source <folder>] [--debug <folder>] [--only <names>]');
const repo = path.resolve(args[0] || process.cwd());
const source = sourceOption ?? path.join(repo, 'tools/harness/sat-documents/source/diagrams');
const out = outputOption ?? path.join(repo, 'docs/SAT/Diagrams');
const frameworkOut = outputOption ?? source;
const structureOut = path.join(out, 'Structure Diagrams'), behaviorOut = path.join(out, 'Behavior diagrams');
for (const dir of [out, structureOut, behaviorOut, frameworkOut, ...(debugOut ? [debugOut] : [])]) await fs.mkdir(dir, { recursive: true });

const readJson = async file => JSON.parse(await fs.readFile(file, 'utf8'));
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
const keepDebug = async (name, text) => { if (debugOut) await fs.writeFile(path.join(debugOut, name), text, 'utf8'); };
async function renderPng(dot, dir, name, adjustSvg = svg => svg, density = 110, engine = 'dot', pixelWidth = 0, fileName = name) {
  assertDeliverable(dot, name + '.png');
  const svg = adjustSvg(viz.renderString(dot, { format: 'svg', engine }));
  await keepDebug(fileName + '.dot', dot);
  await keepDebug(fileName + '.svg', svg);
  if (pixelWidth) density = 72 * Math.sqrt(pixelWidth / Number((/<svg width="([\d.]+)pt"/.exec(svg) ?? fail('SVG width not found in ' + name))[1]));
  const file = path.join(dir, fileName + '.png');
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
// The name of a relation has a white background and is drawn after every line, on a straight part of
// its own line: the name interrupts the line it belongs to and no other line runs through it.
const edgeLabel = (texts, style = {}) => '<<TABLE BORDER="0" CELLBORDER="0" CELLSPACING="0" CELLPADDING="3" BGCOLOR="#FFFFFF">' + htmlRows(texts.map(text => ({ text, ...style }))) + '</TABLE>>';
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


// ---------------------------------------------------------------- Checks on the lines of a picture
// A segment is { a: [x, y], b: [x, y], owner }, horizontal or vertical. Two segments of different owners
// cross when one is horizontal, the other vertical, and they meet inside both.
function crossingsOf(segments) {
  const lo = (s, k) => Math.min(s.a[k], s.b[k]), hi = (s, k) => Math.max(s.a[k], s.b[k]);
  let count = 0;
  for (let x = 0; x < segments.length; x++) for (let y = x + 1; y < segments.length; y++) {
    const s = segments[x], t = segments[y], sFlat = s.a[1] === s.b[1], tFlat = t.a[1] === t.b[1];
    if (s.owner === t.owner || sFlat === tFlat) continue;
    const [h, v] = sFlat ? [s, t] : [t, s];
    if (v.a[0] > lo(h, 0) && v.a[0] < hi(h, 0) && h.a[1] > lo(v, 1) && h.a[1] < hi(v, 1)) count++;
  }
  return count;
}
// The lines Graphviz drew, read back from its SVG as segments. Graphviz writes a line as a chain of
// points; on a line that runs horizontally and vertically only, every two neighbouring points share their x or their y. A line that does
// not stops the build. ownerOf turns the title of a line ("a->b") into the relation it belongs to.
function drawnSegments(svg, name, ownerOf = title => title) {
  const segments = [];
  for (const [, title, body] of svg.matchAll(/<g id="edge\d+" class="edge">\s*<title>([^<]*)<\/title>([\s\S]*?)<\/g>/g)) {
    const drawn = /<path[^>]* d="([^"]+)"/.exec(body);
    if (!drawn) continue;
    const n = drawn[1].match(/-?\d+(?:\.\d+)?/g).map(Number), owner = ownerOf(title.replace(/&#45;/g, '-').replace(/&gt;/g, '>'));
    for (let i = 0; i + 3 < n.length; i += 2) {
      const a = [n[i], n[i + 1]], b = [n[i + 2], n[i + 3]], dx = Math.abs(a[0] - b[0]), dy = Math.abs(a[1] - b[1]);
      if (dx > 0.05 && dy > 0.05) fail(name + ': the line ' + owner + ' has a slanted or curved piece');
      if (dx <= 0.05 && dy <= 0.05) continue;
      // Pieces that follow one another in the same direction are one straight piece.
      const flat = dx > 0.05, last = segments.at(-1);
      if (i > 0 && last?.owner === owner && (last.a[1] === last.b[1]) === flat && Math.abs(last.b[0] - a[0]) < 0.05 && Math.abs(last.b[1] - a[1]) < 0.05) last.b = flat ? [b[0], last.a[1]] : [last.a[0], b[1]];
      else segments.push({ a, b: flat ? [b[0], a[1]] : [a[0], b[1]], owner });
    }
  }
  return segments;
}

// ---------------------------------------------------------------- Fixed layout with right-angled lines
// The ERD and the class, component and activity diagrams are not left to dot, which draws long curves
// and slanted lines that cross one another. Every box has a fixed place in the JSON source ("layout.at"),
// in points with x to the right and y downward. The lines are routed here, one after another, on a grid of CELL
// points: they run horizontally and vertically only, leave and enter a box at right angles, keep SPACE
// cells from every line and frame they run beside, and never run through a box, a label, a title or the
// frame of a group that neither of their ends belongs to. A crossing costs as much as a long detour, so a
// line crosses another only when every other way is blocked; a crossing within NEAR_END cells of the end
// of a line costs more again, so that it lies in open space. A label sits on a straight part of its own
// line, where it covers no box and no other line. Graphviz (engine nop2) then draws everything exactly
// where it was computed. check() measures the result again from the geometry and stops the build when
// something is wrong: see the list at check().
// What the JSON source may say under "layout":
//   at            the centre of every box: { "<id>": [x, y] }; [x, y, "<anchor>"] places a side or a
//                 corner of the box there instead (see placeBoxes)
//   maxCrossings  how many crossings the picture may have; more stop the build
//   lines         for the line "<key>": fromSides and toSides (the sides of the two boxes it may use:
//                 "top", "right", "bottom", "left"), fromPrefer and toPrefer (where along the side, 0 to 1
//                 from the left or the top; 0.5 when not given), fromExact and toExact (use only that
//                 place), labelAt (where along the line its label sits, 0 to 1), via (the corner points
//                 of the line, [[x, y], ...]: the line is not routed but drawn through them, and leaves
//                 and enters its two boxes level with the first and the last point), text (the label
//                 broken into lines; the words must be those of the source)
//   order         the keys of the lines in the order they are routed; a line routed earlier gets the
//                 shorter way, lines not listed follow in the order of the JSON source. Lines with "via"
//                 are laid down before all others
//   frames        for the frame of the group "<id>": titleAlign ("left", "centre", "right"), span and
//                 spanY (the frame reaches at least from span[0] to span[1] and from spanY[0] to spanY[1])
//   titleGap      the room between the title of the picture and the topmost box or frame
//   alignFrames   true gives every frame the same top and bottom edge
// The key of a line is written next to each diagram below. A problem report names the line by its key.
const CELL = 4, CLEAR = 10, STUB = 22, SPACE = 3, PORT_GAP = 16, NEAR_END = 8, ALIGNED = 12;
const COST = { bend: 24, cross: 3000, nearEnd: 400, frame: 8, port: 0.03 };
const DIRS = [[1, 0], [0, 1], [-1, 0], [0, -1]];            // right, down, left, up
const OUTWARD = { right: 0, bottom: 1, left: 2, top: 3 };
const OPPOSITE = { top: 'bottom', bottom: 'top', left: 'right', right: 'left' };
const SIDES = ['top', 'right', 'bottom', 'left'];
const rect = (x, y, w, h) => ({ l: x - w / 2, r: x + w / 2, t: y - h / 2, b: y + h / 2, x, y, w, h });
const meets = (a, b, m = 0) => a.l < b.r + m && a.r > b.l - m && a.t < b.b + m && a.b > b.t - m;
const ARROW = { vee: 10, onormal: 10, normal: 10 };            // arrowhead length at arrowsize 1, points
// Two line ends look like one line through a box when they have the same stroke and at most one arrowhead.
const sameLook = (a, b) => a.dashed === b.dashed && !(a.arrow && b.arrow);

// Sizes of the boxes as Graphviz draws them, in points.
function measure(defaults, statements) {
  const result = viz.render(['digraph M {', ...defaults, ...statements, '}'].join('\n'), { format: 'json0', engine: 'dot' });
  if (result.status !== 'success') fail('Graphviz could not measure the boxes: ' + JSON.stringify(result.errors));
  return new Map((JSON.parse(result.output).objects ?? []).filter(o => o.width !== undefined).map(o => [o.name, { w: Number(o.width) * 72, h: Number(o.height) * 72 }]));
}

class Heap {
  constructor() { this.keys = []; this.values = []; }
  get size() { return this.keys.length; }
  push(key, value) {
    const k = this.keys, v = this.values; let i = k.length; k.push(key); v.push(value);
    while (i > 0) { const p = (i - 1) >> 1; if (k[p] <= key) break; k[i] = k[p]; v[i] = v[p]; i = p; }
    k[i] = key; v[i] = value;
  }
  pop() {
    const k = this.keys, v = this.values, top = [k[0], v[0]], key = k.pop(), value = v.pop();
    if (k.length) {
      let i = 0;
      for (;;) { let c = 2 * i + 1; if (c >= k.length) break; if (c + 1 < k.length && k[c + 1] < k[c]) c++; if (k[c] >= key) break; k[i] = k[c]; v[i] = v[c]; i = c; }
      k[i] = key; v[i] = value;
    }
    return top;
  }
}

// area: the rectangle the lines may use. Boxes, titles and labels are obstacles; frames are crossed at
// right angles only.
function createRouter(area) {
  const W = Math.ceil((area.r - area.l) / CELL) + 1, H = Math.ceil((area.b - area.t) / CELL) + 1, N = W * H;
  const X = i => area.l + i * CELL, Y = j => area.t + j * CELL;
  const blocked = new Uint8Array(N), lineH = new Int32Array(N), lineV = new Int32Array(N), corner = new Uint8Array(N), nearEnd = new Uint8Array(N);
  const nearFrameH = new Uint8Array(N), nearFrameV = new Uint8Array(N), onFrameH = new Uint8Array(N), onFrameV = new Uint8Array(N);
  const spanX = (lo, hi) => [Math.max(0, Math.ceil((lo - area.l) / CELL)), Math.min(W - 1, Math.floor((hi - area.l) / CELL))];
  const spanY = (lo, hi) => [Math.max(0, Math.ceil((lo - area.t) / CELL)), Math.min(H - 1, Math.floor((hi - area.t) / CELL))];
  const each = (r, m, f) => { const [i0, i1] = spanX(r.l - m, r.r + m), [j0, j1] = spanY(r.t - m, r.b + m); for (let j = j0; j <= j1; j++) for (let i = i0; i <= i1; i++) f(j * W + i, i, j); };
  const block = (r, m = CLEAR) => each(r, m, c => { blocked[c] = 1; });
  // The cells of the given rectangles, as a mask for one search: a line may not use them.
  const mask = rects => { const cells = new Uint8Array(N); for (const r of rects) each(r, 0, c => { cells[c] = 1; }); return cells; };
  // A frame: lines cross its border at right angles and keep SPACE cells from it when they run beside it.
  function frame(r) {
    const near = SPACE * CELL;
    for (const y of [r.t, r.b]) {
      each({ l: r.l, r: r.r, t: y, b: y }, near, c => { nearFrameH[c] = 1; });
      const j = Math.round((y - area.t) / CELL), [i0, i1] = spanX(r.l, r.r);
      for (let i = i0; i <= i1; i++) onFrameH[j * W + i] = 1;
    }
    for (const x of [r.l, r.r]) {
      each({ l: x, r: x, t: r.t, b: r.b }, near, c => { nearFrameV[c] = 1; });
      const i = Math.round((x - area.l) / CELL), [j0, j1] = spanY(r.t, r.b);
      for (let j = j0; j <= j1; j++) onFrameV[j * W + i] = 1;
    }
  }
  // Cells where a line can leave or enter a box: on one of the given sides, at a grid position that keeps
  // PORT_GAP from the lines already there, STUB away from the box. A point item (a circle or a diamond)
  // has one port per side, at its centre. With look given (see sameLook), a place straight across the box
  // from a line end of the same look is left out, so that two lines never read as one line through the box.
  function ports(item, sides, used, prefer = 0.5, stubLength = STUB, look = null) {
    const found = [];
    for (const side of sides) {
      const d = OUTWARD[side], flat = side === 'top' || side === 'bottom';
      const lo = flat ? item.l : item.t, hi = flat ? item.r : item.b, margin = Math.min(14, (hi - lo) / 2 - 1);
      const wanted = lo + (hi - lo) * prefer;
      const taken = used.get(item.id + ':' + side) ?? [];
      const across = look ? (used.get(item.id + ':' + OPPOSITE[side]) ?? []).filter(u => sameLook(u, look)) : [];
      const alongs = item.point ? [Math.round(((flat ? item.x - area.l : item.y - area.t)) / CELL)]
        : (([a0, a1]) => Array.from({ length: Math.max(0, a1 - a0 + 1) }, (_, k) => a0 + k))(flat ? spanX(lo + margin, hi - margin) : spanY(lo + margin, hi - margin));
      const out = side === 'top' ? item.t - stubLength : side === 'bottom' ? item.b + stubLength : side === 'left' ? item.l - stubLength : item.r + stubLength;
      const e = flat ? (side === 'top' ? Math.floor((out - area.t) / CELL) : Math.ceil((out - area.t) / CELL))
        : (side === 'left' ? Math.floor((out - area.l) / CELL) : Math.ceil((out - area.l) / CELL));
      for (const a of alongs) {
        const coord = flat ? X(a) : Y(a);
        if (taken.some(u => Math.abs(u.coord - coord) < PORT_GAP) || across.some(u => Math.abs(u.coord - coord) < ALIGNED)) continue;
        const i = flat ? a : e, j = flat ? e : a;
        if (i < 0 || j < 0 || i >= W || j >= H) continue;
        // The stub from the side to the port cell: free of lines, and free of obstacles outside the
        // box's own clearance.
        const stub = [];
        let ok = true;
        for (let s = 0; ok; s++) {
          const si = i - DIRS[d][0] * s, sj = j - DIRS[d][1] * s, c = sj * W + si, x = X(si), y = Y(sj);
          const inside = flat ? (side === 'top' ? y > item.t : y < item.b) : (side === 'left' ? x > item.l : x < item.r);
          if (inside) break;
          const own = flat ? Math.abs(y - (side === 'top' ? item.t : item.b)) <= CLEAR + 0.01 : Math.abs(x - (side === 'left' ? item.l : item.r)) <= CLEAR + 0.01;
          if (lineH[c] || lineV[c] || (!own && blocked[c])) ok = false;
          stub.push(c);
        }
        if (ok) found.push({ c: j * W + i, d, side, coord, stub, cost: COST.port * Math.abs(coord - wanted) });
      }
    }
    return found;
  }
  function search(starts, goals, closed) {
    let gi0 = Infinity, gi1 = -Infinity, gj0 = Infinity, gj1 = -Infinity;
    for (const g of goals) { const i = g.c % W, j = (g.c - i) / W; gi0 = Math.min(gi0, i); gi1 = Math.max(gi1, i); gj0 = Math.min(gj0, j); gj1 = Math.max(gj1, j); }
    const goalAt = new Map(goals.map(g => [g.c * 4 + ((g.d + 2) & 3), g]));
    const h = (i, j) => Math.max(0, gi0 - i, i - gi1) + Math.max(0, gj0 - j, j - gj1);
    const best = new Float64Array(N * 4).fill(Infinity), from = new Int32Array(N * 4).fill(-1), heap = new Heap();
    for (const s of starts) { const k = s.c * 4 + s.d; if (s.cost < best[k]) { best[k] = s.cost; heap.push(s.cost + h(s.c % W, Math.floor(s.c / W)), k); } }
    while (heap.size) {
      const [f, k] = heap.pop(), c = k >> 2, d = k & 3, g = best[k], i = c % W, j = (c - i) / W;
      if (f > g + h(i, j) + 1e-6) continue;
      const goal = goalAt.get(k);
      if (goal) { const keys = []; for (let q = k; q >= 0; q = from[q]) keys.push(q); keys.reverse(); return { keys, start: starts.find(s => s.c * 4 + s.d === keys[0]), goal: { ...goal, cost: g + goal.cost }, cost: g + goal.cost }; }
      for (const nd of [d, (d + 1) & 3, (d + 3) & 3]) {
        if (nd !== d && (lineH[c] || lineV[c])) continue;                 // never turn on another line
        const ni = i + DIRS[nd][0], nj = j + DIRS[nd][1];
        if (ni < 0 || nj < 0 || ni >= W || nj >= H) continue;
        const n = nj * W + ni;
        if (blocked[n] || closed[n]) continue;
        const flat = nd % 2 === 0, same = flat ? lineH : lineV, across = flat ? lineV : lineH;
        if (same[n] || (flat ? nearFrameH : nearFrameV)[n]) continue;    // no running along a line or a frame
        let near = false;
        for (let s = 1; s <= SPACE && !near; s++) near = flat ? (nj - s >= 0 && same[n - s * W] > 0) || (nj + s < H && same[n + s * W] > 0) : (ni - s >= 0 && same[n - s] > 0) || (ni + s < W && same[n + s] > 0);
        if (near) continue;
        let cost = 1 + (nd !== d ? COST.bend : 0);
        if (across[n]) { if (corner[n]) continue; cost += COST.cross + (nearEnd[n] ? COST.nearEnd : 0); }
        if ((flat ? onFrameV : onFrameH)[n]) cost += COST.frame;
        const nk = n * 4 + nd, ng = g + cost;
        if (ng < best[nk]) { best[nk] = ng; from[nk] = k; heap.push(ng + h(ni, nj), nk); }
      }
    }
    return null;
  }
  // Marks a routed line, stubs included; the stubs and the cells next to them stay closed to other lines,
  // so that no line crosses another next to its arrowhead.
  function occupy(found, id) {
    const keys = found.keys;
    keys.forEach((k, q) => {
      const c = k >> 2, d = k & 3, next = q + 1 < keys.length ? keys[q + 1] & 3 : d;
      (d % 2 === 0 ? lineH : lineV)[c] = id;
      if (next !== d) { (next % 2 === 0 ? lineH : lineV)[c] = id; corner[c] = 1; }
    });
    for (const [end, dir] of [[found.start, found.start.d], [found.goal, found.goal.d]]) {
      for (const c of end.stub) { (dir % 2 === 0 ? lineH : lineV)[c] = id; blocked[c] = 1; }
    }
    for (const k of [...keys.slice(0, 2), ...keys.slice(-2)]) blocked[k >> 2] = 1;
    for (const k of [...keys.slice(0, NEAR_END), ...keys.slice(-NEAR_END)]) nearEnd[k >> 2] = 1;
  }
  // A straight line between two given points, such as the short line from an interface to its provider.
  function occupyFixed(points, id) {
    for (let q = 1; q < points.length; q++) {
      const [a, b] = [points[q - 1], points[q]], flat = a[1] === b[1];
      const i0 = Math.round((Math.min(a[0], b[0]) - area.l) / CELL), i1 = Math.round((Math.max(a[0], b[0]) - area.l) / CELL);
      const j0 = Math.round((Math.min(a[1], b[1]) - area.t) / CELL), j1 = Math.round((Math.max(a[1], b[1]) - area.t) / CELL);
      for (let j = j0; j <= j1; j++) for (let i = i0; i <= i1; i++) { (flat ? lineH : lineV)[j * W + i] = id; blocked[j * W + i] = 1; }
    }
  }
  // The cells a rectangle covers hold no line other than the given one.
  const clearOfLines = (r, id) => { let ok = true; each(r, 0, c => { if ((lineH[c] && lineH[c] !== id) || (lineV[c] && lineV[c] !== id)) ok = false; }); return ok; };
  // A line whose corner points are given in the JSON source. Other lines may cross it, but not next to its ends.
  function occupyPath(points, id) {
    const cells = [];
    for (let q = 1; q < points.length; q++) {
      const [a, b] = [points[q - 1], points[q]], flat = a[1] === b[1];
      const i0 = Math.round((Math.min(a[0], b[0]) - area.l) / CELL), i1 = Math.round((Math.max(a[0], b[0]) - area.l) / CELL);
      const j0 = Math.round((Math.min(a[1], b[1]) - area.t) / CELL), j1 = Math.round((Math.max(a[1], b[1]) - area.t) / CELL);
      const run = [];
      for (let j = j0; j <= j1; j++) for (let i = i0; i <= i1; i++) { const c = j * W + i; (flat ? lineH : lineV)[c] = id; run.push(c); }
      if ((flat ? a[0] > b[0] : a[1] > b[1])) run.reverse();
      corner[run[0]] = 1; corner[run.at(-1)] = 1;
      cells.push(...run);
    }
    for (const c of [...cells.slice(0, 6), ...cells.slice(-6)]) blocked[c] = 1;
    for (const c of [...cells.slice(0, NEAR_END), ...cells.slice(-NEAR_END)]) nearEnd[c] = 1;
  }
  return { X, Y, W, block, mask, frame, ports, search, occupy, occupyFixed, occupyPath, clearOfLines };
}

// items: Map id -> { id, x, y, w, h, point? }; frames: [{ ...rect, members, title: rect }]; lines: [{ key,
// from, to, attrs, fromSides, toSides, fromPrefer, toPrefer, fromExact, toExact, label: { w, h } | null,
// labelAt, arrowFrom, arrowTo, fixed }]. With fromExact or toExact the line uses only the preferred place
// on the side. A fixed line is a straight line between two given points (an interface on its provider).
// A line keeps out of every frame that holds neither of its ends. straightThrough false keeps two line
// ends of the same look from standing straight across a box from each other.
function routeAll(items, frames, lines, solids, problems, straightThrough) {
  const all = [...items.values(), ...frames];
  const area = { l: Math.min(...all.map(r => r.l)) - 90, r: Math.max(...all.map(r => r.r)) + 90, t: Math.min(...all.map(r => r.t)) - 60, b: Math.max(...all.map(r => r.b)) + 90 };
  area.l = Math.floor(area.l / CELL) * CELL; area.t = Math.floor(area.t / CELL) * CELL;
  const router = createRouter(area), used = new Map();
  for (const item of items.values()) router.block(item, item.point ? 6 : CLEAR);
  for (const s of solids) router.block(s, 6);
  for (const f of frames) { router.frame(f); if (f.title) router.block(f.title, 4); }
  const labelSolids = [...items.values(), ...solids, ...frames.filter(f => f.title).map(f => f.title)];
  const results = new Map();
  const use = (item, side, coord, look) => { const key = item.id + ':' + side; used.set(key, [...(used.get(key) ?? []), { coord, ...look }]); };
  // The lines with given corner points are laid down first, so that no label is put where one of them runs.
  const given = new Map();
  lines.forEach((line, index) => {
    if (!line.via) return;
    const id = index + 1, a = items.get(line.from) ?? fail('Unknown box: ' + line.from), b = items.get(line.to) ?? fail('Unknown box: ' + line.to);
    const dashed = /style=(dashed|dotted)/.test(line.attrs), looks = [{ dashed, arrow: !!line.arrowFrom }, { dashed, arrow: !!line.arrowTo }];
    // The line leaves and enters the two boxes on the sides that face its first and its last corner
    // point, level with them.
    const end = (item, p) => {
      const inX = item.point ? p[0] === item.x : p[0] > item.l && p[0] < item.r, inY = item.point ? p[1] === item.y : p[1] > item.t && p[1] < item.b;
      if (inX === inY) fail('The line ' + line.key + ': the corner point ' + JSON.stringify(p) + ' is not level with one side of ' + item.id);
      return inX ? (p[1] < item.y ? ['top', [p[0], item.t]] : ['bottom', [p[0], item.b]]) : (p[0] < item.x ? ['left', [item.l, p[1]]] : ['right', [item.r, p[1]]]);
    };
    const [fromSide, first] = end(a, line.via[0]), [toSide, last] = end(b, line.via.at(-1)), points = [first, ...line.via, last];
    points.forEach((p, q) => { if (q && p[0] !== points[q - 1][0] && p[1] !== points[q - 1][1]) fail('The line ' + line.key + ': the corner points ' + JSON.stringify(points[q - 1]) + ' and ' + JSON.stringify(p) + ' are not level'); });
    const clean = points.filter((p, q) => q === 0 || q === points.length - 1 || !((points[q - 1][0] === p[0] && p[0] === points[q + 1][0]) || (points[q - 1][1] === p[1] && p[1] === points[q + 1][1])));
    router.occupyPath(clean, id);
    use(a, fromSide, fromSide === 'top' || fromSide === 'bottom' ? first[0] : first[1], looks[0]);
    use(b, toSide, toSide === 'top' || toSide === 'bottom' ? last[0] : last[1], looks[1]);
    given.set(line.key, { line, points: clean, fromSide, toSide, looks, id });
  });
  lines.forEach((line, index) => {
    const id = index + 1;
    if (results.has(line.key)) fail('Two lines have the key ' + line.key);
    const a = items.get(line.from) ?? fail('Unknown box: ' + line.from), b = items.get(line.to) ?? fail('Unknown box: ' + line.to);
    const dashed = /style=(dashed|dotted)/.test(line.attrs), looks = [{ dashed, arrow: !!line.arrowFrom }, { dashed, arrow: !!line.arrowTo }];
    if (line.fixed) {
      // From the given side of the first box straight to the facing side of the second, a point item.
      const side = line.fixed, flat = side === 'top' || side === 'bottom';
      const points = side === 'top' ? [[b.x, a.t], [b.x, b.b]] : side === 'bottom' ? [[b.x, a.b], [b.x, b.t]] : side === 'left' ? [[a.l, b.y], [b.r, b.y]] : [[a.r, b.y], [b.l, b.y]];
      router.occupyFixed(points, id);
      use(a, side, flat ? b.x : b.y, looks[0]);
      use(b, OPPOSITE[side], flat ? b.x : b.y, looks[1]);
      results.set(line.key, { line, points, fromSide: side, toSide: OPPOSITE[side], looks, id });
      return;
    }
    const foreign = frames.filter(f => !f.members.includes(line.from) && !f.members.includes(line.to));
    const labelled = result => {
      if (line.label) {
        result.label = placeLabel(result.points, line.label, line, labelSolids, frames, foreign, router, id);
        if (result.label) { labelSolids.push(result.label); router.block(result.label, 4); } else problems.push('no room for the label of the line ' + line.key);
      }
      results.set(line.key, result);
    };
    if (line.via) { labelled(given.get(line.key)); return; }
    const exact = (list, on) => on && list.length ? list.filter(p => p.cost <= Math.min(...list.map(q => q.cost)) + 1e-9) : list;
    const starts = exact(router.ports(a, line.fromSides ?? SIDES, used, line.fromPrefer, line.arrowFrom ? STUB : CLEAR + CELL, straightThrough ? null : looks[0]), line.fromExact);
    const goals = exact(router.ports(b, line.toSides ?? SIDES, used, line.toPrefer, line.arrowTo ? STUB : CLEAR + CELL, straightThrough ? null : looks[1]), line.toExact);
    if (!starts.length || !goals.length) { problems.push('no free side for the line ' + line.key + ' (' + (starts.length ? 'at ' + line.to : 'at ' + line.from) + ')'); return; }
    const found = router.search(starts, goals, router.mask(foreign));
    if (!found) { problems.push('no way for the line ' + line.key); return; }
    router.occupy(found, id);
    use(a, found.start.side, found.start.coord, looks[0]);
    use(b, found.goal.side, found.goal.coord, looks[1]);
    // Corner points: the side of the first box, the cells where the line turns, the side of the last box.
    const cellXY = k => [router.X((k >> 2) % router.W), router.Y(Math.floor((k >> 2) / router.W))];
    const sidePoint = (item, end) => { const [x, y] = cellXY(end.c * 4); return end.side === 'top' ? [x, item.t] : end.side === 'bottom' ? [x, item.b] : end.side === 'left' ? [item.l, y] : [item.r, y]; };
    const points = [sidePoint(a, found.start)];
    found.keys.forEach((k, q) => { if (q === 0 || q === found.keys.length - 1 || (k & 3) !== (found.keys[q + 1] & 3)) points.push(cellXY(k)); });
    points.push(sidePoint(b, found.goal));
    const clean = points.filter((p, q) => q === 0 || q === points.length - 1 || !((points[q - 1][0] === p[0] && p[0] === points[q + 1][0]) || (points[q - 1][1] === p[1] && p[1] === points[q + 1][1])));
    labelled({ line, points: clean, fromSide: found.start.side, toSide: found.goal.side, looks, id });
  });
  return results;
}

// A label sits on a straight part of its line, away from both ends and from the corners, where it covers
// no box, title or label, no other line and no frame, and lies in no frame that holds neither end of the
// line. Horizontal parts and the middle of the line come first. When no part of the line has room, the
// label goes right beside the line instead, much closer to it than to any other line.
function placeLabel(points, size, line, solids, frames, foreign, router, id) {
  const total = points.slice(1).reduce((sum, p, q) => sum + Math.abs(p[0] - points[q][0]) + Math.abs(p[1] - points[q][1]), 0);
  const wanted = total * (line.labelAt ?? 0.5);
  let best = null, run = 0;
  for (let q = 1; q < points.length; q++) {
    const [p, r] = [points[q - 1], points[q]], flat = p[1] === r[1], length = Math.abs(r[0] - p[0]) + Math.abs(r[1] - p[1]);
    const startGap = q === 1 ? (line.arrowFrom ?? 0) + 12 : 8, endGap = q === points.length - 1 ? (line.arrowTo ?? 0) + 12 : 8;
    for (const [mode, side] of [['on', 0], ['beside', -1], ['beside', 1]]) {
      const half = (flat ? size.w : size.h) / 2, offset = side * ((flat ? size.h : size.w) / 2 + 4);
      for (let s = half + startGap; s <= length - half - endGap; s += CELL) {
        const t = s / length, x = p[0] + (r[0] - p[0]) * t + (flat ? 0 : offset), y = p[1] + (r[1] - p[1]) * t + (flat ? offset : 0), box = rect(x, y, size.w, size.h);
        if (solids.some(o => meets(box, o, 3)) || foreign.some(f => meets(box, f, 3))) continue;
        if (frames.some(f => [[f.l, f.t, f.r, f.t], [f.l, f.b, f.r, f.b], [f.l, f.t, f.l, f.b], [f.r, f.t, f.r, f.b]].some(([l, t, rr, b]) => meets(box, { l, t, r: rr, b }, 3)))) continue;
        const room = mode === 'on' ? 6 : 24;
        if (!router.clearOfLines(rect(x, y, size.w + room, size.h + room), id)) continue;
        const score = Math.abs(run + s - wanted) + (flat ? 0 : 60) + (mode === 'on' ? 0 : 400);
        if (!best || score < best.score) best = { ...box, score, beside: mode !== 'on' };
      }
    }
    run += length;
  }
  return best;
}

// Measures the drawn picture from its geometry and lists what is wrong with it:
// - more crossings between lines than maxCrossings allows;
// - a line through a box, a label or a title, or through the frame of a group that holds neither of its ends;
// - a label in such a frame, or touching a box or another label;
// - lines that run along one another closer than SPACE cells;
// - boxes that overlap;
// - unless straightThrough is true, two line ends of the same look straight across a box from each other,
//   which read as one line through the box.
function check(results, items, solids, frames, maxCrossings, straightThrough, problems) {
  const segments = [...results.values()].flatMap(r => r.points.slice(1).map((p, q) => ({ r, owner: r, a: r.points[q], b: p, first: q === 0, last: q === r.points.length - 2 })));
  const lo = (s, k) => Math.min(s.a[k], s.b[k]), hi = (s, k) => Math.max(s.a[k], s.b[k]);
  for (const s of segments) if (s.a[0] !== s.b[0] && s.a[1] !== s.b[1]) problems.push('the line ' + s.r.line.key + ' has a slanted piece');
  const crossings = crossingsOf(segments);
  for (let x = 0; x < segments.length; x++) for (let y = x + 1; y < segments.length; y++) {
    const s = segments[x], t = segments[y], sFlat = s.a[1] === s.b[1], tFlat = t.a[1] === t.b[1];
    if (s.r === t.r || sFlat !== tFlat) continue;
    const k = sFlat ? 0 : 1, other = 1 - k;
    if (Math.abs(s.a[other] - t.a[other]) < SPACE * CELL && Math.min(hi(s, k), hi(t, k)) > Math.max(lo(s, k), lo(t, k)))
      problems.push('lines run along each other: ' + s.r.line.key + ' and ' + t.r.line.key);
  }
  const obstacles = [...[...items.values()].map(o => ({ o, what: o.id })), ...solids.map(o => ({ o, what: o.id ?? 'title' })), ...frames.filter(f => f.title).map(f => ({ o: f.title, what: 'the title of the frame ' + f.id })), ...[...results.values()].filter(r => r.label).map(r => ({ o: r.label, what: 'label of ' + r.line.key, own: r }))];
  const foreignTo = r => frames.filter(f => !f.members.includes(r.line.from) && !f.members.includes(r.line.to));
  let through = 0, throughFrames = 0;
  for (const s of segments) {
    const seg = { l: lo(s, 0), r: hi(s, 0), t: lo(s, 1), b: hi(s, 1) };
    for (const { o, what, own } of obstacles) {
      if (own === s.r || what === s.r.line.from && s.first || what === s.r.line.to && s.last) continue;
      if (meets(seg, o, -0.5)) { through++; problems.push('the line ' + s.r.line.key + ' runs through ' + what); }
    }
    for (const f of foreignTo(s.r)) if (meets(seg, f, -0.5)) { throughFrames++; problems.push('the line ' + s.r.line.key + ' runs through the frame ' + f.id + ', which holds neither of its ends'); }
  }
  for (const r of results.values()) if (r.label) for (const f of foreignTo(r)) if (meets(r.label, f, 0)) problems.push('the label of the line ' + r.line.key + ' lies in the frame ' + f.id + ', which holds neither end of the line');
  const labels = [...results.values()].filter(r => r.label);
  labels.forEach((a, q) => { for (const b of [...labels.slice(q + 1).map(r => r.label), ...items.values()]) if (meets(a.label, b, 0)) problems.push('the label of the line ' + a.line.key + ' touches another label or a box'); });
  const boxes = [...items.values()];
  boxes.forEach((a, q) => { for (const b of boxes.slice(q + 1)) if (meets(a, b, 4)) problems.push('boxes overlap: ' + a.id + ' and ' + b.id); });
  if (!straightThrough) {
    // The ends of the lines at every box: the side, the place along it and the look.
    const ends = [...results.values()].flatMap(r => [[r.line.from, r.fromSide, r.points[0], r.looks[0]], [r.line.to, r.toSide, r.points.at(-1), r.looks[1]]]
      .map(([box, side, point, look]) => ({ r, box, side, coord: side === 'top' || side === 'bottom' ? point[0] : point[1], look })));
    ends.forEach((a, q) => { for (const b of ends.slice(q + 1)) if (a.box === b.box && a.r !== b.r && OPPOSITE[a.side] === b.side && Math.abs(a.coord - b.coord) < ALIGNED && sameLook(a.look, b.look))
      problems.push('the lines ' + a.r.line.key + ' and ' + b.r.line.key + ' stand straight across ' + a.box + ' from each other and read as one line'); });
  }
  if (crossings > maxCrossings) problems.push(crossings + ' crossings, the source allows ' + maxCrossings);
  return { crossings, through, throughFrames, bends: [...results.values()].reduce((sum, r) => sum + r.points.length - 2, 0), lines: results.size };
}

// The DOT text for nop2: every node and every line where it was computed. y is negated, because
// Graphviz counts y upward. Labels are moved behind the lines in the SVG, so that a label interrupts
// its own line.
const pos = (x, y) => 'pos="' + x.toFixed(2) + ',' + (-y).toFixed(2) + '"';
function linePos(points, arrowFrom, arrowTo) {
  const p = points.map(q => [...q]), shorten = (end, next, length) => { const dx = Math.sign(next[0] - end[0]), dy = Math.sign(next[1] - end[1]); end[0] += dx * length; end[1] += dy * length; };
  const tip = [...p.at(-1)], tail = [...p[0]];
  if (arrowTo) shorten(p[p.length - 1], p[p.length - 2], arrowTo);
  if (arrowFrom) shorten(p[0], p[1], arrowFrom);
  const spline = [p[0]];
  for (let q = 1; q < p.length; q++) { const [a, b] = [p[q - 1], p[q]]; spline.push([a[0] + (b[0] - a[0]) / 3, a[1] + (b[1] - a[1]) / 3], [a[0] + 2 * (b[0] - a[0]) / 3, a[1] + 2 * (b[1] - a[1]) / 3], b); }
  const xy = q => q[0].toFixed(2) + ',' + (-q[1]).toFixed(2);
  // Graphviz reads the start point before the end point; the other way round it drops the whole route.
  return 'pos="' + (arrowFrom ? 's,' + xy(tail) + ' ' : '') + (arrowTo ? 'e,' + xy(tip) + ' ' : '') + spline.map(xy).join(' ') + '"';
}
function labelsOnTop(svg) {
  const LABEL = /<g id="node\d+" class="node">\s*<title>lbl_[^<]*<\/title>[\s\S]*?<\/g>\n/g;
  const labels = svg.match(LABEL) ?? [];
  const rest = svg.replace(LABEL, ''), end = rest.lastIndexOf('</g>');
  return rest.slice(0, end) + labels.join('') + rest.slice(end);
}
const safeId = key => key.replace(/\W/g, '_');
// Gives every line a key of its own: the second line with the same base key gets "_2", and so on.
function uniqueKeys(lines) {
  const seen = new Map();
  for (const line of lines) { const count = (seen.get(line.key) ?? 0) + 1; seen.set(line.key, count); if (count > 1) line.key += '_' + count; }
  return lines;
}
// Applies layout.lines and layout.order to the lines of a diagram: the settings of each line, then the
// order in which the lines are routed. A key that names no line stops the build.
function arrange(lines, layout, file) {
  const known = new Set(lines.map(line => line.key)), order = layout.order ?? [];
  for (const key of [...Object.keys(layout.lines ?? {}), ...order]) if (!known.has(key)) fail(file + ': the layout names an unknown line: ' + key);
  for (const line of lines) {
    // The layout may break a long label into lines ("text": [...]); the words stay those of the source.
    const given = layout.lines?.[line.key] ?? {};
    if (given.text !== undefined && [given.text].flat().join('') !== [line.text ?? ''].flat().join('')) fail(file + ': the layout breaks the label of ' + line.key + ' into lines that do not add up to "' + line.text + '"');
    Object.assign(line, given);
  }
  const rank = line => line.fixed ? -1 : order.includes(line.key) ? order.indexOf(line.key) : order.length;
  return lines.map((line, index) => [line, index]).sort(([a, i], [b, j]) => rank(a) - rank(b) || i - j).map(([line]) => line);
}

// Draws a picture with a fixed layout. statements: id -> DOT attributes of every box, frame title, line
// label ("lbl_" + line key) and the picture title ("title"). place(sizes) returns the centre of every box;
// points names the boxes that lines join at the middle of a side only (circles and diamonds).
// frames: [{ id, title, members, dashed, titleAt, titleAlign, span, spanY, square, fill }], the frame drawn
// around its members, reaching at least from span[0] to span[1] and from spanY[0] to spanY[1] when given;
// alignFrames gives every frame the same top and bottom edge, and titleGap is the room between the
// picture title and the frames. lines: see routeAll; text is the text of the label.
// Returns the size of the PNG and the measurements of check().
async function drawFixed(o) {
  for (const line of o.lines) if (line.text) { line.label = true; o.statements.set('lbl_' + safeId(line.key), 'label=' + edgeLabel([line.text].flat(), o.labelStyle)); }
  const sizes = measure(o.defaults, [...o.statements].map(([id, attrs]) => id + ' [' + attrs + '];'));
  const size = id => sizes.get(id) ?? fail(o.name + ': no size for ' + id);
  const centres = o.place(sizes);
  const items = new Map([...centres].map(([id, [x, y]]) => [id, { id, ...rect(x, y, size(id).w, size(id).h), point: o.points?.has(id) }]));
  const PAD = o.framePad ?? 16;
  const frames = o.frames.map(f => {
    const members = f.members.map(id => items.get(id) ?? fail(o.name + ': the frame ' + f.id + ' holds a box without a place: ' + id)), t = size(f.title);
    const box = { l: Math.min(...members.map(m => m.l)) - PAD, r: Math.max(...members.map(m => m.r)) + PAD, t: Math.min(...members.map(m => m.t)) - PAD, b: Math.max(...members.map(m => m.b)) + PAD };
    if (f.titleAt === 'bottom') box.b += t.h + 2; else box.t -= t.h + 2;
    if (f.span) { box.l = Math.min(box.l, f.span[0]); box.r = Math.max(box.r, f.span[1]); }
    if (f.spanY) { box.t = Math.min(box.t, f.spanY[0]); box.b = Math.max(box.b, f.spanY[1]); }
    if (box.r - box.l < t.w + 2 * PAD) { const grow = (t.w + 2 * PAD - (box.r - box.l)) / 2; box.l -= grow; box.r += grow; }
    const title = { id: f.title, ...rect(f.titleAlign === 'centre' ? (box.l + box.r) / 2 : f.titleAlign === 'right' ? box.r - 10 - t.w / 2 : box.l + 10 + t.w / 2, f.titleAt === 'bottom' ? box.b - 6 - t.h / 2 : box.t + 6 + t.h / 2, t.w, t.h) };
    return { ...f, ...box, title };
  });
  if (o.layout.alignFrames) {
    // Every frame gets the same top and bottom edge, so that the groups stand side by side in one band.
    const top = Math.min(...frames.map(f => f.t)), bottom = Math.max(...frames.map(f => f.b));
    for (const f of frames) { const shift = f.titleAt === 'bottom' ? bottom - f.b : top - f.t; Object.assign(f, { t: top, b: bottom }); f.title = { ...f.title, ...rect(f.title.x, f.title.y + shift, f.title.w, f.title.h) }; }
  }
  const problems = [];
  frames.forEach((f, q) => { for (const g of frames.slice(q + 1)) if (meets(f, g, 8)) problems.push('the frames ' + f.id + ' and ' + g.id + ' overlap'); });
  for (const f of frames) for (const item of items.values()) {
    if (!f.members.includes(item.id) && meets(f, item, 6)) problems.push(item.id + ' lies on the frame ' + f.id);
    if (f.members.includes(item.id) && meets(f.title, item, 2)) problems.push(item.id + ' covers the title of the frame ' + f.id);
  }
  // The picture title, centred above everything, or at the left edge with titleAlign 'left'.
  const everything = [...items.values(), ...frames], top = Math.min(...everything.map(r => r.t)), t = size('title');
  const left = Math.min(...everything.map(r => r.l)), titleBox = { id: 'title', ...rect(o.titleAlign === 'left' ? left + t.w / 2 : (left + Math.max(...everything.map(r => r.r))) / 2, top - (o.layout.titleGap ?? 24) - t.h / 2, t.w, t.h) };
  const lines = o.lines.map(line => ({ ...line, label: line.label ? size('lbl_' + safeId(line.key)) : null }));
  const results = routeAll(items, frames, lines, [titleBox], problems, !!o.straightThrough);
  const measured = check(results, items, [titleBox], frames, o.layout.maxCrossings ?? 0, !!o.straightThrough, problems);
  const dot = ['digraph ' + o.name.replace(/\W/g, '') + ' {', 'graph [outputorder=nodesfirst, pad=0.3];', ...o.defaults, o.edgeDefaults];
  for (const f of frames) dot.push('frame_' + f.id + ' [shape=box, style="' + [f.square ? '' : 'rounded', f.dashed ? 'dashed' : '', f.fill ? 'filled' : ''].filter(Boolean).join(',') + '"' + (f.fill ? ', fillcolor="' + f.fill + '"' : '') + ', color="' + LINE + '", label="", fixedsize=true, width=' + ((f.r - f.l) / 72).toFixed(4) + ', height=' + ((f.b - f.t) / 72).toFixed(4) + ', ' + pos((f.l + f.r) / 2, (f.t + f.b) / 2) + '];',
    f.title.id + ' [' + o.statements.get(f.title.id) + ', ' + pos(f.title.x, f.title.y) + '];');
  dot.push('title [' + o.statements.get('title') + ', ' + pos(titleBox.x, titleBox.y) + '];');
  for (const item of items.values()) dot.push(item.id + ' [' + o.statements.get(item.id) + ', ' + pos(item.x, item.y) + '];');
  for (const r of results.values()) {
    if (r.label) dot.push('lbl_' + safeId(r.line.key) + ' [' + o.statements.get('lbl_' + safeId(r.line.key)) + ', ' + pos(r.label.x, r.label.y) + '];');
    dot.push(r.line.from + ' -> ' + r.line.to + ' [' + r.line.attrs + ', ' + linePos(r.points, r.line.arrowFrom, r.line.arrowTo) + '];');
  }
  dot.push('}');
  await keepDebug(o.name + '.layout.json', JSON.stringify({ problems, measured, items: [...items.values()], frames, lines: [...results.values()].map(r => ({ key: r.line.key, points: r.points, label: r.label })) }, null, 1));
  if (problems.length) {
    // A rejected layout is never written to the output folder. With --debug its picture is kept for a look.
    if (debugOut) await renderPng(dot.join('\n'), debugOut, o.name + ' (rejected)', labelsOnTop, o.density ?? 110, 'nop2');
    fail(o.name + ': ' + [...new Set(problems)].join('; ') + (debugOut ? '. The rejected picture is in ' + debugOut : '. Run again with --debug <folder> to get a picture of the rejected layout'));
  }
  // Read the picture back: every line and every box must be in it once, and Graphviz must have drawn
  // every line along the given corner points, without a slanted or curved piece.
  let drawn = null;
  const png = await renderPng(dot.join('\n'), o.dir, o.name, svg => {
    const lined = labelsOnTop(svg);
    drawn = { edges: (lined.match(/class="edge"/g) ?? []).length, nodes: (lined.match(/class="node"/g) ?? []).length, pieces: drawnSegments(lined, o.name).length };
    return lined;
  }, o.density ?? 110, 'nop2', o.pixelWidth ?? 0, o.fileName ?? o.name);
  const pieces = [...results.values()].reduce((sum, r) => sum + r.points.length - 1, 0);
  if (drawn.pieces !== pieces) fail(o.name + ': the picture holds ' + drawn.pieces + ' straight pieces of lines, expected ' + pieces);
  const nodes = items.size + frames.length * 2 + 1 + [...results.values()].filter(r => r.label).length;
  if (drawn.edges !== results.size || drawn.nodes !== nodes) fail(o.name + ': the picture holds ' + drawn.edges + ' lines and ' + drawn.nodes + ' boxes, expected ' + results.size + ' and ' + nodes);
  return { png, ...measured, items, results };
}
// The place of a box in layout.at: [x, y] is its centre. [x, y, "<anchor>"] puts another point of the box
// there instead: "t", "b", "l" or "r" the middle of that side, "tl", "tr", "bl" or "br" that corner.
function placeBoxes(ids, layout, file) {
  const at = layout.at ?? {};
  for (const id of Object.keys(at)) if (!ids.includes(id)) fail(file + ': the layout places an unknown box: ' + id);
  return sizes => new Map(ids.map(id => {
    const [x, y, anchor = ''] = at[id] ?? fail(file + ': the layout has no place for ' + id);
    if (typeof x !== 'number' || typeof y !== 'number' || !/^(|t|b|l|r|tl|tr|bl|br)$/.test(anchor)) fail(file + ': the layout places ' + id + ' at ' + JSON.stringify(at[id]) + ', which is not [x, y] or [x, y, anchor]');
    const { w, h } = sizes.get(id) ?? fail(file + ': no size for ' + id);
    return [id, [x + (anchor.includes('l') ? w / 2 : anchor.includes('r') ? -w / 2 : 0), y + (anchor.includes('t') ? h / 2 : anchor.includes('b') ? -h / 2 : 0)]];
  }));
}
const fixedSummary = drawn => drawn.png + ', ' + drawn.crossings + ' crossings, ' + drawn.bends + ' bends';

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
if (only.includes('erd')) {
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

  // Picture: fixed layout (see "Fixed layout with right-angled lines"). layout.at holds the place of every
  // table; the frame of a group is drawn around its tables, and a relation between two groups runs from
  // one frame to the other. The key of a line is "<child table>_<parent table>". The title and the notation
  // above the picture are the header lines of the text file after the first, without the PlantUML brackets.
  const layout = spec.layout ?? fail('erd.json has no layout');
  const statements = new Map();
  for (const group of spec.groups) {
    statements.set('title_' + group.id, 'label=' + label([group.label], { size: 14, bold: true, color: NAVY }, 3));
    for (const name of group.tables) {
      const table = tables.get(name), [keys, others] = keyFirst(table);
      const row = c => '<TR><TD>' + (required(table, c) ? '*' : ' ') + '</TD><TD ALIGN="LEFT">' + esc(c.name) + '</TD><TD ALIGN="LEFT"><FONT COLOR="' + MUTED + '">' + c.type + '</FONT></TD><TD>' + (mark(table, c) || ' ') + '</TD></TR>';
      const tableRows = keys.map(row).join('') + (keys.length && others.length ? '<HR/>' : '') + others.map(row).join('');
      statements.set(name, 'fontname="Arial", label=<<TABLE BORDER="1" CELLBORDER="0" CELLSPACING="0" CELLPADDING="3" COLOR="' + LINE + '" BGCOLOR="#FFFFFF"><TR><TD COLSPAN="4" BGCOLOR="' + NAVY + '"><FONT COLOR="#FFFFFF"><B>' + esc(name) + '</B></FONT></TD></TR>' + tableRows + '</TABLE>>');
    }
  }
  const notation = spec.header.slice(1).map(line => line.replace(/<<(\w+)>>/g, '$1'));
  statements.set('title', 'label=<<TABLE BORDER="0" CELLBORDER="0" CELLSPACING="0" CELLPADDING="3">' + htmlRows([{ text: spec.title, size: 20, color: NAVY, align: 'left' }, ...notation.map(text => ({ text, size: 12, align: 'left' }))]) + '</TABLE>>');
  // Line ends in IE notation, the shape next to the table first: two bars for exactly one, a bar and a
  // circle for zero or one, a crow's foot and a circle for zero or many. The number is the length of the
  // line end in points; the line itself stops where the line end begins.
  const END = { one: ['teetee', 10], zeroOrOne: ['teeodot', 13], zeroOrMany: ['crowodot', 18] };
  const lines = arrange(uniqueKeys(spec.relations.map(r => {
    const child = END[r.oneToOne ? 'zeroOrOne' : 'zeroOrMany'], parent = END[r.toOptional ? 'zeroOrOne' : 'one'];
    const texts = r.label.split(',').map((item, index, all) => item.trim() + (index + 1 < all.length ? ',' : ''));
    return { key: r.from + '_' + r.to, from: r.from, to: r.to, text: texts, attrs: 'dir=both, arrowtail=' + child[0] + ', arrowhead=' + parent[0] + (r.identifying ? '' : ', style=dashed'), arrowFrom: child[1], arrowTo: parent[1] };
  })), layout, 'erd.json');
  const frames = spec.groups.map(g => ({ id: g.id, title: 'title_' + g.id, members: g.tables, titleAt: 'top', ...(layout.frames?.[g.id] ?? {}) }));
  const drawn = await drawFixed({ name: 'ERD', dir: out, statements, frames, lines, layout, labelStyle: { size: 10 }, titleAlign: 'left', density: 120, straightThrough: true, framePad: 28,
    place: placeBoxes(grouped, layout, 'erd.json'),
    defaults: ['node [shape=plain, fontname="' + CJK_FONT + '", fontsize=11, fontcolor="' + INK + '"];'], edgeDefaults: 'edge [color="' + LINE + '"];' });
  console.log('ERD:', tables.size, 'tables,', spec.relations.length, 'relations,', fixedSummary(drawn));
}

// ---------------------------------------------------------------- Class diagram
if (only.includes('class')) {
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

  // Picture: fixed layout (see "Fixed layout with right-angled lines"). layout.at holds the place of every
  // class; the frame of a group is drawn around its classes. layout.memberWidth is the width, in points, at
  // which a long member is broken into two lines. The key of a line is "<from>_<to>".
  const layout = spec.layout ?? fail('uml.json has no layout');
  const statements = new Map();
  for (const group of spec.groups) {
    statements.set('title_' + group.id, 'label=' + label([group.label], { size: 13, color: NAVY }, 3));
    for (const name of group.classes) {
      const c = byName(name);
      const lines = [...(c.stereotype ? [{ text: '«' + c.stereotype + '»', size: 10, bg: LIGHT }] : []), { text: name, bold: true, bg: LIGHT, hr: true }];
      c.members.forEach((member, at) => {
        wrap(member.text, 10, layout.memberWidth ?? 340, ', ').forEach((piece, index) => lines.push({ text: piece, size: 10, align: 'left', underline: !!member.static, indent: index ? 14 : 0 }));
        // A line between the fields and the methods, as PlantUML draws its two compartments.
        if (isField(member) && c.members[at + 1] && !isField(c.members[at + 1])) lines.at(-1).hr = true;
      });
      statements.set(name, 'label=' + boxed(lines));
    }
  }
  statements.set('title', 'label=' + label([spec.title], { size: 18, color: NAVY }, 4));
  const style = { implements: ['style=dashed', 'onormal'], creates: ['style=dashed', 'vee'], uses: ['style=solid', 'vee'] };
  const lines = arrange(uniqueKeys(spec.relations.map(r => {
    const [line, head] = style[r.kind];
    return { key: r.from + '_' + r.to, from: r.from, to: r.to, text: relationLabel(r), attrs: line + ', arrowhead=' + head, arrowTo: ARROW[head] };
  })), layout, 'uml.json');
  const frames = spec.groups.map(g => ({ id: g.id, title: 'title_' + g.id, members: g.classes, titleAt: 'top', ...(layout.frames?.[g.id] ?? {}) }));
  const drawn = await drawFixed({ name: 'Class Diagram', dir: structureOut, statements, frames, lines, layout, labelStyle: { size: 10 },
    place: placeBoxes(spec.classes.map(c => c.name), layout, 'uml.json'),
    defaults: ['node [shape=plain, fontname="' + CJK_FONT + '", fontsize=11, fontcolor="' + INK + '"];'], edgeDefaults: 'edge [color="' + LINE + '"];' });
  console.log('Class Diagram:', spec.classes.length, 'classes,', spec.relations.length, 'relations,', fixedSummary(drawn));
}

// ---------------------------------------------------------------- Component diagram
if (only.includes('component')) {
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

  // Picture: fixed layout (see "Fixed layout with right-angled lines"). The centre of every component and
  // note is in layout.at. An interface is a circle on a short solid line from one side of its provider,
  // with its name beside the circle; layout.interfaces gives the side, the place along it and where the
  // name goes. layout.lines may fix the sides a line uses and the order in which the lines are routed.
  const layout = spec.layout ?? fail('component.json has no layout');
  const external = new Set(spec.groups.filter(g => g.external).flatMap(g => g.components));
  const statements = new Map();
  for (const c of spec.components) {
    const lines = [...(c.stereotype ? [{ text: '«' + c.stereotype + '»', size: 11, bg: LIGHT }] : []), { text: c.name, size: 15, bold: true, bg: external.has(c.id) ? '#FFFFFF' : LIGHT },
      ...(pathText(c) ? wrap(pathText(c), 11, 210, '、').map(text => ({ text, size: 11, color: MUTED })) : []),
      // A role is broken after a comma or a semicolon; a piece that is still much too wide, after「、」as well.
      ...(c.role ? wrap(c.role, 13, 210, '[，；]').flatMap(piece => textWidth(piece, 13) > 250 ? wrap(piece, 13, 210, '、') : [piece]).map(text => ({ text, size: 13 })) : [])];
    statements.set(c.id, 'label=' + boxed(lines, external.has(c.id) ? ' STYLE="dashed"' : ''));
  }
  for (const n of spec.notes) statements.set(n.of + '_note', 'shape=note, style=filled, fillcolor="' + NOTE + '", color="' + LINE + '", margin="0.1,0.06", label=' + label(n.lines, { size: 12, align: 'left' }));
  for (const i of spec.interfaces) {
    statements.set('if_' + i.id, 'shape=circle, fixedsize=true, width=0.24, height=0.24, style=filled, fillcolor="#FFFFFF", color="' + INK + '", label=""');
    statements.set('ifname_' + i.id, 'label=<<TABLE BORDER="0" CELLBORDER="0" CELLSPACING="0" CELLPADDING="0">' + htmlRows(interfaceLines(i).map(text => ({ text, size: 12, align: 'left' }))) + '</TABLE>>');
  }
  for (const g of spec.groups) statements.set('title_' + g.id, 'label=' + label([g.label], { size: 15, bold: true, color: NAVY }, 3));
  statements.set('title', 'label=' + label([spec.title], { size: 20, color: NAVY }, 4));
  const lines = [];
  for (const i of spec.interfaces) {
    const place = layout.interfaces?.[i.id] ?? fail('component.json layout: no place for the interface ' + i.id);
    lines.push({ key: 'if_' + i.id, from: i.providedBy.component, to: 'if_' + i.id, fixed: place.side, attrs: 'dir=none, color="' + INK + '"' });
    for (const u of i.usedBy) lines.push({ key: 'use_' + u.component + '_' + i.id, from: u.component, to: 'if_' + i.id, text: u.label, attrs: 'style=dashed, arrowhead=vee', arrowTo: 8 });
  }
  for (const d of spec.dependencies) lines.push({ key: 'dep_' + d.from + '_' + d.to, from: d.from, to: d.to, text: d.label, attrs: 'style=dashed, arrowhead=vee', arrowTo: 8 });
  for (const n of spec.notes) lines.push({ key: 'note_' + n.of, from: n.of + '_note', to: n.of, attrs: 'style=dotted, dir=none', fromSides: [OPPOSITE[n.side]], toSides: [n.side] });
  const routed = arrange(uniqueKeys(lines), layout, 'component.json');
  const snap = value => Math.round(value / CELL) * CELL;
  const placeAll = sizes => {
    const at = placeBoxes([...spec.components.map(c => c.id), ...spec.notes.map(n => n.of + '_note')], layout, 'component.json')(sizes);
    // A note stands on the side of its component that the source names (PlantUML: note <side> of).
    for (const n of spec.notes) {
      const [nx, ny] = at.get(n.of + '_note'), [cx, cy] = at.get(n.of), c = sizes.get(n.of);
      const there = { left: nx < cx - c.w / 2, right: nx > cx + c.w / 2, top: ny < cy - c.h / 2, bottom: ny > cy + c.h / 2 }[n.side] ?? fail('The note of ' + n.of + ' has an unknown side: ' + n.side);
      if (!there) fail('The note of ' + n.of + ' must be on its ' + n.side);
    }
    for (const i of spec.interfaces) {
      const p = layout.interfaces[i.id], [px, py] = at.get(i.providedBy.component), ps = sizes.get(i.providedBy.component), pr = rect(px, py, ps.w, ps.h);
      const r = sizes.get('if_' + i.id).w / 2, stub = p.stub ?? 24, along = p.at ?? 0.5;
      const x = snap(p.side === 'top' || p.side === 'bottom' ? pr.l + (pr.r - pr.l) * along : p.side === 'left' ? pr.l - stub - r : pr.r + stub + r);
      const y = snap(p.side === 'left' || p.side === 'right' ? pr.t + (pr.b - pr.t) * along : p.side === 'top' ? pr.t - stub - r : pr.b + stub + r);
      at.set('if_' + i.id, [x, y]);
      const n = sizes.get('ifname_' + i.id), gap = 6;
      const across = p.align === 'start' ? x - r + n.w / 2 : p.align === 'end' ? x + r - n.w / 2 : x;
      at.set('ifname_' + i.id, [p.name === 'right' ? x + r + gap + n.w / 2 : p.name === 'left' ? x - r - gap - n.w / 2 : across, p.name === 'above' ? y - r - gap - n.h / 2 : p.name === 'below' ? y + r + gap + n.h / 2 : y]);
    }
    return at;
  };
  const frames = spec.groups.map(g => ({ id: g.id, title: 'title_' + g.id, dashed: !!g.external, titleAt: g.external ? 'bottom' : 'top', ...(layout.frames?.[g.id] ?? {}),
    members: [...g.components, ...spec.interfaces.filter(i => g.components.includes(i.providedBy.component)).flatMap(i => ['if_' + i.id, 'ifname_' + i.id]), ...spec.notes.filter(n => g.components.includes(n.of)).map(n => n.of + '_note')] }));
  const drawn = await drawFixed({ name: 'Component Diagram', dir: structureOut, statements, place: placeAll, frames, lines: routed, layout, labelStyle: { size: 12 },
    points: new Set(spec.interfaces.map(i => 'if_' + i.id)), defaults: ['node [shape=plain, fontname="' + CJK_FONT + '", fontsize=12, fontcolor="' + INK + '"];'], edgeDefaults: 'edge [color="' + LINE + '", arrowsize=0.8];' });
  console.log('Component Diagram:', spec.components.length, 'components,', spec.interfaces.length, 'interfaces,', spec.interfaces.reduce((sum, i) => sum + 1 + i.usedBy.length, 0) + spec.dependencies.length, 'relations,', fixedSummary(drawn));
}

// ---------------------------------------------------------------- System overview
// A simplified view of the component diagram, embedded in the technical document. Every layer, node
// and flow in framework.json points at components and relations of component.json, which the block
// above has just checked against the product source, or names a source file and a string of its own.
if (only.includes('overview')) {
  const spec = await readJson(path.join(source, 'framework.json'));
  const parts = await readJson(path.join(source, spec.componentSpec));
  if (parts.sourceRoot !== spec.sourceRoot) fail('framework.json and ' + spec.componentSpec + ' use different source roots');
  const root = path.join(repo, spec.sourceRoot), check = sourceChecker(root);
  const same = (a, b) => a.slice().sort().join() === b.slice().sort().join();
  const part = id => parts.components.find(c => c.id === id) ?? fail('framework.json names an unknown component: ' + id);
  if (spec.nodes.some(n => n.id === 'host')) fail('framework.json: "host" names the frame of the host and cannot be the id of a node');
  const hostNode = { id: 'host', components: [spec.host.component] };
  const node = id => id === 'host' ? hostNode : spec.nodes.find(n => n.id === id) ?? fail('framework.json names an unknown node: ' + id);
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
    if (to === hostNode) fail(what + ': a flow can start at the frame of the host, not end there');
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
  // frames of the layers and of the host are computed from the nodes they hold. A flow is a straight
  // line between two nodes of one row or one column, or runs through the corner points its "via" gives;
  // a flow from "host" starts at the frame of the host. Graphviz (engine nop2) only draws. The checks
  // below stop the build when two boxes overlap, when a line is not horizontal or vertical, crosses
  // another line, or runs through a box or a label, and when a label touches a box, a frame or a label.
  const PAD = 12, HEAD = 26, FRAME_FILL = '#FBFCFD';
  const boxes = new Map(spec.nodes.map(n => {
    const lines = [...n.name.map(text => ({ text, size: 15, bold: true })),
      ...(n.showPath ? [{ text: spec.pathPrefix + n.components.flatMap(id => part(id).paths).join('、'), size: 12, color: MUTED }] : []),
      ...(n.detail ?? []).map(text => ({ text, size: 13 }))];
    // "width" makes a node wider than its text, so that a line can enter it beside the title of its frame.
    return [n.id, { id: n.id, lines, x: n.at[0], y: n.at[1], w: Math.max(n.width ?? 0, Math.max(...lines.map(l => textWidth(l.text, l.size, l.bold))) + 2 * PAD), h: lines.reduce((sum, l) => sum + l.size * 1.3 + 4, 10) }];
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
  const problems = [];
  const touch = (a, b, margin = 0) => a.l < b.r + margin && a.r > b.l - margin && a.t < b.b + margin && a.b > b.t - margin;
  for (const id of spec.outside) if (touch(edgesOf(boxes.get(id)), hostFrame)) problems.push('the node ' + id + ' is outside the layers but lies on the frame of the host');
  const nodeRects = [...boxes.values()].map(b => ({ id: b.id, ...edgesOf(b) }));
  nodeRects.forEach((a, q) => { for (const b of nodeRects.slice(q + 1)) if (touch(a, b, 4)) problems.push('boxes overlap: ' + a.id + ' and ' + b.id); });
  frames.forEach((a, q) => { for (const b of frames.slice(q + 1)) if (touch(a, b, 4)) problems.push('the frames ' + a.id + ' and ' + b.id + ' overlap'); });
  for (const f of frames) for (const r of nodeRects) if (!spec.layers.find(l => l.group === f.id).nodes.includes(r.id) && touch(f, r, 4)) problems.push(r.id + ' lies on the frame ' + f.id);

  // The corner points of every flow, from the side of its first node to the side of its second, and
  // the place of its label: above a horizontal line, beside a vertical one, in the stretch that is
  // clear of both end nodes and of the frames around only one of them, or where "labelAt" puts it.
  const allFrames = [...frames.map((f, index) => ({ ...f, members: spec.layers[index].nodes })), { ...hostFrame, id: 'host', members: spec.layers.flatMap(l => l.nodes) }];
  const rectOf = id => id === 'host' ? hostFrame : edgesOf(boxes.get(id));
  const routes = spec.flows.map(f => {
    const what = 'the flow ' + f.from + ' -> ' + f.to, a = rectOf(f.from), b = rectOf(f.to);
    const w = Math.max(...f.label.map(text => textWidth(text, 13))), h = f.label.length * 19;
    let points;
    if (f.via) {
      // Level with the first and the last corner point, on the side that faces it.
      const end = (r, p) => {
        const inX = p[0] > r.l && p[0] < r.r, inY = p[1] > r.t && p[1] < r.b;
        if (inX === inY) fail('framework.json: ' + what + ': the corner point ' + JSON.stringify(p) + ' is not level with one side of its node');
        return inX ? [p[0], p[1] < r.t ? r.t : r.b] : [p[0] < r.l ? r.l : r.r, p[1]];
      };
      // A corner point on a straight stretch only says where the line runs; it is no corner.
      const given = [end(a, f.via[0]), ...f.via, end(b, f.via.at(-1))];
      points = given.filter((p, q) => q === 0 || q === given.length - 1 || !((given[q - 1][0] === p[0] && p[0] === given[q + 1][0]) || (given[q - 1][1] === p[1] && p[1] === given[q + 1][1])));
    } else {
      // Straight: both nodes in one row or one column. The frame of the host counts as lying in the row of the other node.
      const ax = f.from === 'host' ? null : boxes.get(f.from).x, ay = f.from === 'host' ? boxes.get(f.to).y : boxes.get(f.from).y, bx = boxes.get(f.to).x, by = boxes.get(f.to).y;
      if (ay === by) points = (f.from === 'host' ? (a.l + a.r) / 2 : ax) < bx ? [[a.r, ay], [b.l, ay]] : [[a.l, ay], [b.r, ay]];
      else if (ax === bx) points = ay < by ? [[ax, a.b], [ax, b.t]] : [[ax, a.t], [ax, b.b]];
      else fail('framework.json: ' + what + ' joins nodes of different rows and columns and needs "via"');
    }
    let at = f.labelAt;
    if (!at) {
      if (f.via) fail('framework.json: ' + what + ' has corner points and needs "labelAt"');
      const flat = points[0][1] === points[1][1], forward = flat ? points[0][0] < points[1][0] : points[0][1] < points[1][1];
      const [firstId, secondId] = forward ? [f.from, f.to] : [f.to, f.from];
      const only = (own, other) => [rectOf(own), ...allFrames.filter(frame => frame.id !== own && frame.members.includes(own) && !frame.members.includes(other) && other !== frame.id)];
      const clearFrom = Math.max(...only(firstId, secondId).map(r => flat ? r.r : r.b)), clearTo = Math.min(...only(secondId, firstId).map(r => flat ? r.l : r.t));
      if (clearTo - clearFrom < (flat ? w : h) + 4) fail('No room for the label of flow ' + f.from + ' -> ' + f.to);
      const middle = (clearFrom + clearTo) / 2;
      at = flat ? [middle, points[0][1] - h / 2 - 4] : [points[0][0] + (f.labelSide === 'left' ? -1 : 1) * (w / 2 + 6), middle];
    }
    return { f, key: f.from + '->' + f.to, points, label: { l: at[0] - w / 2, r: at[0] + w / 2, t: at[1] - h / 2, b: at[1] + h / 2, x: at[0], y: at[1] } };
  });
  const segments = routes.flatMap(r => r.points.slice(1).map((p, q) => ({ a: r.points[q], b: p, owner: r })));
  for (const s of segments) {
    if (s.a[0] !== s.b[0] && s.a[1] !== s.b[1]) problems.push('the flow ' + s.owner.key + ' has a slanted piece');
    const seg = { l: Math.min(s.a[0], s.b[0]), r: Math.max(s.a[0], s.b[0]), t: Math.min(s.a[1], s.b[1]), b: Math.max(s.a[1], s.b[1]) };
    for (const r of nodeRects) if (r.id !== s.owner.f.from && r.id !== s.owner.f.to && touch(seg, r, 3)) problems.push('the flow ' + s.owner.key + ' runs through ' + r.id);
    for (const r of routes) if (touch(seg, r.label, 1)) problems.push('the flow ' + s.owner.key + ' runs through the label of ' + r.key);
    for (const frame of allFrames) if (touch(seg, { l: frame.l + PAD, r: frame.l + PAD + textWidth(frame.title, 14, true), t: frame.t + 2, b: frame.t + HEAD - 2 }, 2)) problems.push('the flow ' + s.owner.key + ' runs through the title of the frame ' + frame.id);
    // A line may only cross the frames that hold one of its ends but not the other.
    for (const frame of allFrames) if (frame.id !== s.owner.f.from && !frame.members.includes(s.owner.f.from) && !frame.members.includes(s.owner.f.to) && touch(seg, frame)) problems.push('the flow ' + s.owner.key + ' runs through the frame ' + frame.id);
  }
  const crossings = crossingsOf(segments);
  if (crossings > (spec.maxCrossings ?? 0)) problems.push(crossings + ' crossings, the source allows ' + (spec.maxCrossings ?? 0));
  routes.forEach((r, q) => {
    for (const other of routes.slice(q + 1)) if (touch(r.label, other.label, 2)) problems.push('the labels of ' + r.key + ' and ' + other.key + ' touch');
    for (const box of nodeRects) if (touch(r.label, box, 2)) problems.push('the label of ' + r.key + ' touches ' + box.id);
    for (const frame of allFrames) if ([[frame.l, frame.t, frame.r, frame.t], [frame.l, frame.b, frame.r, frame.b], [frame.l, frame.t, frame.l, frame.b], [frame.r, frame.t, frame.r, frame.b]].some(([l, t, rr, b]) => touch(r.label, { l, t, r: rr, b }, 2))) problems.push('the label of ' + r.key + ' lies on the frame ' + frame.id);
  });
  if (problems.length) fail('System overview: ' + [...new Set(problems)].join('; '));

  const inch = value => (value / 72).toFixed(4);
  const sized = (w, h) => 'fixedsize=true, width=' + inch(w) + ', height=' + inch(h);
  const frame = (id, f, fill, color) => [
    id + ' [shape=box, style="rounded,filled", fillcolor="' + fill + '", color="' + color + '", label="", ' + sized(f.r - f.l, f.b - f.t) + ', ' + pos((f.l + f.r) / 2, (f.t + f.b) / 2) + '];',
    id + '_title [shape=plain, label=' + label([f.title], { size: 14, bold: true, color: NAVY }, 0) + ', ' + pos(f.l + PAD + textWidth(f.title, 14, true) / 2, f.t + HEAD / 2 + 1) + '];'];
  // Later nodes are drawn over earlier ones: the host frame first, then the layers, then the nodes.
  const dot = ['digraph SystemOverview {', 'graph [outputorder=nodesfirst, pad=0.12];',
    'node [fontname="' + CJK_FONT + '", fontsize=13, fontcolor="' + INK + '"];', 'edge [color="' + INK + '", arrowhead=vee, arrowsize=0.8];',
    ...frame('host', hostFrame, '#FFFFFF', NAVY), ...frames.flatMap(f => frame('layer_' + f.id, f, FRAME_FILL, LINE))];
  for (const [id, b] of boxes) {
    const outside = spec.outside.includes(id);
    dot.push(id + ' [shape=box, style="' + (outside ? 'rounded,filled' : 'filled') + '", fillcolor="' + (outside ? '#FFFFFF' : LIGHT) + '", color="' + LINE + '", ' + sized(b.w, b.h) + ', ' + pos(b.x, b.y)
      + ', label=<<TABLE BORDER="0" CELLBORDER="0" CELLSPACING="0" CELLPADDING="2">' + htmlRows(b.lines) + '</TABLE>>];');
  }
  routes.forEach((r, index) => dot.push(r.f.from + ' -> ' + r.f.to + ' [' + linePos(r.points, 0, 8) + '];',
    'flow' + index + ' [shape=plain, label=' + label(r.f.label, { size: 13 }, 1) + ', ' + pos(r.label.x, r.label.y) + '];'));
  dot.push('}');
  // Read the picture back: every flow is in it, drawn along its corner points without a slanted piece.
  let drawn = null;
  const size = await renderPng(dot.join('\n'), frameworkOut, 'framework', svg => { drawn = { edges: (svg.match(/class="edge"/g) ?? []).length, pieces: drawnSegments(svg, 'System overview').length }; return svg; }, 110, 'nop2', 2000);
  if (drawn.edges !== routes.length || drawn.pieces !== segments.length) fail('System overview: the picture holds ' + drawn.edges + ' lines in ' + drawn.pieces + ' straight pieces, expected ' + routes.length + ' and ' + segments.length);
  console.log('System overview (' + spec.title + '):', spec.nodes.length, 'nodes,', spec.layers.length, 'layers,', spec.flows.length, 'flows,', size + ',', crossings, 'crossings,', segments.length - routes.length, 'bends');
}

// ---------------------------------------------------------------- Activity diagram
if (only.includes('activity')) {
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

  // Picture: fixed layout (see "Fixed layout with right-angled lines"). One frame per lane, side by side;
  // layout.at holds the centre of every node, layout.lanes the left and right edge of every lane.
  // Branches that meet again go through a merge node, because an action with two incoming flows would
  // mean "wait for both" in UML. Diamonds and circles are joined at their corners and at their top,
  // bottom, left and right points only.
  const layout = spec.layout ?? fail('activity.json has no layout');
  const statements = new Map(), lines = [], points = new Set(), laneMembers = new Map(spec.lanes.map(l => [l.id, []]));
  let merges = 0;
  const node = (id, laneId, attrs, point = false) => { statements.set(id, attrs); laneMembers.get(laneId).push(id); if (point) points.add(id); };
  // A guard sits on its line right next to its decision.
  const link = (pending, target) => { for (const p of pending) lines.push({ key: p.from + '_' + target, from: p.from, to: target, text: p.guard, attrs: 'style=solid', arrowTo: 8, ...(p.guard ? { labelAt: 0 } : {}) }); };
  const mergeInto = (pending, laneId) => {
    if (pending.length < 2) return pending;
    const id = 'merge' + (++merges);
    node(id, laneId, 'shape=diamond, fixedsize=true, width=0.36, height=0.36, label="", color="' + INK + '"', true);
    link(pending, id);
    return [{ from: id }];
  };
  function draw(flow, pending) {
    for (const step of flow) {
      pending = mergeInto(pending, step.lane);
      if (step.type === 'action') {
        node(step.id, step.lane, 'shape=box, style="rounded,filled", fillcolor="' + LIGHT + '", color="' + LINE + '", margin="0.1,0.07", label=' + label(step.text, { size: 14 }));
        if (step.note) {
          node(step.id + '_note', step.lane, 'shape=note, style=filled, fillcolor="' + NOTE + '", color="' + LINE + '", margin="0.1,0.06", label=' + label(step.note, { size: 11.5, align: 'left' }));
          lines.push({ key: step.id + '_note', from: step.id, to: step.id + '_note', attrs: 'style=dotted, arrowhead=none', fromSides: ['right'], toSides: ['left'], note: true });
        }
        link(pending, step.id);
        pending = [{ from: step.id }];
      } else {
        node(step.id, step.lane, 'shape=diamond, style=filled, fillcolor="#FFFFFF", color="' + INK + '", margin="0.01,0.01", label=' + label(step.text, { size: 13 }), true);
        link(pending, step.id);
        pending = step.branches.flatMap(branch => draw(branch.flow, [{ from: step.id, guard: branch.guard }]));
      }
    }
    return pending;
  }
  const first = spec.flow[0].lane;
  node('start', first, 'shape=circle, style=filled, fillcolor="' + INK + '", color="' + INK + '", fixedsize=true, width=0.26, label=""', true);
  const last = mergeInto(draw(spec.flow, [{ from: 'start' }]), spec.endLane);
  node('finish', spec.endLane, 'shape=doublecircle, style=filled, fillcolor="' + INK + '", color="' + INK + '", fixedsize=true, width=0.22, label=""', true);
  link(last, 'finish');
  const routed = arrange(uniqueKeys(lines), layout, 'activity.json');
  for (const l of spec.lanes) statements.set('title_' + l.id, 'label=' + label([l.label], { size: 15, bold: true, color: NAVY }, 3));
  statements.set('title', 'label=' + label([spec.title], { size: 18, color: NAVY }, 4));
  const frames = spec.lanes.map(l => ({ id: l.id, title: 'title_' + l.id, members: laneMembers.get(l.id), titleAt: 'top', titleAlign: 'centre', square: true, fill: '#FBFCFD',
    span: layout.lanes?.[l.id] ?? fail('activity.json layout: no edges for the lane ' + l.id) }));
  const drawn = await drawFixed({ name: 'Activity Diagram', dir: behaviorOut, statements, frames, lines: routed, layout: { ...layout, alignFrames: true }, labelStyle: { size: 13, bold: true }, points, straightThrough: true,
    place: placeBoxes([...statements.keys()].filter(id => id !== 'title' && !id.startsWith('title_')), layout, 'activity.json'),
    defaults: ['node [shape=plain, fontname="' + CJK_FONT + '", fontsize=13, fontcolor="' + INK + '"];'], edgeDefaults: 'edge [color="' + INK + '", arrowhead=vee, arrowsize=0.8];' });
  const actions = all.filter(step => step.type === 'action').length, decisions = all.length - actions;
  console.log('Activity Diagram:', actions, 'actions,', decisions, 'decisions,', merges, 'merge nodes,', lines.filter(line => !line.note).length, 'flows,', fixedSummary(drawn));
}
