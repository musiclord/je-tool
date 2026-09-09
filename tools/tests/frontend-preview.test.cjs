'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const vm = require('node:vm');
const { dispatch } = require('../harness/frontend-preview/replay.js');
const { createServer, previewHtml, previewPort } = require('../harness/frontend-preview/server.cjs');
const root = path.resolve(__dirname, '../..');
const scenario = { name: 'Synthetic', groups: [{ rules: [{ type: 'fieldValue', field: 'amount', operator: 'greaterThan', value: '200' }] }] };
const bundle = { fixtures: [{ scenario, preview: { scenario: { count: 2 } }, page: { queryRevision: 'v1', rows: [] }, detail: { rows: [] } }] };

test('desktop preview configuration binds the same loopback port as its browser URL', () => {
  const config = JSON.parse(fs.readFileSync(path.join(root, '.claude/launch.json'), 'utf8')).configurations[0];
  assert.equal(config.program, 'tools/harness/frontend-preview/server.cjs');
  assert.equal(config.url, 'http://127.0.0.1:' + config.port);
  assert.equal(previewPort(config.env.PORT), config.port);
  assert.equal(config.autoPort, false);
  assert.equal(previewPort(''), 0);
  assert.equal(previewPort('0'), 0);
  assert.equal(previewPort('65535'), 65535);
  for (const value of ['-1', '65536', 'abc', '4243x', '1.5', ' ', '0x1093']) {
    assert.throws(() => previewPort(value));
  }
});
test('prepared action matches exactly; changes never receive a fabricated answer', () => {
  const payload = { populationScope: 'auditPeriod', scenario };
  assert.equal(dispatch(bundle, 'filter.preview', payload).scenario.count, 2);
  assert.equal(dispatch(bundle, 'filter.preview', { ...payload, scenario: { ...scenario, name: 'Renamed' } }).scenario.count, 2);
  const changed = structuredClone(payload); changed.scenario.groups[0].rules[0].value = '201';
  assert.throws(() => dispatch(bundle, 'filter.preview', changed));
  assert.throws(() => dispatch(bundle, 'filter.commit', payload));
  assert.throws(() => dispatch(bundle, 'export.workpaperStream', {}));
  assert.throws(() => dispatch(bundle, 'query.filterVoucherPage', { ...payload, pageSize: 50, search: 'A' }));
  assert.throws(() => dispatch(bundle, 'query.filterVoucherPage', { ...payload, pageSize: 50, queryRevision: 'old' }));
  assert.throws(() => dispatch(bundle, 'filter.preview', { ...payload, unexpected: true }));
  const result = dispatch(bundle, 'filter.preview', payload); result.scenario.count = 999;
  assert.equal(dispatch(bundle, 'filter.preview', payload).scenario.count, 2);
});
test('production JetApi receives success and rejects unprepared action through preview bridge', async () => {
  const window = { JetPreviewReplay: { dispatch }, crypto, setTimeout, clearTimeout };
  const context = vm.createContext({ window, fetch: async () => ({ ok: true, json: async () => bundle }), structuredClone });
  vm.runInContext(fs.readFileSync(path.join(root, 'tools/harness/frontend-preview/bridge.js'), 'utf8'), context);
  vm.runInContext(fs.readFileSync(path.join(root, 'src/JET/JET/wwwroot/js/jet-api.js'), 'utf8'), context);
  assert.equal(window.JetApi.isReady(), true);
  assert.equal((await window.JetApi.filterPreview({ populationScope: 'auditPeriod', scenario })).scenario.count, 2);
  await assert.rejects(window.JetApi.filterCommit({ scenarios: [scenario] }), /設計預覽未提供/);
});
test('page reuses current production scripts and does not install preview into product', () => {
  const html = previewHtml();
  assert.ok(html.indexOf('/preview/bridge.js') < html.indexOf('/runtime/js/jet-api.js'));
  assert.ok(html.includes('/runtime/js/steps/filter-step.js'));
  assert.ok(html.includes('/runtime/css/app.css'));
  const productIndex = fs.readFileSync(path.join(root, 'src/JET/JET/wwwroot/index.html'), 'utf8');
  assert.ok(!productIndex.includes('/preview/'));
  assert.ok(!fs.existsSync(path.join(root, 'src/JET/JET/wwwroot/fixtures.json')));
});
test('server exposes only preview resources, never repository or write endpoints', async () => {
  const server = createServer();
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port;
  const request = (route, method = 'GET', host = '127.0.0.1:' + port) => new Promise((resolve, reject) => {
    const req = http.request({ host: '127.0.0.1', port, path: route, method, headers: { host } }, res => { res.resume(); res.on('end', () => resolve(res.statusCode)); });
    req.on('error', reject); req.end();
  });
  try {
    assert.equal(await request('/runtime/css/app.css'), 200);
    assert.equal(await request('/preview/replay.js'), 200);
    assert.equal(await request('/.git/config'), 404);
    assert.equal(await request('/runtime/%2e%2e/%2e%2e/AGENTS.md'), 409);
    assert.equal(await request('/runtime/css/app.css', 'POST'), 405);
    assert.equal(await request('/runtime/css/app.css', 'GET', 'example.com'), 403);
  } finally { await new Promise(resolve => server.close(resolve)); }
});
