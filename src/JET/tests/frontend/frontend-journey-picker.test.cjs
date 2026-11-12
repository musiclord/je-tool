'use strict';
// 情境 1：案件清單。
//
// 審計員的操作：開啟 JET 看到本機案件清單；按「重新整理本機」，成功與失敗；刪除一個案件，失敗、成功、
// 資料夾沒刪乾淨；開一個會失敗的案件後輸出支援日誌；清單還在讀取時想開案或新增案件；按「退出」但視窗關不掉，回到案件清單；
// 開著 JET 切到其他程式再回來。
//
// 夾具：支援模組的 fullAppFixture，經 bootFullApp（本檔叫 boot）開啟 JET 停在案件清單：
// 整個 index.html 的 body 標記與全部前端腳本，正式的 jet-api.js 經假的 chrome.webview 收發，app.js 照瀏覽器執行正式的 init()。
// remapAppFixture 把 app.js 切成幾段載入、不跑 init()，案件清單第一次讀取與 jet-api.js 的請求都看不到，所以這裡用整個畫面。
//
// 逐字比對的只有已定案的用字；「重新整理本機」這類按鈕文字的用字沒有定案，
// 按鈕用 data-action 找，文字只比對意思。預期值一律寫死；action payload 用 deepEqual 精確比對；
// 斷言的失敗值不是假 DOM 節點（節點只用 === 轉成布林）。
// 作業名稱（Ui.run 的 label）只有「讀取案件清單」、「開啟案件」與「刪除案件」的用字已定案，
// 其他作業名稱放在已定案的句型裡時只比對意思。
// 每個測試都有 30 秒上限：改壞檢查時若有請求永遠等不到回應，測試直接失敗，不會拖到改壞工具的時間上限。
const nodeTest = require('node:test');
const test = (name, fn) => nodeTest(name, { timeout: 30000 }, fn);
const assert = require('node:assert/strict');
const { plain, flush, bootFullApp: boot, noDunderKeys } = require('./support/frontend-fixtures.cjs');

const LOCAL_A = { projectId: '合成甲案', clientName: '合成客戶甲', databaseProvider: 'sqlite' };
// 正式後端只列 SQLite 案件。
const LOCAL_B = { projectId: '合成乙案', clientName: '合成客戶乙', databaseProvider: 'sqlite' };

const rowNames = (f, list) => f.dom.content.querySelectorAll('[data-motion-list="' + list + '"] .project-row__title').map(node => node.textContent);
// 清單上方的錯誤框：標題（只比對意思）與內文分開讀。
const errorBox = f => {
  const box = f.$('.picker-feedback');
  if (box === null) return null;
  const title = box.querySelector('.picker-feedback__title'), body = box.querySelector('.picker-feedback__body');
  return { role: box.getAttribute('role'), title: title === null ? null : title.textContent, body: body === null ? null : body.textContent };
};

test('JET starts from the local list only, and refreshing asks only for the local list again', async () => {
  const f = await boot([LOCAL_A, LOCAL_B]);
  // 啟動時只讀本機清單；畫面沒有線上清單，也沒有「同步線上案件」按鈕。
  assert.deepEqual(f.posted.map(r => r.action), ['system.ping', 'system.whoAmI', 'project.listLocal']);
  assert.deepEqual(plain(f.request('project.listLocal').payload), {});
  assert.deepEqual(rowNames(f, 'local'), ['合成甲案', '合成乙案']);
  assert.ok(f.$('[data-motion-list="online"]') === null, 'there is no online list');
  assert.ok(f.$('[data-action="picker-sync-online"]') === null, 'there is no online sync button');
  // 重新整理的按鈕說明是本機（只比對意思）。
  assert.match(f.$('[data-action="picker-refresh-local"]').textContent, /本機/);

  f.click('[data-action="picker-refresh-local"]');
  f.reply('project.listLocal', { projects: [LOCAL_B] }); await flush();
  assert.deepEqual(rowNames(f, 'local'), ['合成乙案']);
  assert.equal(f.sent('project.listLocal').length, 2);
});

// 案件清單只有本機一區，標題下沒有重述標題的說明句。
test('the case list has only the local section and the title has no sentence restating it', async () => {
  const f = await boot([LOCAL_A]);
  const sections = f.$('.picker-panel').children.filter(node => node.className === 'picker-section');
  assert.equal(sections.length, 1);
  assert.ok(sections[0].querySelector('[data-motion-list="local"]') !== null, 'the only section is the local list');
  assert.ok(f.$('.picker-panel__hint') === null, 'no sentence under the title');
  assert.deepEqual(rowNames(f, 'local'), ['合成甲案']);
});

// 按「重新整理本機」後讀取失敗。作業名稱「讀取案件清單」的用字已定案，逐字比對。
test('a failed refresh keeps the local rows, puts the reason above the list as an alert, and the next refresh clears it at once', async () => {
  const f = await boot([LOCAL_A]);
  f.click('[data-action="picker-refresh-local"]');
  f.clock.advance(2500);
  f.fail('project.listLocal', { code: 'synthetic', message: '合成：清單讀不出來' }, 'refresh-correlation'); await flush();
  assert.deepEqual(rowNames(f, 'local'), ['合成甲案']);
  // 案件清單看不到訊息面板，所以同一則失敗句也寫在清單上方的錯誤框。
  const box = errorBox(f);
  assert.equal(box.role, 'alert');
  assert.match(box.title, /無法/, 'the box says the operation could not be completed');
  assert.equal(box.body, '讀取案件清單失敗：合成：清單讀不出來（耗時 2.5 秒）。');
  assert.deepEqual(f.messages()[0], { text: box.body, level: 'error' });
  // 錯誤框在案件清單之前，而且框裡沒有按鈕。
  const panel = f.$('.picker-panel').children;
  assert.ok(panel.findIndex(node => node.className === 'picker-feedback') < panel.findIndex(node => node.className === 'picker-section'),
    'the error box comes before the case lists');
  assert.ok(f.$('.picker-feedback button') === null, 'no buttons inside the error box');
  // 案件清單不是步驟畫面，不加步驟提示框。
  assert.equal(f.state().operationFailure, null);
  // 再按一次重新整理：舊的錯誤框立刻清掉，不等回應。
  f.click('[data-action="picker-refresh-local"]');
  assert.equal(errorBox(f), null, 'the old error is cleared as soon as the refresh starts');
  f.reply('project.listLocal', { projects: [LOCAL_A] }); await flush();
  assert.deepEqual(rowNames(f, 'local'), ['合成甲案']);
});

test('deleting a case reloads the local list once only after it succeeds, the row disappears and focus moves to the next case', async () => {
  const f = await boot([LOCAL_A, LOCAL_B]);
  f.click('[data-action="picker-delete"][data-project-id="合成甲案"]');
  assert.equal(f.$('[data-bind="delete-modal"]').hidden, false);
  // 確認框的標題是「刪除案件」。
  assert.equal(f.$('#delete-modal-title').textContent, '刪除案件');
  assert.deepEqual(plain(f.request('project.deletePreview').payload), { projectId: '合成甲案' });
  f.reply('project.deletePreview', { reportCount: 1, workingPaperCount: 2 }); await flush();
  // 數量句以句號結尾，後面直接接刪除範圍，中間不多一個空白。
  assert.match(f.$('[data-bind="delete-modal-detail"]').innerHTML, /工作底稿 2 份。將永久刪除此案件的資料庫。/);
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
  assert.equal(f.state().busyLabel, '讀取案件清單');
  f.reply('project.listLocal', { projects: [LOCAL_B] }); await flush();
  assert.deepEqual(rowNames(f, 'local'), ['合成乙案']);
  assert.equal(f.state().busy, false);
  // 這句的用字已定案。
  assert.deepEqual(f.messages()[0], { text: '已刪除案件「合成甲案」。', level: 'info' });
  assert.equal(errorBox(f), null);
  // 被刪的那一列不在了，焦點交給下一個案件的開啟鈕。
  f.timers.run();
  const active = f.dom.document.activeElement;
  assert.ok(active !== null && active === f.$('[data-action="picker-open"][data-project-id="合成乙案"]'), 'focus lands on the next case');
});

// 訊息分三級。刪除案件後資料夾沒刪乾淨時，案件清單上方是琥珀色的提醒。
// 這則提醒要等清單重新讀回後才寫上，所以 refresh 先讀回清單，再補上提醒；只在刪除成功後執行。
test('when the case folder is not fully removed the list reloads and an amber note above it repeats the backend sentence', async () => {
  const f = await boot([LOCAL_A, LOCAL_B]);
  f.click('[data-action="picker-delete"][data-project-id="合成甲案"]');
  f.reply('project.deletePreview', { reportCount: 0, workingPaperCount: 0 }); await flush();
  f.click('[data-action="modal-confirm"]');
  f.reply('project.delete', { message: '合成：資料夾還有檔案' }); await flush();
  f.reply('project.listLocal', { projects: [LOCAL_B] }); await flush();
  // 刪除本身成功：一般說明一則、需要注意一則；清單上方是琥珀色的提醒，不是錯誤框。
  assert.deepEqual(f.messages().slice(0, 2), [{ text: '合成：資料夾還有檔案', level: 'warn' }, { text: '已刪除案件「合成甲案」。', level: 'info' }]);
  assert.equal(errorBox(f), null);
  const note = f.$('.form-notice--warning');
  assert.ok(note !== null, 'an amber note is shown above the list');
  assert.equal(note.textContent, '合成：資料夾還有檔案');
  assert.equal(note.getAttribute('role'), 'status');
});

test('a failed deletion sends no list request, explains the failure with the case and correlation, and returns focus to that delete button', async () => {
  const f = await boot([LOCAL_A, LOCAL_B]);
  f.click('[data-action="picker-delete"][data-project-id="合成甲案"]');
  f.reply('project.deletePreview', { reportCount: 0, workingPaperCount: 0 }); await flush();
  const listsBefore = f.sent('project.listLocal').length;
  f.click('[data-action="modal-confirm"]');
  f.fail('project.delete', { code: 'file_locked', message: '合成：檔案正被其他程式使用' }, 'delete-correlation'); await flush();
  assert.equal(f.sent('project.listLocal').length, listsBefore, 'a failed deletion does not reload the list');
  assert.equal(f.state().busy, false);
  // 「刪除案件」與失敗句的用字已定案。
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

test('when the list cannot be read again after a deletion, the deletion still stands and the list error is shown', async () => {
  const f = await boot([LOCAL_A, LOCAL_B]);
  f.click('[data-action="picker-delete"][data-project-id="合成甲案"]');
  f.reply('project.deletePreview', { reportCount: 0, workingPaperCount: 0 }); await flush();
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

test('a case that fails to open keeps the auditor on the list with the reason, and a debug build can still export the support log', async () => {
  const f = await boot([LOCAL_A]);
  // 案件清單的「輸出支援日誌」只在 Debug 組建出現。
  f.store.setDevToolsEnabled(true); await flush();
  f.click('[data-action="picker-open"][data-project-id="合成甲案"]');
  const load = f.request('project.load');
  // 開案請求只帶案件名稱。
  assert.deepEqual(plain(load.payload), { projectId: '合成甲案' });
  f.clock.advance(1200);
  f.fail(load, { code: 'file_read_error', message: '合成：案件資料讀不出來' }, 'load-correlation'); await flush();
  assert.equal(f.state().view, 'picker');
  assert.equal(f.state().project, null);
  // 「開啟案件」與失敗句的用字已定案，包括括號後的句號。
  const box = errorBox(f);
  assert.equal(box.role, 'alert');
  assert.equal(box.body, '開啟案件失敗：合成：案件資料讀不出來（耗時 1.2 秒）。');
  assert.deepEqual(plain(f.state().pickerFeedbackContext),
    { projectId: '合成甲案', errorCode: 'file_read_error', correlationId: 'load-correlation', devExportedPath: null });
  // 支援日誌在案件清單卡片下方的頁尾：不帶案件，輸出後寫在按鈕下方，錯誤框照舊。
  f.click('[data-action="picker-support-export"]');
  const exported = f.request('support.log.export');
  assert.deepEqual(plain(exported.payload), {});
  f.reply(exported, { filePath: '合成/logs/JET-support.txt' }); await flush();
  // 這句的用字已定案。
  assert.equal(f.$('.picker-footer [role="status"]').textContent, '支援日誌已輸出：合成/logs/JET-support.txt');
  assert.match(errorBox(f).body, /合成：案件資料讀不出來/);
});

test('while the list is still loading, opening a case sends nothing and stays on the list', async () => {
  const f = await boot([LOCAL_A]);
  f.click('[data-action="picker-refresh-local"]');
  assert.equal(f.state().busyLabel, '讀取案件清單');
  f.$('[data-action="picker-open"][data-project-id="合成甲案"]').dispatch('click');
  await flush();
  assert.equal(f.sent('project.load').length, 0, 'no project.load while the list is loading');
  assert.equal(f.state().view, 'picker');
  // 這句的用字已定案。
  assert.deepEqual(f.messages()[0], { text: '「讀取案件清單」還在進行，請等它完成後再試。', level: 'info' });
});

test('after leaving the list to create a case, a late list answer or failure changes nothing', async () => {
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

test('when the window cannot close after leaving the case, the auditor is back on the list and told the case was left safely', async () => {
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
// Apple 人機介面指引 Windows：不在作用中的視窗不使用 vibrancy。
test('switching to another program flattens the glass, coming back restores it, and nothing is sent', async () => {
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

// 流程總覽與刪除案件對話框同時開著時，一次 Escape 只關最上層，
// Tab 只在最上層循環，關閉後焦點回原處。
// 目前操作上碰不到這個情形（總覽開著時蓋住刪除鈕），測試直接按下被蓋住的刪除鈕。
test('with the overview open, the delete dialog opened on top takes Escape and Tab alone and hands focus back when it closes', async () => {
  const f = await boot([LOCAL_A, LOCAL_B]);
  const opener = f.$('[data-action="overview-open"]'); opener.focus(); opener.dispatch('click');
  assert.equal(f.state().overviewOpen, true); assert.equal(f.$('.overview-modal').hidden, false);
  const overviewClose = f.$('.overview-modal__card button[data-action="overview-close"]');
  assert.ok(f.dom.document.activeElement === overviewClose, 'the overview has focus');
  const deleteButton = f.$('[data-action="picker-delete"][data-project-id="合成甲案"]');
  deleteButton.focus(); deleteButton.dispatch('click');
  const modal = f.$('[data-bind="delete-modal"]'); assert.equal(modal.hidden, false, 'the delete dialog opens on top');
  const cancel = modal.querySelector('button[data-action="modal-cancel"]'), confirm = modal.querySelector('[data-action="modal-confirm"]');
  assert.ok(f.dom.document.activeElement === cancel, 'focus moves into the delete dialog');
  // Tab 只在最上層循環：從最後一顆按鈕回到第一顆，不會被下面的總覽拉走。
  confirm.focus();
  const tab = f.dom.document.dispatch('keydown', { key: 'Tab', shiftKey: false });
  assert.equal(tab.defaultPrevented, true, 'Tab is handled by the dialog on top');
  assert.ok(f.dom.document.activeElement === cancel, 'Tab wraps inside the delete dialog');
  // 一次 Escape 只關最上層，焦點回到打開它的刪除鈕。
  f.dom.document.dispatch('keydown', { key: 'Escape' }); f.timers.run();
  assert.equal(modal.hidden, true, 'Escape closes the delete dialog');
  assert.equal(f.state().overviewOpen, true, 'the overview below stays open');
  assert.ok(f.dom.document.activeElement === deleteButton, 'focus returns to the delete button that opened the dialog');
  // 現在最上層是總覽：Tab 把跑到外面的焦點拉回總覽，Escape 關掉它，焦點回到「總覽」按鈕。
  const pull = f.dom.document.dispatch('keydown', { key: 'Tab' });
  assert.equal(pull.defaultPrevented, true); assert.ok(f.dom.document.activeElement === overviewClose, 'Tab pulls focus back into the overview');
  f.dom.document.dispatch('keydown', { key: 'Escape' });
  assert.equal(f.state().overviewOpen, false, 'the second Escape closes the overview');
  assert.ok(f.dom.document.activeElement === opener, 'focus returns to the overview button');
});

// 反過來：刪除對話框先開，總覽開在上面。Escape 只關總覽，刪除對話框回到最上層後 Tab 拉回它、Escape 關掉它。
test('with the delete dialog open, the overview opened on top takes Escape alone and the dialog takes the keyboard back afterwards', async () => {
  const f = await boot([LOCAL_A, LOCAL_B]);
  const deleteButton = f.$('[data-action="picker-delete"][data-project-id="合成甲案"]'); deleteButton.focus(); deleteButton.dispatch('click');
  const modal = f.$('[data-bind="delete-modal"]'), cancel = modal.querySelector('button[data-action="modal-cancel"]');
  assert.equal(modal.hidden, false); assert.ok(f.dom.document.activeElement === cancel);
  const opener = f.$('[data-action="overview-open"]'); opener.focus(); opener.dispatch('click');
  assert.equal(f.state().overviewOpen, true);
  const card = f.$('.overview-modal__card'), overviewClose = card.querySelector('button[data-action="overview-close"]');
  assert.ok(f.dom.document.activeElement === overviewClose, 'the overview on top has focus');
  const tab = f.dom.document.dispatch('keydown', { key: 'Tab', shiftKey: true });
  assert.equal(tab.defaultPrevented, true); assert.ok(card.contains(f.dom.document.activeElement), 'Tab stays inside the overview on top');
  f.dom.document.dispatch('keydown', { key: 'Escape' });
  assert.equal(f.state().overviewOpen, false, 'Escape closes the overview on top');
  assert.equal(modal.hidden, false, 'the delete dialog below stays open');
  assert.ok(f.dom.document.activeElement === opener, 'focus returns to the overview button');
  const pull = f.dom.document.dispatch('keydown', { key: 'Tab' });
  assert.equal(pull.defaultPrevented, true); assert.ok(f.dom.document.activeElement === cancel, 'Tab pulls focus back into the delete dialog');
  f.dom.document.dispatch('keydown', { key: 'Escape' }); f.timers.run();
  assert.equal(modal.hidden, true, 'Escape closes the delete dialog');
  assert.ok(f.dom.document.activeElement === deleteButton, 'focus returns to the delete button');
});
