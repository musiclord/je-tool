'use strict';
// 前端改壞工具 support/mutate-frontend.cjs 的自我測試，以及前端測試同時執行的檔數上限要放得進 tools/harness/lanes.json 的驗證步驟上限。
// 這三個測試原本在 frontend-support.test.cjs；前端測試搬到 src/JET/tests/frontend 時拆到這裡，因為它們測的是 tools 底下的驗證工具與設定。
// 註解照搬沒有改寫。全部使用程式生成的合成資料。
require('../../src/JET/tests/frontend/support/resource-guard.cjs');
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const mutation = require('./support/mutate-frontend.cjs');

test('mutation tool applies exactly the requested change, stays under its report folder and only counts new failures', () => {
  assert.equal(mutation.applyMutation('a();b();', 'b();', 'c();'), 'a();c();');
  assert.equal(mutation.applyMutation('x x', 'x', 'y', 2), 'y y');
  assert.throws(() => mutation.applyMutation('a();', 'missing', 'x'), /exactly 1 time/);
  assert.throws(() => mutation.applyMutation('x x', 'x', 'y'), /found 2/);
  assert.throws(() => mutation.applyMutation('a', 'a', 'a'), /must differ/);
  const base = path.join(__dirname, 'synthetic-mutation-root');
  assert.equal(mutation.mutationWorkDirectory('0', 'A01', base), path.join(base, '0', 'A01'));
  assert.throws(() => mutation.mutationWorkDirectory('0', '../escape', base), /safe name/);
  assert.throws(() => mutation.mutationWorkDirectory('..', 'A01', base), /safe name/);
  const tap = 'ok 1 - kept\nnot ok 2 - layout only\nnot ok 3 - caught\n# tests 3\n# pass 1\n# fail 2\n';
  const summary = mutation.parseTapSummary(tap);
  assert.deepEqual([summary.tests, summary.pass, summary.fail], [3, 1, 2]);
  const copy = mutation.parseTapSummary('ok 1 - kept\nnot ok 2 - layout only\nok 3 - caught\n# tests 3\n# pass 2\n# fail 1\n');
  const tree = mutation.parseTapSummary('ok 1 - kept\nok 2 - layout only\nok 3 - caught\n# tests 3\n# pass 3\n# fail 0\n');
  assert.deepEqual(mutation.newFailures(copy, summary), ['caught']);
  assert.equal(mutation.verdict(tree, copy, summary), 'killed');
  assert.equal(mutation.verdict(tree, copy, copy), 'survived', 'failures that already happen before mutating are not credited');
  assert.equal(mutation.verdict(summary, copy, summary), 'inconclusive-tree-failed');
  // 2026-10-13 起：還在撰寫的 *.wip.cjs 也能做改壞檢查，不必另寫腳本；碰到時間、輸出量或記憶體上限的執行不算抓到。
  assert.ok(mutation.isMutationTestPath('tools/tests/frontend-journey-tables.wip.cjs'));
  assert.ok(mutation.isMutationTestPath('tools\\tests\\frontend-workflow.test.cjs'));
  assert.ok(!mutation.isMutationTestPath('tools/tests/support/frontend-fixtures.cjs'));
  assert.ok(!mutation.isMutationTestPath('tools/tests/nested/frontend-x.test.cjs'));
  const { MARKER } = require('../../src/JET/tests/frontend/support/resource-guard.cjs');
  assert.equal(mutation.resourceLimitOf({ limit: null, text: 'not ok 1 - x\n# ' + MARKER + ' memory 2100 MB exceeded the 2048 MB limit' }), 'resource-guard');
  assert.equal(mutation.resourceLimitOf({ limit: 'timeout', text: '' }), 'timeout');
  assert.equal(mutation.resourceLimitOf({ limit: null, text: summary.failed.join('\n') }), null);
  assert.equal(mutation.verdict(tree, copy, summary, 'fail', { mutant: 'resource-guard' }), 'inconclusive-resource-limit',
    'a mutant that only stops at the resource limit is investigated, not credited');
  assert.equal(mutation.verdict(tree, copy, summary, 'fail', { tree: 'timeout' }), 'inconclusive-resource-limit');
});

test('frontend test runs cap how many test files run at once, so the total stays within the verification step limit', () => {
  // 2026-10-14：每個測試程序各有上限，但 node --test 預設依 CPU 核心數同時執行，合計記憶體會隨核心數放大。
  const { MAX_PARALLEL_TEST_FILES } = require('../../src/JET/tests/frontend/support/resource-guard-marker.cjs');
  const { DEFAULT_MAX_RSS_MB } = require('../../src/JET/tests/frontend/support/resource-guard.cjs');
  const lanes = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'harness', 'lanes.json'), 'utf8'));
  assert.ok(Number.isInteger(MAX_PARALLEL_TEST_FILES) && MAX_PARALLEL_TEST_FILES >= 1);
  assert.ok(MAX_PARALLEL_TEST_FILES * DEFAULT_MAX_RSS_MB <= lanes.limits.maximumProcessTreeMemoryMegabytes,
    'test files running at once, each at its own limit, must fit inside the verification step limit');
  const mutation = require('./support/mutate-frontend.cjs');
  const args = mutation.nodeTestArgs(['tools/tests/frontend-a.test.cjs', 'tools/tests/frontend-b.test.cjs'], 'approval');
  assert.ok(args.includes('--test-concurrency=' + MAX_PARALLEL_TEST_FILES), args.join(' '));
  assert.deepEqual(args.slice(-2), ['tools/tests/frontend-a.test.cjs', 'tools/tests/frontend-b.test.cjs']);
});

test('mutation tool itself runs outside the test-process limits, so a long list is not stopped halfway', () => {
  // 2026-10-13 一次跑整份改壞清單時，工具載入了測試程序的資源上限，在第 900 秒把自己停掉，只完成 37 個。
  // 每次執行 node --test 已有工具自己的逾時與子程序清理，測試程序也各自載入上限；工具程序本身只讀標記，不啟動上限。
  const toolPath = path.join(__dirname, 'support', 'mutate-frontend.cjs');
  const { MARKER } = require('../../src/JET/tests/frontend/support/resource-guard.cjs');
  const env = { ...process.env };
  delete env.JET_TEST_MAX_RSS_MB;
  delete env.JET_TEST_MAX_SECONDS;
  const probe = spawnSync(process.execPath, ['-e', 'require(' + JSON.stringify(toolPath) + ');\n' +
    "setTimeout(() => console.log('still running', typeof globalThis.__jetTestResourceGuard), 2500);"],
    { env: { ...env, JET_TEST_MAX_SECONDS: '1' }, encoding: 'utf8', timeout: 30000 });
  assert.equal(probe.status, 0, probe.stderr);
  assert.ok(!probe.stderr.includes(MARKER), probe.stderr);
  assert.equal(probe.stdout.trim(), 'still running undefined');
});

test('mutation tool accepts the frontend tests under src/JET/tests/frontend and still accepts the ones left in tools/tests', () => {
  assert.ok(mutation.isMutationTestPath('src/JET/tests/frontend/frontend-workflow.test.cjs'));
  assert.ok(mutation.isMutationTestPath('src\\JET\\tests\\frontend\\frontend-journey-tables.wip.cjs'));
  assert.ok(mutation.isMutationTestPath('tools/tests/frontend-development-repository.test.cjs'));
  assert.ok(!mutation.isMutationTestPath('src/JET/tests/frontend/support/frontend-fixtures.cjs'));
  assert.ok(!mutation.isMutationTestPath('src/JET/tests/frontend/nested/frontend-x.test.cjs'));
  assert.ok(!mutation.isMutationTestPath('src/JET/tests/JET.Tests/frontend-x.test.cjs'));
});
