'use strict';
// 前端情境測試的改壞檢查（收尾四項第一項第 0 批，2026-10-13）。
//
// 目的：確認新寫的前端行為測試真的抓得到錯。做法是把正式前端複製一份，在副本上做一處文字替換（改壞），
// 對副本執行指定的測試，測試必須失敗；同一個測試在工作樹的正式前端上必須通過。
// 這支工具只寫 artifacts/frontend-mutation/ 底下的副本，不改工作樹裡的產品檔，所以不會影響其他人同時執行的驗證。
// 它不是 verify.ps1 的正式路線，輸出是診斷紀錄，不是收據。
//
// 資源上限（2026-10-13 起）：同一時間只跑一個改壞檢查，其他人執行時會排隊等候；每次執行 node --test 都有時間上限，
// 逾時就連同它開出的子程序一起結束；測試程序本身另受 resource-guard.cjs 的記憶體上限保護。
// 2026-10-13 曾有幾個各自寫的改壞腳本同時執行、沒有任何上限，測試程序占用數十 GB 記憶體，整台電腦因此停擺，
// 所以改壞檢查一律用這支工具，不另寫腳本。
//
// 用法：
//   node tools/tests/support/mutate-frontend.cjs --spec <改壞清單.json> [--only <編號>] [--wait-seconds <秒數>]
// --wait-seconds 是等候其他改壞檢查結束的上限，預設 1800 秒。
//
// 改壞清單的格式（JSON，UTF-8）：
//   { "batch": "0", "timeoutSeconds": 300, "mutations": [ {   // timeoutSeconds 選填，每次執行 node --test 的上限，預設 300
//       "id": "approval-mapped-requires-column",           // 編號：英數字、點、底線與連字號
//       "file": "js/steps/mapping-step.js",                 // wwwroot 底下的相對路徑
//       "find": "原本的文字", "replace": "改壞後的文字",
//       "count": 1,                                          // 選填，find 必須剛好出現幾次，預設 1
//       "test": "tools/tests/frontend-mapping.test.cjs",    // 儲存庫相對路徑，可以是字串或陣列；也接受還在撰寫的 *.wip.cjs
//       "namePattern": "approval",                          // 選填，交給 node --test-name-pattern
//       "expect": "fail",                                    // 選填，預設 fail；fail 表示副本上的測試必須失敗
//       "timeoutSeconds": 300,                               // 選填，蓋過整份清單的設定
//       "why": "說明這個改壞方式模擬什麼錯誤"                // 選填，寫進結果檔
//   } ] }
//
// 每個改壞方式的副本與輸出放在 artifacts/frontend-mutation/<batch>/<id>/：
//   src/JET/JET/wwwroot/  改壞後的前端副本（不含 assets/ 的字型檔）
//   tools/tests/          測試檔、support/ 與 fixtures/ 的副本，讓寫死相對路徑的舊測試檔也讀到改壞的副本
//   tree.tap              工作樹上的對照執行，必須通過
//   copy.tap              副本改壞之前的對照執行；這裡已經失敗的測試（例如讀了副本沒有的 docs/ 或 C# 檔）不算改壞抓到的
//   mutant.tap            副本改壞之後的執行，必須失敗
//   result.json           三次執行的摘要與判定
// 副本上的執行另外設定 JET_FRONTEND_TEST_WWWROOT 指向副本；工作樹上的對照執行會清掉這個變數。
// 任何一次執行碰到時間、輸出量或記憶體上限，判定都是 inconclusive-resource-limit：要查出是產品還是測試在無限執行或增長，
// 測試或夾具的問題要修，不能重跑了事。
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { spawn, spawnSync } = require('node:child_process');
// 只讀標記與常數，不載入 resource-guard.cjs：那會讓這支工具自己受測試程序的 900 秒上限管，長清單跑到一半就被停掉。
const { MARKER: RESOURCE_GUARD_MARKER, MAX_PARALLEL_TEST_FILES } = require('./resource-guard-marker.cjs');

const REPOSITORY_ROOT = path.resolve(__dirname, '../../..');
const MUTATION_ROOT = path.join(REPOSITORY_ROOT, 'artifacts', 'frontend-mutation');
const SESSION_LOCK = path.join(MUTATION_ROOT, '.session.lock');
const WWWROOT_RELATIVE = path.join('src', 'JET', 'JET', 'wwwroot');
const SAFE_NAME = /^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$/;
const TEST_PATH = /^tools\/tests\/[^/]+\.(?:test|wip)\.cjs$/;
const DEFAULT_TIMEOUT_SECONDS = 300;
const MAX_OUTPUT_BYTES = 64 * 1024 * 1024;

// 在文字副本上套用一處替換；find 出現的次數必須剛好是 count，否則改壞方式沒有套上，直接失敗。
function applyMutation(source, find, replace, count = 1) {
  assert.equal(typeof find, 'string', 'mutation find must be a string');
  assert.ok(find.length > 0, 'mutation find must not be empty');
  assert.equal(typeof replace, 'string', 'mutation replace must be a string');
  assert.notEqual(find, replace, 'mutation replace must differ from find');
  const found = source.split(find).length - 1;
  assert.equal(found, count, 'mutation find text must appear exactly ' + count + ' time(s), found ' + found + ': ' + find);
  return source.split(find).join(replace);
}

// 讀 node --test 的 TAP 摘要。
function parseTapSummary(text) {
  const read = key => { const match = text.match(new RegExp('^# ' + key + ' (\\d+)$', 'm')); return match ? Number(match[1]) : null; };
  const failed = [...text.matchAll(/^\s*not ok \d+ - (.+)$/gm)].map(match => match[1].trim());
  return { tests: read('tests'), pass: read('pass'), fail: read('fail'), skipped: read('skipped'), cancelled: read('cancelled'), failed };
}

// 改壞副本的目錄。批次與編號只接受安全字元，結果必定落在 artifacts/frontend-mutation/ 底下。
function mutationWorkDirectory(batch, id, mutationRoot = MUTATION_ROOT) {
  assert.ok(SAFE_NAME.test(String(batch)), 'batch must be a safe name: ' + batch);
  assert.ok(SAFE_NAME.test(String(id)), 'mutation id must be a safe name: ' + id);
  const directory = path.resolve(mutationRoot, String(batch), String(id));
  assert.ok(directory.startsWith(path.resolve(mutationRoot) + path.sep), 'mutation directory must stay under ' + mutationRoot);
  return directory;
}

// 能交給改壞檢查的測試檔：tools/tests/ 第一層的 *.test.cjs，以及還在撰寫、尚未收進正式路線的 *.wip.cjs。
function isMutationTestPath(test) {
  return TEST_PATH.test(String(test).replace(/\\/g, '/'));
}

// 一次執行碰到的資源上限：逾時、輸出量超過上限，或測試程序被 resource-guard.cjs 停掉。沒有碰到時回傳 null。
function resourceLimitOf(run) {
  if (run.limit) return run.limit;
  return run.text.includes(RESOURCE_GUARD_MARKER) ? 'resource-guard' : null;
}

// 改壞後才失敗的測試：副本在改壞之前就失敗的（例如測試讀了副本沒有複製的 docs/ 或 C# 檔），不算改壞抓到的。
function newFailures(copy, mutant) {
  const before = new Set(copy.failed);
  return mutant.failed.filter(name => !before.has(name));
}

// 判定：工作樹上的對照執行必須全部通過而且至少跑到一個測試；改壞後才失敗的測試至少一個才算抓到。
// 副本在改壞之前就有失敗時照樣判定，但這些測試不列入抓到的清單，結果檔另外列出。
// 任何一次執行碰到資源上限時，不論其他結果，一律是 inconclusive-resource-limit。
function verdict(tree, copy, mutant, expect = 'fail', limits = {}) {
  if (limits.tree || limits.copy || limits.mutant) return 'inconclusive-resource-limit';
  if (!tree.tests || tree.fail !== 0) return 'inconclusive-tree-failed';
  if (!copy.tests || !mutant.tests) return 'inconclusive-no-tests';
  const caught = newFailures(copy, mutant);
  if (expect === 'fail') return caught.length > 0 ? 'killed' : 'survived';
  return caught.length === 0 ? 'passed-as-expected' : 'failed-unexpectedly';
}

function copyTree(from, to, skip = () => false) {
  fs.cpSync(from, to, { recursive: true, filter: source => !skip(source) });
}

// 結束一個程序和它開出的所有子程序。node --test 會為每個測試檔另開子程序，只結束外層會留下仍在執行的測試程序。
function killProcessTree(pid) {
  if (!pid) return;
  if (process.platform === 'win32') {
    spawnSync('taskkill', ['/PID', String(pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' });
    return;
  }
  try { process.kill(pid, 'SIGKILL'); } catch { /* already gone */ }
}

// 一個改壞方式可以指定好幾個測試檔；同時執行的檔數照 FrontendMapping 的上限，合計記憶體才不會隨核心數放大。
function nodeTestArgs(files, namePattern) {
  const args = ['--test', '--test-reporter=tap', '--test-concurrency=' + MAX_PARALLEL_TEST_FILES];
  if (namePattern) args.push('--test-name-pattern=' + namePattern);
  return args.concat(files);
}

function runNodeTests(cwd, files, namePattern, env, timeoutSeconds) {
  const args = nodeTestArgs(files, namePattern);
  return new Promise(resolve => {
    const child = spawn(process.execPath, args, { cwd, env, windowsHide: true });
    const chunks = { stdout: [], stderr: [] };
    let bytes = 0, limit = null;
    const stop = reason => { if (limit) return; limit = reason; killProcessTree(child.pid); };
    for (const stream of ['stdout', 'stderr']) {
      child[stream].on('data', chunk => {
        bytes += chunk.length;
        if (bytes > MAX_OUTPUT_BYTES) { stop('output-limit'); return; }
        chunks[stream].push(chunk);
      });
    }
    const timer = setTimeout(() => stop('timeout'), timeoutSeconds * 1000);
    let settled = false;
    const finish = (exitCode, extra) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      const stdout = Buffer.concat(chunks.stdout).toString('utf8'), stderr = Buffer.concat(chunks.stderr).toString('utf8') + (extra || '');
      resolve({ exitCode, limit, text: stdout + (stderr ? '\n# stderr\n' + stderr : '') });
    };
    child.on('error', error => finish(null, String(error && error.stack || error)));
    child.on('close', code => finish(code));
  });
}

function timeoutFor(spec, mutation) {
  const value = Number(mutation.timeoutSeconds ?? spec.timeoutSeconds ?? DEFAULT_TIMEOUT_SECONDS);
  assert.ok(Number.isFinite(value) && value >= 10 && value <= 1800, mutation.id + ': timeoutSeconds must be between 10 and 1800');
  return value;
}

async function runMutation(spec, mutation) {
  const batch = spec.batch, id = mutation.id, tests = [].concat(mutation.test || []);
  assert.ok(tests.length > 0, id + ': at least one test file is required');
  for (const test of tests) {
    assert.ok(isMutationTestPath(test), id + ': test must be a tools/tests/*.test.cjs or tools/tests/*.wip.cjs file: ' + test);
    assert.ok(fs.existsSync(path.join(REPOSITORY_ROOT, test)), id + ': test file not found: ' + test);
  }
  const timeoutSeconds = timeoutFor(spec, mutation);
  const relativeFile = String(mutation.file || '').replace(/\\/g, '/');
  assert.ok(relativeFile && !relativeFile.split('/').includes('..') && !path.isAbsolute(relativeFile), id + ': file must be relative to wwwroot');
  const work = mutationWorkDirectory(batch, id);
  fs.rmSync(work, { recursive: true, force: true });
  const wwwroot = path.join(work, WWWROOT_RELATIVE);
  const assets = path.join(REPOSITORY_ROOT, WWWROOT_RELATIVE, 'assets');
  copyTree(path.join(REPOSITORY_ROOT, WWWROOT_RELATIVE), wwwroot, source => source === assets || source.startsWith(assets + path.sep));
  copyTree(path.join(REPOSITORY_ROOT, 'tools', 'tests'), path.join(work, 'tools', 'tests'),
    source => fs.statSync(source).isFile() && !/\.(?:cjs|json)$/.test(source));
  const target = path.join(wwwroot, relativeFile);
  assert.ok(target.startsWith(wwwroot + path.sep) && fs.existsSync(target), id + ': mutated file not found in the copy: ' + relativeFile);
  const original = fs.readFileSync(target, 'utf8');
  const mutated = applyMutation(original, mutation.find, mutation.replace, mutation.count ?? 1);

  const cleanEnv = { ...process.env };
  delete cleanEnv.JET_FRONTEND_TEST_WWWROOT;
  const copyEnv = { ...cleanEnv, JET_FRONTEND_TEST_WWWROOT: wwwroot };
  const tree = await runNodeTests(REPOSITORY_ROOT, tests, mutation.namePattern, cleanEnv, timeoutSeconds);
  const copy = await runNodeTests(work, tests, mutation.namePattern, copyEnv, timeoutSeconds);
  fs.writeFileSync(target, mutated);
  const mutant = await runNodeTests(work, tests, mutation.namePattern, copyEnv, timeoutSeconds);
  fs.writeFileSync(path.join(work, 'tree.tap'), tree.text);
  fs.writeFileSync(path.join(work, 'copy.tap'), copy.text);
  fs.writeFileSync(path.join(work, 'mutant.tap'), mutant.text);
  const treeSummary = parseTapSummary(tree.text), copySummary = parseTapSummary(copy.text), mutantSummary = parseTapSummary(mutant.text);
  const limits = { tree: resourceLimitOf(tree), copy: resourceLimitOf(copy), mutant: resourceLimitOf(mutant) };
  const result = { batch: String(batch), id, why: mutation.why || null, file: relativeFile, find: mutation.find, replace: mutation.replace,
    tests, namePattern: mutation.namePattern || null, expect: mutation.expect || 'fail', timeoutSeconds,
    tree: treeSummary, copy: copySummary, mutant: mutantSummary, limits,
    caughtBy: newFailures(copySummary, mutantSummary), copyOnlyFailures: copySummary.failed,
    verdict: verdict(treeSummary, copySummary, mutantSummary, mutation.expect || 'fail', limits),
    finishedAt: new Date().toISOString() };
  fs.writeFileSync(path.join(work, 'result.json'), JSON.stringify(result, null, 2) + '\n');
  return result;
}

function isProcessAlive(pid) {
  try { process.kill(pid, 0); return true; } catch (error) { return error.code === 'EPERM'; }
}

function readLockHolder() {
  try { return JSON.parse(fs.readFileSync(SESSION_LOCK, 'utf8')); } catch { return null; }
}

// 同一時間只跑一個改壞檢查。鎖檔記下持有者的程序編號；持有者已經不在時視為殘留，直接取代。
async function acquireSessionLock(waitSeconds) {
  fs.mkdirSync(MUTATION_ROOT, { recursive: true });
  const deadline = Date.now() + waitSeconds * 1000;
  let announced = false;
  for (;;) {
    try {
      fs.writeFileSync(SESSION_LOCK, JSON.stringify({ pid: process.pid, startedAt: new Date().toISOString() }) + '\n', { flag: 'wx' });
      break;
    } catch (error) {
      if (error.code !== 'EEXIST') throw error;
      const holder = readLockHolder();
      if (!holder || !Number.isInteger(holder.pid) || !isProcessAlive(holder.pid)) { fs.rmSync(SESSION_LOCK, { force: true }); continue; }
      if (Date.now() >= deadline) {
        throw new Error('another mutation session (pid ' + holder.pid + ') is still running after waiting ' + waitSeconds + ' s; lock file: ' + SESSION_LOCK);
      }
      if (!announced) { process.stderr.write('waiting for the mutation session of pid ' + holder.pid + ' to finish\n'); announced = true; }
      await new Promise(resolve => setTimeout(resolve, 2000));
    }
  }
  process.on('exit', () => {
    const holder = readLockHolder();
    if (holder && holder.pid === process.pid) fs.rmSync(SESSION_LOCK, { force: true });
  });
}

async function main(argv) {
  const specIndex = argv.indexOf('--spec'), onlyIndex = argv.indexOf('--only'), waitIndex = argv.indexOf('--wait-seconds');
  if (specIndex < 0 || !argv[specIndex + 1]) {
    process.stderr.write('usage: node tools/tests/support/mutate-frontend.cjs --spec <mutations.json> [--only <id>] [--wait-seconds <seconds>]\n');
    return 3;
  }
  const spec = JSON.parse(fs.readFileSync(path.resolve(argv[specIndex + 1]), 'utf8'));
  const only = onlyIndex >= 0 ? argv[onlyIndex + 1] : null;
  const waitSeconds = waitIndex >= 0 ? Number(argv[waitIndex + 1]) : 1800;
  assert.ok(Number.isFinite(waitSeconds) && waitSeconds >= 0, '--wait-seconds must be a non-negative number');
  const mutations = (spec.mutations || []).filter(mutation => !only || mutation.id === only);
  assert.ok(mutations.length > 0, 'no mutation selected');
  assert.equal(new Set(mutations.map(mutation => mutation.id)).size, mutations.length, 'mutation ids must be unique');
  await acquireSessionLock(waitSeconds);
  let ok = true;
  for (const mutation of mutations) {
    const result = await runMutation(spec, mutation);
    const good = result.verdict === 'killed' || result.verdict === 'passed-as-expected';
    ok = ok && good;
    const limitNote = Object.entries(result.limits).filter(([, value]) => value).map(([run, value]) => run + ' ' + value).join(', ');
    process.stdout.write([result.verdict, result.id, 'tree ' + result.tree.pass + '/' + result.tree.tests, 'copy ' + result.copy.pass + '/' + result.copy.tests,
      'mutant fail ' + result.mutant.fail + '/' + result.mutant.tests, 'caught by ' + result.caughtBy.length + ': ' + result.caughtBy.slice(0, 3).join(' | ')]
      .concat(limitNote ? ['limits: ' + limitNote] : []).join('\t') + '\n');
  }
  process.stdout.write('outputs: ' + path.relative(REPOSITORY_ROOT, path.join(MUTATION_ROOT, String(spec.batch))) + '\n');
  return ok ? 0 : 1;
}

module.exports = { applyMutation, parseTapSummary, mutationWorkDirectory, isMutationTestPath, resourceLimitOf, newFailures, verdict, nodeTestArgs, MUTATION_ROOT };

if (require.main === module) {
  main(process.argv.slice(2)).then(
    code => { process.exitCode = code; },
    error => { process.stderr.write(String(error && error.stack || error) + '\n'); process.exitCode = 2; });
}
