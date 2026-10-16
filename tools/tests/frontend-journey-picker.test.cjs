'use strict';
// 情境 1：案件清單（收尾四項第一項第 10 批，2026-10-14）。
//
// 審計員的操作：開啟 JET 看到本機案件清單；按「重新整理本機」；按「同步線上案件」，成功與失敗；刪除一個案件，失敗、成功、
// 資料夾沒刪乾淨；開一個會失敗的案件後輸出支援日誌；清單還在讀取時想開案或新增案件；按「退出」但視窗關不掉，回到案件清單；
// 開著 JET 切到其他程式再回來。
// 同步線上案件時按「取消作業」送出的是同一個請求，在 frontend-journey-operations 的情境 22 測試。
//
// 夾具：支援模組的 fullAppFixture，經 bootFullApp（本檔叫 boot）開啟 JET 停在案件清單：
// 整個 index.html 的 body 標記與全部前端腳本，正式的 jet-api.js 經假的 chrome.webview 收發，app.js 照瀏覽器執行正式的 init()。
// 計畫原本寫用 remapAppFixture；它把 app.js 切成幾段載入、不跑 init()，案件清單第一次讀取與 jet-api.js 的請求都看不到，所以改用整個畫面。
// 第 10 批時本檔、frontend-journey-export 與 -operations 各放一份相同的副本，第 11 批（2026-10-14）收進 support/frontend-fixtures.cjs。
//
// 取代的 C# 字串測試（src/JET/tests/JET.Tests/Architecture/），都已在 2026-10-14 第二、三波整併刪除；
// Picker_HasVisibleInlineFeedback_… 與 PickerDelete_… 的第一次失敗收據在第 11 批（2026-10-14）補進本檔對應的測試上方：
//   FrontendHardeningContractTests.Picker_HasVisibleInlineFeedback_AndAllFailureEntrypointsUseIt
//   ProjectDeletionFrontendTests.PickerDelete_DeclaresSuccessOnlyRefreshInsideRunPipeline
//   ProjectListLocalFirstFrontendTests.Picker_UsesLocalActionForBootstrapAndRefresh_AndOnlineActionOnlyForManualSync
//   ProjectListLocalFirstFrontendTests.Picker_InvalidatesLateListResponsesBeforeLeavingOrOpeningAProject
//
// 逐字比對的只有使用者確認過的句子，出處寫在旁邊；「重新整理本機」「同步線上案件」這類按鈕文字查不到使用者裁定，
// 按鈕用 data-action 找，文字只比對意思（計畫第 4 節 B24）。預期值一律寫死；action payload 用 deepEqual 精確比對；
// 斷言的失敗值不是假 DOM 節點（節點只用 === 轉成布林）。
// 作業名稱（Ui.run 的 label）只有「讀取案件清單」（勾選頁確認 L01）、「開啟案件」（L03）與「刪除案件」（2026-10-13 裁定）是使用者確認過的，
// 其他作業名稱放在確認過的句型裡時只比對意思。
// 每個測試都有 30 秒上限：改壞檢查時若有請求永遠等不到回應，測試直接失敗，不會拖到改壞工具的時間上限（2026-10-14 第 10 批補）。
const nodeTest = require('node:test');
const test = (name, fn) => nodeTest(name, { timeout: 30000 }, fn);
const assert = require('node:assert/strict');
const { plain, flush, bootFullApp: boot, noDunderKeys } = require('./support/frontend-fixtures.cjs');

const LOCAL_A = { projectId: '合成甲案', entityName: '合成客戶甲', databaseProvider: 'sqlite' };
const LOCAL_B = { projectId: '合成乙案', entityName: '合成客戶乙', databaseProvider: 'duckdb' };
const ONLINE_C = { projectId: '合成線上丙案', entityName: '合成客戶丙', databaseProvider: 'sqlServer' };

const rowNames = (f, list) => f.dom.content.querySelectorAll('[data-motion-list="' + list + '"] .project-row__title').map(node => node.textContent);
// 清單上方的錯誤框：標題（只比對意思）與內文分開讀。
const errorBox = f => {
  const box = f.$('.picker-feedback');
  if (box === null) return null;
  const title = box.querySelector('.picker-feedback__title'), body = box.querySelector('.picker-feedback__body');
  return { role: box.getAttribute('role'), title: title === null ? null : title.textContent, body: body === null ? null : body.textContent };
};

test('journey 1: JET starts from the local list only, syncing adds the online cases, and refreshing asks only for the local list again', async () => {
  const f = await boot([LOCAL_A, LOCAL_B]);
  // 啟動時只讀本機清單，不碰線上。
  assert.deepEqual(f.posted.map(r => r.action), ['system.ping', 'system.databaseInfo', 'system.whoAmI', 'project.listLocal']);
  assert.deepEqual(plain(f.request('project.listLocal').payload), {});
  assert.deepEqual(rowNames(f, 'local'), ['合成甲案', '合成乙案']);
  // 線上區還沒同步（只比對意思）。
  assert.match(f.$('[data-motion-list="online"]').textContent, /尚未同步/);
  // 兩個按鈕各自說明是本機與線上（只比對意思，計畫第 4 節 B24）。
  assert.match(f.$('[data-action="picker-refresh-local"]').textContent, /本機/);
  assert.match(f.$('[data-action="picker-sync-online"]').textContent, /線上/);

  f.click('[data-action="picker-sync-online"]');
  const sync = f.request('project.list');
  assert.deepEqual(plain(sync.payload), {});
  f.reply(sync, { projects: [LOCAL_A, LOCAL_B, ONLINE_C], online: { reachable: true, principal: '合成帳號' } }); await flush();
  assert.deepEqual(rowNames(f, 'online'), ['合成線上丙案']);
  assert.deepEqual(rowNames(f, 'local'), ['合成甲案', '合成乙案']);

  // 先讓同步失敗留下錯誤，再按重新整理本機：錯誤立刻清掉，只送本機清單，線上區回到還沒同步。
  f.click('[data-action="picker-sync-online"]');
  f.fail('project.list', { code: 'synthetic', message: '合成：連不上線上資料庫' }); await flush();
  assert.notEqual(errorBox(f), null);
  const before = f.sent('project.list').length;
  f.click('[data-action="picker-refresh-local"]');
  assert.equal(errorBox(f), null, 'the old error is cleared as soon as the refresh starts');
  assert.equal(f.sent('project.list').length, before, 'refreshing does not ask for online cases');
  f.reply('project.listLocal', { projects: [LOCAL_B] }); await flush();
  assert.deepEqual(rowNames(f, 'local'), ['合成乙案']);
  assert.deepEqual(rowNames(f, 'online'), []);
  assert.match(f.$('[data-motion-list="online"]').textContent, /尚未同步/);
  assert.equal(f.sent('project.listLocal').length, 2);
});

test('journey 1: a failed online sync keeps the local rows and puts the reason above the lists as an alert', async () => {
  const f = await boot([LOCAL_A]);
  f.click('[data-action="picker-sync-online"]');
  f.clock.advance(2500);
  f.fail('project.list', { code: 'synthetic', message: '合成：連不上線上資料庫' }, 'sync-correlation'); await flush();
  assert.deepEqual(rowNames(f, 'local'), ['合成甲案']);
  // 失敗句：使用者 2026-10-13 勾選頁確認 19（final-wording.json），2026-10-14 第三輪 R3-27 括號後加句號；
  // 「同步線上案件」這個作業名稱沒有使用者裁定，只確認提到線上。案件清單看不到訊息面板，同一句寫在清單上方的錯誤框。
  const box = errorBox(f);
  assert.equal(box.role, 'alert');
  assert.match(box.title, /無法/, 'the box says the operation could not be completed');
  const sentence = box.body.match(/^(.+)失敗：合成：連不上線上資料庫（耗時 2\.5 秒）。$/);
  assert.ok(sentence !== null, 'the confirmed failure sentence: ' + box.body);
  assert.match(sentence[1], /線上/);
  assert.deepEqual(f.messages()[0], { text: box.body, level: 'error' });
  // 錯誤框在兩個清單之前，而且框裡沒有按鈕（使用者 2026-10-13 選「從錯誤框拿掉，改成一般工具」）。
  const panel = f.$('.picker-panel').children;
  assert.ok(panel.findIndex(node => node.className === 'picker-feedback') < panel.findIndex(node => node.className === 'picker-section'),
    'the error box comes before the case lists');
  assert.ok(f.$('.picker-feedback button') === null, 'no buttons inside the error box');
  // 案件清單不是步驟畫面，不加步驟提示框。
  assert.equal(f.state().operationFailure, null);
});

// 取代 ProjectDeletionFrontendTests.PickerDelete_DeclaresSuccessOnlyRefreshInsideRunPipeline 的刪除流程。原方法註解：使用者 2026-10-13 選擇把案件清單的「專案」
// 統一改稱「案件」，執行名稱跟著從「刪除專案」改成「刪除案件」；改字前的失敗收據 20261013-032847428-7aa20114d4404ec7b5b0892aa50d7275。
test('journey 1: deleting a case reloads the local list once only after it succeeds, the row disappears and focus moves to the next case', async () => {
  const f = await boot([LOCAL_A, LOCAL_B]);
  f.click('[data-action="picker-delete"][data-project-id="合成甲案"]');
  assert.equal(f.$('[data-bind="delete-modal"]').hidden, false);
  // 確認框的標題是「刪除案件」：使用者 2026-10-13 選擇把案件清單的「專案」改稱「案件」。
  assert.equal(f.$('#delete-modal-title').textContent, '刪除案件');
  assert.deepEqual(plain(f.request('project.deletePreview').payload), { projectId: '合成甲案' });
  f.reply('project.deletePreview', { reportCount: 1, workpaperCount: 2 }); await flush();
  const listsBefore = f.sent('project.listLocal').length;
  f.click('[data-action="modal-confirm"]');
  const remove = f.request('project.delete');
  assert.deepEqual(plain(remove.payload), { projectId: '合成甲案' });
  assert.ok(noDunderKeys(remove.payload));
  assert.equal(f.state().busy, true);
  assert.equal(f.sent('project.listLocal').length, listsBefore, 'no list request before the deletion answers');
  f.reply(remove, {}); await flush();
  // 刪除成功後才讀清單，而且是在忙碌解除之後，只讀一次本機清單。
  assert.equal(f.sent('project.listLocal').length, listsBefore + 1);
  assert.equal(f.sent('project.list').length, 0);
  assert.equal(f.state().busyLabel, '讀取案件清單');
  f.reply('project.listLocal', { projects: [LOCAL_B] }); await flush();
  assert.deepEqual(rowNames(f, 'local'), ['合成乙案']);
  assert.equal(f.state().busy, false);
  // 使用者 2026-10-13 勾選頁確認 1（final-wording.json，維持原句）。
  assert.deepEqual(f.messages()[0], { text: '已刪除案件「合成甲案」。', level: 'info' });
  assert.equal(errorBox(f), null);
  // 被刪的那一列不在了，焦點交給下一個案件的開啟鈕。
  f.timers.run();
  const active = f.dom.document.activeElement;
  assert.ok(active !== null && active === f.$('[data-action="picker-open"][data-project-id="合成乙案"]'), 'focus lands on the next case');
});

// 取代 FrontendHardeningContractTests.Picker_HasVisibleInlineFeedback_AndAllFailureEntrypointsUseIt 的琥珀色等級，原方法註解：使用者 2026-10-13 裁定訊息分三級，
// 案件清單的提示多一個等級（刪除案件後資料夾沒刪乾淨時是琥珀色），第一次失敗收據 20261013-095534301-f71b2166815146bd8ac37239a22b0472。
// 也取代 ProjectDeletionFrontendTests.PickerDelete_DeclaresSuccessOnlyRefreshInsideRunPipeline 的提醒部分，原方法註解：資料夾沒刪乾淨的提醒要在清單重新讀回後
// 寫在案件清單上方，所以 refresh 先讀回清單，再補上這則琥珀色提醒；仍只在刪除成功後執行。第一次失敗收據 20261013-095826239-64f6bb236a924aadbdd241ba19d07625。
test('journey 1: when the case folder is not fully removed the list reloads and an amber note above it repeats the backend sentence', async () => {
  const f = await boot([LOCAL_A, LOCAL_B]);
  f.click('[data-action="picker-delete"][data-project-id="合成甲案"]');
  f.reply('project.deletePreview', { reportCount: 0, workpaperCount: 0 }); await flush();
  f.click('[data-action="modal-confirm"]');
  f.reply('project.delete', { message: '合成：資料夾還有檔案' }); await flush();
  f.reply('project.listLocal', { projects: [LOCAL_B] }); await flush();
  // 刪除本身成功：一般說明一則、需要注意一則；清單上方是琥珀色的提醒，不是錯誤框（使用者 2026-10-13 裁定訊息分三級）。
  assert.deepEqual(f.messages().slice(0, 2), [{ text: '合成：資料夾還有檔案', level: 'warn' }, { text: '已刪除案件「合成甲案」。', level: 'info' }]);
  assert.equal(errorBox(f), null);
  const note = f.$('.form-notice--warning');
  assert.ok(note !== null, 'an amber note is shown above the list');
  assert.equal(note.textContent, '合成：資料夾還有檔案');
  assert.equal(note.getAttribute('role'), 'status');
});

test('journey 1: a failed deletion sends no list request, explains the failure with the case and correlation, and returns focus to that delete button', async () => {
  const f = await boot([LOCAL_A, LOCAL_B]);
  f.click('[data-action="picker-delete"][data-project-id="合成甲案"]');
  f.reply('project.deletePreview', { reportCount: 0, workpaperCount: 0 }); await flush();
  const listsBefore = f.sent('project.listLocal').length;
  f.click('[data-action="modal-confirm"]');
  f.fail('project.delete', { code: 'file_locked', message: '合成：檔案正被其他程式使用' }, 'delete-correlation'); await flush();
  assert.equal(f.sent('project.listLocal').length, listsBefore, 'a failed deletion does not reload the list');
  assert.equal(f.state().busy, false);
  // 「刪除案件」是使用者 2026-10-13 的裁定；失敗句是勾選頁確認 19（final-wording.json）。
  const box = errorBox(f);
  assert.equal(box.role, 'alert');
  assert.match(box.body, /^刪除案件失敗：合成：檔案正被其他程式使用（耗時/);
  assert.deepEqual(plain(f.state().pickerFeedbackContext),
    { projectId: '合成甲案', errorCode: 'file_locked', correlationId: 'delete-correlation', devExportedPath: null });
  assert.deepEqual(rowNames(f, 'local'), ['合成甲案', '合成乙案']);
  f.timers.run();
  const active = f.dom.document.activeElement;
  assert.ok(active !== null && active === f.$('[data-action="picker-delete"][data-project-id="合成甲案"]'), 'focus returns to the delete button of the same case');
});

test('journey 1: when the list cannot be read again after a deletion, the deletion still stands and the list error is shown', async () => {
  const f = await boot([LOCAL_A, LOCAL_B]);
  f.click('[data-action="picker-delete"][data-project-id="合成甲案"]');
  f.reply('project.deletePreview', { reportCount: 0, workpaperCount: 0 }); await flush();
  f.click('[data-action="modal-confirm"]');
  f.reply('project.delete', {}); await flush();
  f.fail('project.listLocal', { code: 'synthetic', message: '合成：清單讀不出來' }); await flush();
  assert.equal(f.state().busy, false);
  assert.ok(f.messages().some(m => m.text === '已刪除案件「合成甲案」。'), 'the deletion message stays');
  const box = errorBox(f);
  assert.equal(box.role, 'alert');
  assert.match(box.body, /合成：清單讀不出來/);
  assert.ok(f.messages().some(m => m.level === 'error' && m.text.includes('合成：清單讀不出來')), 'the list failure is logged as an error');
});

test('journey 1: a case that fails to open keeps the auditor on the list with the reason, and the support log can still be exported', async () => {
  const f = await boot([LOCAL_A]);
  f.click('[data-action="picker-open"][data-project-id="合成甲案"]');
  const load = f.request('project.load');
  assert.deepEqual(plain(load.payload), { projectId: '合成甲案', databaseProvider: 'sqlite' });
  f.clock.advance(1200);
  f.fail(load, { code: 'file_read_error', message: '合成：案件資料讀不出來' }, 'load-correlation'); await flush();
  assert.equal(f.state().view, 'picker');
  assert.equal(f.state().project, null);
  // 「開啟案件」是勾選頁確認 L03，失敗句是勾選頁確認 19（final-wording.json），2026-10-14 第三輪 R3-27 括號後加句號。
  const box = errorBox(f);
  assert.equal(box.role, 'alert');
  assert.equal(box.body, '開啟案件失敗：合成：案件資料讀不出來（耗時 1.2 秒）。');
  assert.deepEqual(plain(f.state().pickerFeedbackContext),
    { projectId: '合成甲案', errorCode: 'file_read_error', correlationId: 'load-correlation', devExportedPath: null });
  // 支援日誌是案件清單操作列的一般工具：不帶案件，輸出後寫在按鈕旁，錯誤框照舊。
  f.click('[data-action="picker-support-export"]');
  const exported = f.request('support.log.export');
  assert.deepEqual(plain(exported.payload), {});
  f.reply(exported, { filePath: '合成/logs/JET-support.txt' }); await flush();
  // 使用者 2026-10-13 勾選頁確認 6（final-wording.json，維持原句）。
  assert.equal(f.$('.picker-panel__actions [role="status"]').textContent, '支援日誌已輸出：合成/logs/JET-support.txt');
  assert.match(errorBox(f).body, /合成：案件資料讀不出來/);
});

test('journey 1: while the list is still loading, opening a case sends nothing and stays on the list', async () => {
  const f = await boot([LOCAL_A]);
  f.click('[data-action="picker-refresh-local"]');
  assert.equal(f.state().busyLabel, '讀取案件清單');
  f.$('[data-action="picker-open"][data-project-id="合成甲案"]').dispatch('click');
  await flush();
  assert.equal(f.sent('project.load').length, 0, 'no project.load while the list is loading');
  assert.equal(f.state().view, 'picker');
  // 使用者 2026-10-13 勾選頁確認 14（final-wording.json）。
  assert.deepEqual(f.messages()[0], { text: '「讀取案件清單」還在進行，請等它完成後再試。', level: 'info' });
});

test('journey 1: after leaving the list to create a case, a late list answer or failure changes nothing', async () => {
  for (const outcome of ['answer', 'failure']) {
    const f = await boot([LOCAL_A]);
    f.click('[data-action="picker-refresh-local"]');
    const late = f.request('project.listLocal');
    f.click('[data-action="picker-new"]');
    assert.equal(f.state().view, 'workflow');
    const messagesBefore = f.messages().length;
    if (outcome === 'answer') f.reply(late, { projects: [LOCAL_B] }); else f.fail(late, { code: 'synthetic', message: '合成：晚到的失敗' });
    await flush();
    assert.equal(f.state().view, 'workflow', outcome + ': still creating a case');
    assert.deepEqual(plain(f.state().projects.map(p => p.projectId)), ['合成甲案'], outcome + ': the old list is not replaced');
    assert.equal(f.state().pickerFeedback, null, outcome + ': no error on the list');
    assert.equal(f.messages().length, messagesBefore, outcome + ': nothing is logged');
  }
});

test('journey 1: when the window cannot close after leaving the case, the auditor is back on the list and told the case was left safely', async () => {
  const f = await boot([LOCAL_A]);
  f.click('[data-action="picker-open"][data-project-id="合成甲案"]');
  f.reply('project.load', { project: { ...LOCAL_A, periodStart: '2025-01-01', periodEnd: '2025-12-31', currentStep: 1 } }); await flush();
  f.reply('log.recent', { messages: [] }); await flush();
  assert.equal(f.state().view, 'workflow');
  f.click('[data-action="app-exit"]'); await flush();
  f.reply('project.saveProgress', {}); await flush();
  f.reply('project.releaseLock', {}); await flush();
  f.fail('host.exitApp', { code: 'synthetic', message: '合成：視窗關不掉' }); await flush();
  assert.equal(f.state().view, 'picker');
  assert.equal(f.state().project, null);
  // 只比對意思：說明案件已安全離開，並寫出原因。
  const box = errorBox(f);
  assert.match(box.body, /已安全離開/);
  assert.match(box.body, /合成：視窗關不掉/);
  // 回到清單時照樣讀一次本機清單。
  f.reply(f.sent('project.listLocal').at(-1), { projects: [LOCAL_A] }); await flush();
  assert.deepEqual(rowNames(f, 'local'), ['合成甲案']);
});

// 審計員開著 JET 切到 Excel 等其他程式：視窗不在前景時玻璃改成不透明（app.css 的 html.window-inactive），回到 JET 時恢復。
// Apple 人機介面指引 Windows：不在作用中的視窗不使用 vibrancy。使用者 2026-10-15 對這項建議選「直接照建議改」。新增的測試。
test('journey 1: switching to another program flattens the glass, coming back restores it, and nothing is sent', async () => {
  const f = await boot([LOCAL_A]);
  const root = f.dom.document.documentElement;
  const fire = type => { for (const entry of f.windowListeners.filter(item => item.type === type)) entry.fn({ type }); };
  const sentBefore = f.posted.length;
  assert.equal(root.classList.contains('window-inactive'), false, 'JET opens with the normal glass');
  fire('blur');
  assert.equal(root.classList.contains('window-inactive'), true, 'another program is in front');
  fire('focus');
  assert.equal(root.classList.contains('window-inactive'), false, 'back in JET');
  await flush();
  assert.equal(f.posted.length, sentBefore, 'switching programs only changes the look and sends no action');
});
