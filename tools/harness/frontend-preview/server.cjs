'use strict';
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const root = path.resolve(__dirname, '../../..');
const product = path.join(root, 'src/JET/JET/wwwroot');
const output = path.join(root, 'artifacts/frontend-preview');
const mime = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.json': 'application/json; charset=utf-8', '.ttf': 'font/ttf', '.woff2': 'font/woff2', '.svg': 'image/svg+xml', '.png': 'image/png', '.ico': 'image/x-icon' };
function boundedRead(file) {
  if (fs.statSync(file).size > 32 * 1024 * 1024) throw new Error('Preview resource exceeds size limit.');
  return fs.readFileSync(file);
}
function fixture() {
  const bundle = JSON.parse(boundedRead(path.join(output, 'fixtures.json')));
  if (bundle.schemaVersion !== 1 || bundle.synthetic !== true || !Array.isArray(bundle.fixtures)) throw new Error('Invalid synthetic fixture.');
  for (const [relative, expected] of Object.entries(bundle.sourceHashes)) {
    const target = path.resolve(root, relative);
    if (!target.startsWith(path.join(root, 'src/JET/JET') + path.sep)) throw new Error('Invalid fixture source.');
    if (crypto.createHash('sha256').update(boundedRead(target)).digest('hex') !== expected)
      throw new Error('Product contract changed; regenerate fixtures with the documented Focused command.');
  }
  return bundle;
}
function previewHtml() {
  const source = boundedRead(path.join(product, 'index.html')).toString('utf8');
  return source.replace(/(src|href)="\.\//g, '$1="/runtime/')
    .replace('<script defer src="/runtime/js/jet-api.js">', '<script defer src="/preview/replay.js"></script><script defer src="/preview/bridge.js"></script><script defer src="/runtime/js/jet-api.js">')
    .replace('</head>', '<script defer src="/preview/bootstrap.js"></script></head>')
    .replace('<body>', '<body><aside style="padding:8px 16px;background:#fff7dd;color:#292720;font:13px sans-serif" aria-label="設計預覽工具">' +
      '<strong id="preview-status">正在載入合成設計情境…</strong> ' +
      '<label>固定情境 <select id="preview-scene"><option value="matches">日期與金額（有結果）</option><option value="empty">日期與金額（無結果）</option><option value="stale">資料已變更（結果待更新）</option></select></label> ' +
      '<button id="preview-reload">重新載入</button> <button id="preview-requests">查看最近 action</button>' +
      '<pre id="preview-request-output" hidden style="max-height:180px;overflow:auto;white-space:pre-wrap"></pre></aside>');
}
function asset(base, relative) {
  if (relative.split(/[\\/]/).some(part => part === '..' || part === '.') || path.isAbsolute(relative)) throw new Error('Invalid path.');
  const target = fs.realpathSync(path.resolve(base, relative));
  if (!target.startsWith(fs.realpathSync(base) + path.sep) || !mime[path.extname(target)]) throw new Error('Not a preview resource.');
  return { body: boundedRead(target), type: mime[path.extname(target)] };
}
function createServer() {
  return http.createServer((req, res) => {
    res.setHeader('Cache-Control', 'no-store');
    res.setHeader('X-Content-Type-Options', 'nosniff');
    if (!['GET', 'HEAD'].includes(req.method)) { res.writeHead(405); res.end(); return; }
    if (req.headers.host !== '127.0.0.1:' + serverPort(req)) { res.writeHead(403); res.end(); return; }
    try {
      const route = decodeURIComponent(req.url.split('?')[0]);
      let result;
      if (route === '/') { fixture(); result = { body: previewHtml(), type: mime['.html'] }; }
      else if (route === '/fixtures.json') result = { body: JSON.stringify(fixture()), type: mime['.json'] };
      else if (route.startsWith('/runtime/')) result = asset(product, route.slice(9));
      else if (['/preview/replay.js', '/preview/bridge.js', '/preview/bootstrap.js'].includes(route)) result = asset(__dirname, route.slice(9));
      else { res.writeHead(404); res.end(); return; }
      res.writeHead(200, { 'Content-Type': result.type });
      res.end(req.method === 'HEAD' ? undefined : result.body);
    } catch { res.writeHead(409, { 'Content-Type': 'text/plain; charset=utf-8' }); res.end('預覽資料不可用。請依 docs/development-guide.md 重新產生合成資料，或確認資源路徑。'); }
  });
}
function serverPort(req) { return req.socket.localPort; }
function previewPort(value = process.env.PORT) {
  if (value === undefined || value === '') return 0;
  if (!/^\d+$/.test(value) || Number(value) > 65535) throw new Error('PORT must be an integer from 0 to 65535.');
  return Number(value);
}
if (require.main === module) {
  const port = previewPort();
  fixture();
  fs.mkdirSync(output, { recursive: true });
  fs.writeFileSync(path.join(output, 'index.html'), previewHtml());
  const server = createServer();
  server.listen(port, '127.0.0.1', () => console.log('JET design preview: http://127.0.0.1:' + server.address().port + '/ (expires in 60 minutes; Ctrl+C stops this process)'));
  const deadline = setTimeout(() => server.close(() => process.exit(0)), 60 * 60 * 1000);
  process.on('SIGINT', () => { clearTimeout(deadline); server.close(() => process.exit(0)); });
}
module.exports = { createServer, previewHtml, asset, fixture, previewPort };
