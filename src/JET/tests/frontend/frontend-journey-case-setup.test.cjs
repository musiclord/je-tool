'use strict';
// 第一步建立與修改案件，以及第二步匯入與行事曆的情境測試。
//
// 使用者的操作：
//   建立與修改案件：填表建立案件；後端指出某一欄、沒指出欄位、指出不在白名單的欄位時各失敗一次；修改案件資料時失敗；
//           填表到一半 Windows 身分才送到；表單剛打開就點進欄位；建好以後在頁首與案件清單看到案件名稱與客戶名稱。
//           表單填到一半時別的作業失敗、步驟上方出現失敗提示，接著送出表單（建立案件與修改案件資料各一次）；
//           案件建好以後接著開啟它時失敗或被取消，回到案件清單從清單開啟；JET 放不掉這個案件時再按一次「建立案件」；
//           Debug 組建在開啟失敗後回到的案件清單按頁尾的「輸出診斷日誌」；
//           從案件清單開啟案件，後端已經開啟、畫面沒有開成，以及這時放不掉案件。
//           這三段在檔案最後，用整個 JET 畫面的 bootFullApp，原因寫在各段的開頭。
//   匯入與行事曆：選 GL 與 TB 來源檔；匯入 0 筆的假日檔與補班檔；把整週都設成工作日；重存同一組非工作日但順序不同或重複；改成不同的非工作日。
// 夾具：support/frontend-fixtures.cjs 的 usageJourneyFixture（正式 state、ui-core、各步驟腳本，可訂閱重繪）
// 與 remapAppFixture（另加上 app.js 的頁首、案件清單與中欄重繪）。這裡用 usageJourneyFixture 的假 DOM 畫正式的表單，
// 畫出來的欄位、屬性與焦點都看得到。
//
// jet-api 把 wire 錯誤轉成 err.field，由 frontend-journey-transport.test.cjs 負責。
// 流程總覽的標題由 frontend-journey-overview.test.cjs 負責。
//
// 這個檔前面的測試刻意不斷言訊息面板的句子（建案成功、非工作日已更新或沒有變更、匯入完成等）、步驟提示框與操作名稱；
// 這些在後面「訊息面板的句子」那一段斷言。後端原因句子只當作原樣顯示的合成字串，不比對正式句子。
// 斷言的失敗值一律不是假 DOM 節點（節點只用 === 轉成布林）；預期值一律寫死；action payload 用 deepEqual 精確比對，並確認沒有 `__` 鍵。
const test = require('node:test');
const assert = require('node:assert/strict');
const { usageJourneyFixture, remapAppFixture, settle, plain, readWwwroot, noDunderKeys, bootFullApp, flush } = require('./support/frontend-fixtures.cjs');

// 瀏覽器的 form 可以用欄位名稱直接取到欄位（form.projectName）；create-step.js 送出時這樣讀，假 DOM 沒有，這裡補上。
function namedForm(form) {
  assert.ok(form, 'form rendered');
  for (const node of form.querySelectorAll('input,select')) {
    const name = node.getAttribute('name');
    if (name) form[name] = node;
  }
  return form;
}

function fill(form, values) {
  for (const [name, value] of Object.entries(values)) { assert.ok(form[name], name + ' visible'); form[name].value = value; }
}

function submit(form) { form.fire('submit', { preventDefault() {} }); }

// JetFocus.defer 交給它的元素，就是瀏覽器畫完後會取得焦點的元素。
function recordFocus(f) {
  const deferred = [];
  f.window.JetFocus = { defer(target) { deferred.push(target); }, deferFirst(selectors) { deferred.push(selectors); } };
  return deferred;
}

function createFixture() {
  const f = usageJourneyFixture('create');
  f.store.resetWorkflow();
  const focus = recordFocus(f);
  const page = () => namedForm(f.draw('create').querySelector('[data-bind="create-form"]'));
  return { ...f, focus, page };
}

// 建案表單沒有資料儲存方式，一律建 SQLite 案件。
const VALID_CREATE = { projectName: '合成案件甲', projectCode: 'SYN-01', clientName: '合成客戶股份有限公司',
  periodStart: '2025-01-01', periodEnd: '2025-12-31', prepStartDate: '2025-12-20' };

function labelText(row) { return row.querySelector('.form__label').textContent.replace(/\s*\*$/, ''); }

// =====================================================================================================
// 第一步建立與修改案件
// =====================================================================================================

test('the create form collects the case details without an operator field and labels carry no bracketed notes', () => {
  const f = createFixture();
  f.store.setCurrentUser({ shortName: '合成審計員', userNumber: 7 });
  const form = f.page();

  assert.ok(form.querySelector('[name="operatorId"]') === null, 'the form has no operator field');
  const operator = form.querySelector('[data-bind="create-operator"]');
  assert.ok(operator !== null, 'the current account is shown as a notice');
  assert.ok(operator.querySelector('input') === null, 'the current account cannot be typed in');
  assert.match(operator.textContent, /合成審計員/);

  // 第一步的標籤不帶括號說明：「案件編號」「客戶名稱」不寫「（選填）」，因為必填欄已經用星號標示。
  // 這兩個標籤逐字比對。建案表單沒有「資料儲存方式」這一欄，建案一律是 SQLite。
  const rows = form.querySelectorAll('.form__row');
  const labels = rows.map(labelText);
  for (const label of ['案件編號', '客戶名稱']) assert.ok(labels.includes(label), label + ' is the exact label');
  assert.ok(!labels.includes('資料儲存方式'), 'the form has no storage choice');
  assert.ok(form.querySelector('[name="databaseProvider"]') === null, 'no database field');
  assert.deepEqual(labels.filter(label => /選填|（|\(|建立後/.test(label)), [], 'no label carries a bracketed note');

  // 必填欄用星號標示，選填欄沒有星號，也不寫「選填」。
  const starred = rows.filter(row => row.querySelector('.form__req') !== null)
    .map(row => row.querySelector('input,select').getAttribute('name')).sort();
  assert.deepEqual(starred, ['periodEnd', 'periodStart', 'projectName']);
  for (const name of ['projectName', 'periodStart', 'periodEnd']) assert.equal(form[name].hasAttribute('required'), true, name + ' required');
  for (const name of ['projectCode', 'clientName', 'prepStartDate']) assert.equal(form[name].hasAttribute('required'), false, name + ' optional');
});

test('creating a case sends exactly the entered details without an operator, then opens it and shows its name and client', async () => {
  const f = remapAppFixture(); f.store.resetWorkflow(); f.store.setView('workflow');
  f.store.setCurrentUser({ shortName: '合成審計員', userNumber: 7 });
  // 頁首的案件摘要用正式 index.html 的標記。
  f.dom.content.insertAdjacentHTML('beforeend', readWwwroot('index.html').match(/<section class="case-summary"[\s\S]*?<\/section>/)[0]);
  const form = namedForm(f.draw('create').querySelector('[data-bind="create-form"]'));
  fill(form, { ...VALID_CREATE, projectName: '  合成案件甲  ', projectCode: ' SYN-01 ', clientName: ' 合成客戶股份有限公司 ' });
  submit(form);

  const create = f.request('projectCreate');
  assert.deepEqual(plain(create.payload), { projectName: '合成案件甲', projectCode: 'SYN-01', clientName: '合成客戶股份有限公司',
    periodStart: '2025-01-01', periodEnd: '2025-12-31', prepStartDate: '2025-12-20' });
  assert.equal(Object.hasOwn(create.payload, 'operatorId'), false, 'the operator is taken by the backend, not sent');
  assert.ok(noDunderKeys(create.payload));

  create.resolve({ projectId: '合成案件甲', warnings: [] }); await settle();
  const load = f.request('projectLoad');
  assert.deepEqual(plain(load.payload), { projectId: '合成案件甲' });
  load.resolve({ project: { projectId: '合成案件甲', clientName: '合成客戶股份有限公司', projectCode: 'SYN-01', operatorId: '合成審計員',
    periodStart: '2025-01-01', periodEnd: '2025-12-31', prepStartDate: '2025-12-20', databaseProvider: 'sqlite', currentStep: 0 } });
  await settle();

  // 案件以案件名稱識別，客戶名稱另外顯示。
  assert.equal(f.state.caseId, '合成案件甲');
  assert.equal(f.state.caseClient, '合成客戶股份有限公司');
  f.app.render();
  assert.equal(f.$('[data-bind="case-id"]').textContent, '合成案件甲');
  assert.equal(f.$('[data-bind="case-client"]').textContent, '合成客戶股份有限公司');

  // 建好以後第一步顯示建案者是後端記下的名稱。
  const summary = f.draw('create').querySelector('.kv-list').textContent;
  assert.match(summary, /合成案件甲/);
  assert.match(summary, /合成審計員/);
  assert.ok(f.draw('create').querySelector('[data-bind="create-form"]') === null, 'the form is replaced by the case summary');
});

test('the case list names each case by its case name and lists the client and case number separately', () => {
  const f = remapAppFixture(); f.store.resetWorkflow(); f.store.setView('picker');
  f.store.setProjects([
    { projectId: '合成案件甲', clientName: '合成客戶股份有限公司', projectCode: 'SYN-01', databaseProvider: 'sqlite' },
    // 正式後端只列 SQLite 案件。
    { projectId: '合成案件乙', clientName: '', databaseProvider: 'sqlite' }
  ], null);
  f.app.renderPicker(f.state);

  const first = f.$('.project-row[data-project-id="合成案件甲"]');
  assert.ok(first !== null, 'first row rendered');
  assert.equal(first.querySelector('.project-row__title').textContent, '合成案件甲');
  assert.equal(first.querySelector('.project-row__entity').textContent, '合成客戶股份有限公司');
  assert.equal(first.querySelector('.project-row__code').textContent, 'SYN-01');
  assert.doesNotMatch(first.querySelector('.project-row__title').textContent, /合成客戶/, 'the client is not the row name');
  assert.match(first.querySelector('[data-action="picker-open"]').getAttribute('aria-label'), /合成案件甲/);

  const second = f.$('.project-row[data-project-id="合成案件乙"]');
  assert.ok(second !== null, 'second row rendered');
  assert.equal(second.querySelector('.project-row__title').textContent, '合成案件乙');
  assert.ok(second.querySelector('.project-row__entity') === null, 'no empty client line');
});

// 同一錯誤呈現也支援日期與資料修改欄位，依 field 白名單定位，不解析訊息。
// 後端訊息原樣顯示；這裡的訊息都是合成字串。
test('a create failure marks only the field the backend names and never guesses a field from the code or the message', async () => {
  const cases = [
    { name: 'named case name', error: { code: 'invalid_payload', field: 'projectName', message: '合成原因：名稱不可用。' }, marked: 'projectName' },
    { name: 'named period start', error: { code: 'invalid_payload', field: 'periodStart', message: '合成原因：日期。' }, marked: 'periodStart' },
    { name: 'field outside the allow-list', error: { code: 'invalid_payload', field: 'databaseProvider', message: '合成原因：資料儲存方式。' }, marked: null },
    { name: 'prototype key', error: { code: 'invalid_payload', field: '__proto__', message: '合成原因：案件名稱。' }, marked: null },
    // 訊息與代碼都提到案件名稱，但沒有 field：不從文字猜。
    { name: 'no field', error: { code: 'invalid_payload', message: '案件名稱與客戶名稱的合成原因（projectName）。' }, marked: null }
  ];
  for (const { name, error, marked } of cases) {
    const f = createFixture();
    const form = f.page();
    fill(form, VALID_CREATE);
    submit(form);
    f.request('projectCreate').reject(error); await settle();

    const summary = form.querySelector('[data-bind="create-error"]');
    assert.equal(summary.hidden, false, name + ': the reason is shown');
    assert.equal(summary.getAttribute('role'), 'alert', name);
    assert.equal(summary.textContent, error.message, name + ': the backend reason is shown as written');
    const invalid = form.querySelectorAll('[aria-invalid="true"]').map(node => node.getAttribute('name'));
    assert.deepEqual(invalid, marked ? [marked] : [], name + ': marked fields');
    if (marked) assert.equal(form[marked].getAttribute('aria-describedby'), 'create-form-error', name);
    const target = f.focus.at(-1);
    assert.ok(target === (marked ? form[marked] : summary), name + ': focus goes to ' + (marked || 'the reason'));
    assert.equal(f.requests.filter(r => r.action === 'projectLoad').length, 0, name + ': nothing is opened');

    // 重新輸入時錯誤標示收起；表單內容不變。
    form.fire('input', { target: form.projectName });
    assert.equal(summary.hidden, true, name + ': typing hides the reason');
    assert.deepEqual(form.querySelectorAll('[aria-invalid="true"]').map(node => node.getAttribute('name')), [], name);
    assert.equal(form.clientName.value, '合成客戶股份有限公司', name + ': the entered values stay');
  }
});

// 建立案件表單的說明寫出建立前要確認的事：案件名稱與查核期間建立後不能修改、填錯時怎麼辦，
// 以及期末財報準備日可以之後按「修改案件資料」補填。只核對這些資訊，不逐字比對說明句。
test('the create form says what cannot change after creation and that the preparation date can be filled in later', () => {
  const f = createFixture();
  const note = f.page().querySelectorAll('.rule-card__sub').map(node => node.textContent).join('');
  for (const piece of ['查核期間', '不能修改', '新案件', '「財報準備日起核准」', '留空', '「修改案件資料」']) {
    assert.ok(note.includes(piece), piece + ' in: ' + note);
  }
  assert.doesNotMatch(note, /不會延長/);
});

test('a failed save of the case details marks the preparation date only when the backend names it', async () => {
  for (const { field, marked } of [{ field: 'prepStartDate', marked: 'prepStartDate' }, { field: 'databaseProvider', marked: null }, { field: undefined, marked: null }]) {
    const f = usageJourneyFixture('create', { redraw: true });
    const focus = recordFocus(f);
    f.store.setProject({ projectId: '合成案件甲', clientName: '合成客戶', projectCode: 'SYN-01', operatorId: '合成審計員',
      periodStart: '2025-01-01', periodEnd: '2025-12-31', prepStartDate: null, databaseProvider: 'sqlite' });
    f.click('[data-action="edit-project-metadata"]');
    let form = namedForm(f.$('[data-bind="project-update-form"]'));
    // 修改表單的標籤一樣不寫「（選填）」。「客戶名稱」與「案件編號」逐字比對；
    // 「期末財報準備日」只比對意思。列數與順序照樣鎖住。
    const updateLabels = form.querySelectorAll('.form__row').map(labelText);
    assert.equal(updateLabels.length, 3);
    assert.deepEqual(updateLabels.slice(0, 2), ['客戶名稱', '案件編號']);
    assert.match(updateLabels[2], /準備日/);
    assert.deepEqual(updateLabels.filter(label => /選填|（|\(/.test(label)), [], 'no label carries a bracketed note');
    fill(form, { clientName: ' 新合成客戶 ', projectCode: 'SYN-02', prepStartDate: '2025-12-20' });
    submit(form);
    const update = f.request('projectUpdate');
    assert.deepEqual(plain(update.payload), { clientName: '新合成客戶', projectCode: 'SYN-02', prepStartDate: '2025-12-20' });
    assert.ok(noDunderKeys(update.payload));
    const error = { code: 'invalid_payload', message: '合成原因：期末財報準備日。' };
    if (field) error.field = field;
    update.reject(error); await settle();

    form = namedForm(f.$('[data-bind="project-update-form"]'));
    const label = 'field ' + String(field);
    const summary = form.querySelector('[data-bind="project-update-error"]');
    assert.equal(summary.hidden, false, label);
    assert.equal(summary.textContent, '合成原因：期末財報準備日。', label);
    assert.deepEqual(form.querySelectorAll('[aria-invalid="true"]').map(node => node.getAttribute('name')), marked ? [marked] : [], label);
    if (marked) assert.equal(form[marked].getAttribute('aria-describedby'), 'project-update-error', label);
    assert.ok(focus.at(-1) === (marked ? form[marked] : summary), label + ': focus target');
    assert.equal(f.state.project.clientName, '合成客戶', label + ': the saved case details are unchanged');
  }
});

// Windows 身分晚到時只更新「目前帳號」那一行，不重繪整個表單，還沒儲存的輸入才不會被清掉。
test('a late Windows identity updates only the account line and keeps the typed case name, its selection and focus', () => {
  const f = remapAppFixture(); f.store.resetWorkflow(); f.store.setView('workflow');
  const step = f.store.STEPS[0];
  f.app.renderContent(f.state, step);
  const notice = () => f.$('[data-bind="create-operator"]').textContent;
  // 還沒有身分時說明是目前的 Windows 帳號（只比對意思）。
  assert.match(notice(), /Windows 帳號/);

  const input = f.$('[name="projectName"]');
  assert.ok(input !== null, 'case name input rendered');
  input.value = '未儲存的合成案件'; input.selectionStart = 2; input.selectionEnd = 5; input.focus();

  f.store.setCurrentUser({ shortName: '合成審計員', userNumber: 7, principal: 'SYNTHETIC\\合成審計員' });
  f.app.renderContent(f.state, step);
  assert.ok(f.$('[name="projectName"]') === input, 'the same input stays on screen');
  assert.equal(input.isConnected, true);
  assert.equal(input.value, '未儲存的合成案件');
  assert.equal(input.selectionStart, 2); assert.equal(input.selectionEnd, 5);
  assert.ok(f.dom.document.activeElement === input, 'focus stays in the case name');
  assert.match(notice(), /合成審計員/);

  // 身分沒有簡稱時仍說明是目前的 Windows 帳號，不顯示空白或 undefined。
  f.store.setCurrentUser({ userNumber: 8, principal: 'SYNTHETIC\\no-short-name' });
  f.app.renderContent(f.state, step);
  assert.ok(f.$('[name="projectName"]') === input, 'the same input stays on screen');
  assert.match(notice(), /Windows 帳號/);
  assert.doesNotMatch(notice(), /undefined|null|合成審計員/);
});

// 打開建立案件表單時，步驟標題的焦點排在下一輪才移過去。電腦忙的時候這一輪會晚到，使用者已經點進查核起始日，
// 標題才把焦點搶走，接著輸入的日期就落到標題上。延後移動焦點的期間，使用者自己點進別的欄位時，焦點留在那個欄位。
test('a field clicked before the step title takes focus keeps the focus, while an untouched form still focuses the title', () => {
  const opened = () => {
    const f = remapAppFixture({ queueTimers: true }); f.store.resetWorkflow(); f.store.setView('workflow');
    f.app.renderContent(f.state, f.store.STEPS[0]);
    return f;
  };
  const title = f => f.dom.document.activeElement !== null &&
    f.dom.document.activeElement.getAttribute('data-bind') === 'current-step-title';

  const f = opened();
  const date = f.$('[name="periodStart"]');
  assert.ok(date !== null, 'start date rendered');
  // 按下滑鼠先經過 document 的捕獲監聽，焦點接著落到欄位上，和瀏覽器的順序相同。
  date.dispatch('pointerdown');
  date.focus();
  f.timers.run();
  assert.ok(f.dom.document.activeElement === date, 'the step title does not take the focus from the clicked field');

  // 沒有動表單時，標題照樣拿到焦點，鍵盤使用者從標題開始。
  const g = opened();
  g.timers.run();
  assert.ok(title(g), 'the step title takes focus when the auditor has not touched the form');

  // 點在沒有控制項的地方，焦點仍在整個頁面上，標題照樣拿到焦點。
  const h = opened();
  h.$('[data-bind="create-form"]').dispatch('pointerdown');
  h.timers.run();
  assert.ok(title(h), 'clicking blank space does not keep the title from taking focus');
});

// =====================================================================================================
// 第二步匯入與行事曆
// =====================================================================================================

function importFixture() {
  const f = usageJourneyFixture('import', { redraw: true });
  recordFocus(f);
  return f;
}

function calendarRow(f) {
  const toggle = f.$('[data-task-toggle="calendar"]');
  assert.ok(toggle !== null, 'calendar task rendered');
  return toggle.closest('.import-task');
}

const calendarDone = f => calendarRow(f).querySelector('.import-task__mark--done') !== null;
const calendarMeta = f => calendarRow(f).querySelector('.import-task__meta').textContent;

test('choosing GL and TB source files offers exactly the seven supported file types and the guidance names them', async () => {
  const f = importFixture();
  // 第二步不出現這兩句說明。
  const panel = f.$('[data-motion-key="import-panel"]').textContent;
  assert.doesNotMatch(panel, /完整資料直接匯入案件資料庫|解鎖「非授權編製人員」/);
  // 頁首不常駐支援的檔案類型，只在選檔畫面說一次。
  assert.doesNotMatch(panel, /支援 Excel/);

  const guidanceOf = {};
  for (const [kind, name] of [['gl', /PBC-GL/], ['tb', /TB/]]) {
    f.click('[data-task-toggle="' + kind + '"]');
    f.click('[data-action="wizard-replace-' + kind + '"]');
    const pick = f.request('hostSelectFiles');
    assert.deepEqual(Object.keys(pick.payload).sort(), ['extensions', 'title']);
    // 選檔對話框的副檔名是前後端契約，精確比對。
    assert.deepEqual(plain(pick.payload.extensions), ['.xlsx', '.xlsm', '.xls', '.csv', '.txt', '.mdb', '.accdb']);
    assert.match(pick.payload.title, name);
    pick.resolve({ files: [] }); await settle();
    // 只比對意思：選檔畫面列出 Excel、CSV、文字檔與 Access。
    const guidance = f.$('.wizard-pane__hint').textContent;
    for (const word of [/Excel/, /CSV/, /文字檔/, /Access/]) assert.match(guidance, word);
    guidanceOf[kind] = guidance;
    f.click('[data-action="wizard-cancel"]');
  }
  // GL 與 TB 的選檔說明用同一套句子，只差在 GL 與 TB；TB 的說明不提期初、期末與查核期間。
  assert.equal(guidanceOf.tb, guidanceOf.gl.replace('建立 GL', '建立 TB'));
  assert.doesNotMatch(guidanceOf.tb, /期初|期末|查核期間/);
  assert.equal(f.requests.filter(r => r.action === 'hostSelectFiles').length, 2);
});

// TB 和 GL 走同一條多檔路徑。一次選兩個欄位相同的檔案，逐檔檢視後都列在待匯入清單，
// 「開始匯入」只送一個匯入 action、帶兩個來源；匯入後按「加入」再選的檔案以 append 送出。
// 還沒匯入時，GL 與 TB 的卡片都寫出可以合併欄位相同的檔案，選檔說明也是同一句。
test('GL and TB both send several files picked at once in one import and can add more files later', async () => {
  for (const kind of ['gl', 'tb']) {
    const f = importFixture();
    const method = kind === 'gl' ? 'importGlFromFile' : 'importTbFromFile';
    const fileName = name => kind + '-' + name + '.csv';
    const picked = name => ({ filePath: 'C:\\in\\' + fileName(name), fileName: fileName(name) });
    const sent = name => ({ ...picked(name), encoding: 'utf-8', delimiter: ',' });
    const card = () => f.$('[data-bind="import-card-' + kind + '"]').textContent;
    async function inspectEach(names) {
      for (const name of names) {
        const inspect = f.request('importInspectFile');
        assert.equal(inspect.payload.filePath, picked(name).filePath, 'files are inspected one by one in the picked order');
        inspect.resolve({ fileType: 'csv', columns: ['Code', 'Amount'], encoding: 'utf-8', delimiter: ',' }); await settle();
      }
    }

    f.click('[data-task-toggle="' + kind + '"]');
    assert.match(card(), /可合併欄位相同的檔案、工作表或資料表/);
    assert.doesNotMatch(card(), /期初|期末|查核期間/);
    f.click('[data-action="wizard-replace-' + kind + '"]');
    const pick = f.request('hostSelectFiles');
    assert.match(pick.payload.title, /可多選/);
    pick.resolve({ files: [picked('north'), picked('south')] }); await settle();
    await inspectEach(['north', 'south']);
    assert.match(f.$('.wizard-pane__hint').textContent, /選擇一個或多個來源檔/);
    assert.match(f.$('[data-action="wizard-confirm"]').textContent, /開始匯入（2 個來源）/);

    f.click('[data-action="wizard-confirm"]');
    const imports = () => f.requests.filter(r => r.action === method);
    assert.equal(imports().length, 1, 'both files go out in one import action');
    assert.deepEqual(plain(imports()[0].payload), { mode: 'replace', sources: [sent('north'), sent('south')] });
    const listed = (sourceNo, name, rowCount) => ({ sourceNo, fileName: fileName(name), sheetName: null, encoding: 'utf-8',
      delimiter: ',', headerRow: null, rowCount, importedUtc: '2026-11-09T08:00:00Z' });
    imports()[0].resolve({ batchId: kind + '-two-files', rowCount: 5, addedRowCount: 5, columns: ['Code', 'Amount'],
      sources: [listed(1, 'north', 3), listed(2, 'south', 2)], warnings: [], openElsewhereWarnings: [],
      invalidatedResults: { validation: true, prescreen: true, filter: true, filterScenarios: true },
      staleState: { validation: true, prescreen: true, filter: false }, reports: [] });
    await settle();
    assert.match(card(), /已匯入 5 列、2 欄（2 個來源）/);
    for (const name of ['north', 'south']) assert.ok(card().includes(fileName(name)), fileName(name) + ' is listed as a source');

    f.click('[data-action="wizard-append-' + kind + '"]');
    f.click('[data-action="wizard-pick"]');
    f.request('hostSelectFiles').resolve({ files: [picked('east')] }); await settle();
    await inspectEach(['east']);
    f.click('[data-action="wizard-confirm"]');
    assert.equal(imports().length, 2);
    assert.deepEqual(plain(imports()[1].payload), { mode: 'append', sources: [sent('east')] });
  }
});

// 四列的按鈕寫出這一列要做的事，還沒匯入寫「匯入」，國定假日與補班日還沒設定寫「設定」，
// 已經有資料寫「更改」；報讀名稱是按鈕的字接這一列的名稱。四列仍是同一種低調按鈕，見下一個測試。
test('each import task button says what the row needs: 匯入, 設定 or 更改', () => {
  const f = importFixture();
  const button = kind => f.$('[data-task-toggle="' + kind + '"]');
  const kinds = ['gl', 'tb', 'authorizedPreparer', 'calendar'];
  assert.deepEqual(kinds.map(kind => button(kind).textContent), ['匯入', '匯入', '匯入', '設定'], 'nothing imported yet');
  assert.deepEqual(kinds.map(kind => button(kind).getAttribute('aria-label')),
    ['匯入：GL (總帳明細)', '匯入：TB (試算表)', '匯入：授權編製人員清單', '設定：國定假日與補班日']);
  f.store.setImportResult('gl', { batchId: 'gl-1', columns: ['Doc'], rowCount: 3 });
  f.store.setCalendarState({ holidayCount: 3, makeupDayCount: 1, calendarImported: true, nonWorkingDays: [6, 0], nonWorkingDaysConfigured: true });
  assert.deepEqual(kinds.map(kind => button(kind).textContent), ['更改', '匯入', '匯入', '更改'], 'rows that have data offer a change');
  assert.equal(button('gl').getAttribute('aria-label'), '更改：GL (總帳明細)');
  assert.ok(kinds.every(kind => !/操作/.test(button(kind).textContent)), 'no row says only 操作');
});

// 第二步四列的按鈕是每列重複的同一種動作，要長得一樣，沒有哪一列另外加框。
// 展開中的那一列標出 aria-expanded，樣式由它保留淡藍底。嵌進列裡的匯入卡沒有獨立卡片的陰影與圓角。
test('every import task uses the same action button and an opened task shows no card edge', () => {
  const f = importFixture();
  const actions = ['gl', 'tb', 'authorizedPreparer', 'calendar'].map(kind => f.$('[data-task-toggle="' + kind + '"]'));
  assert.ok(actions.every(action => action !== null), 'all four tasks are listed');
  // 同功能的元件只留一套，展開用的按鈕都接共用的展開箭頭 jet-disclosure；四列仍是同一種按鈕。
  assert.deepEqual(actions.map(action => action.getAttribute('class')), Array(4).fill('import-task__action jet-disclosure'));
  f.click('[data-task-toggle="tb"]');
  assert.equal(f.$('[data-task-toggle="tb"]').getAttribute('aria-expanded'), 'true');
  assert.equal(f.$('[data-task-toggle="gl"]').getAttribute('aria-expanded'), 'false');
  const css = require('node:fs').readFileSync(require('node:path').join(__dirname, '../../JET/wwwroot/css/app.css'), 'utf8');
  assert.doesNotMatch(css, /import-task__action--primary/, 'no row action is styled apart from the others');
  const panelCard = css.match(/\.import-task__panel \.import-card \{([^}]*)\}/);
  assert.ok(panelCard !== null, 'the opened card has its own rule');
  assert.match(panelCard[1], /box-shadow: none;/);
  assert.match(panelCard[1], /border-radius: 0;/);
  // 這一句看展開中的按鈕有往下凹的樣式規則；它排在共用選擇器清單裡，後面接逗號或大括號都接受。
  assert.match(css, /\.import-task__action\[aria-expanded="true"\]\s*[,{]/);
});

test('a holiday file and a make-up day file with zero rows still complete the calendar task', async () => {
  const f = importFixture();
  assert.equal(calendarDone(f), false, 'nothing imported yet');
  f.click('[data-task-toggle="calendar"]');

  for (const [action, pickAction, importAction, fileName] of [
    ['import-holiday', 'hostSelectFile', 'importHolidayFromFile', 'synthetic-holiday.xlsx'],
    ['import-makeup', 'hostSelectFile', 'importMakeupDayFromFile', 'synthetic-makeup.xlsx']]) {
    const before = f.requests.filter(r => r.action === pickAction).length;
    f.click('[data-action="' + action + '"]');
    const picks = f.requests.filter(r => r.action === pickAction);
    assert.equal(picks.length, before + 1, action + ' opens a file dialog');
    assert.deepEqual(plain(picks.at(-1).payload.extensions), ['.xlsx']);
    picks.at(-1).resolve({ filePath: 'synthetic\\' + fileName, fileName }); await settle();
    const imported = f.request(importAction);
    assert.deepEqual(plain(imported.payload), { filePath: 'synthetic\\' + fileName, fileName });
    imported.resolve({ count: 0 }); await settle();
    assert.equal(calendarDone(f), true, action + ': a successful import with zero rows completes the task');
  }
  assert.match(calendarMeta(f), /國定假日 0 天/);
  assert.match(calendarMeta(f), /補班日 0 天/);
  assert.match(f.$('[data-bind="import-card-calendar"] .import-card__status').textContent, /國定假日 0 天.*補班日 0 天/);
});

test('making every weekday a working day is saved and the summary says there are no non-working days', async () => {
  const f = importFixture();
  f.click('[data-task-toggle="calendar"]');
  const boxes = () => f.dom.content.querySelectorAll('[data-bind="nonworking-days"] input[data-weekday]');
  assert.deepEqual(boxes().filter(box => box.checked).map(box => box.getAttribute('data-weekday')), ['6', '0'], 'Saturday and Sunday by default');
  for (const box of boxes()) box.checked = false;
  boxes()[0].fire('change');

  const save = f.request('calendarSetNonWorkingDays');
  assert.deepEqual(plain(save.payload), { days: [] });
  save.resolve({ nonWorkingDays: [] }); await settle();
  assert.equal(calendarDone(f), true, 'a saved weekday setting completes the task');
  // 只比對意思：整週都是工作日時，摘要寫非工作日為無。
  assert.match(calendarMeta(f), /每週休息日\s*無/);
  assert.deepEqual(boxes().filter(box => box.checked).map(box => box.getAttribute('data-weekday')), [], 'no weekday stays ticked');
});

test('saving the same non-working days in another order or with repeats keeps results, and a different set refreshes them', async () => {
  const f = importFixture();
  f.store.setCalendarState({ holidayCount: 3, makeupDayCount: 1, calendarImported: true, nonWorkingDays: [6, 0], nonWorkingDaysConfigured: true });
  f.click('[data-task-toggle="calendar"]');
  const box = day => { const node = f.$('[data-bind="nonworking-days"] input[data-weekday="' + day + '"]'); assert.ok(node !== null, 'weekday ' + day); return node; };

  for (const response of [[0, 6], [6, 0, 0], [0, 6, 6, 0]]) {
    const calendar = f.state.importState.calendar, generation = f.state.dataGeneration;
    box(6).fire('change');
    const save = f.requests.filter(r => r.action === 'calendarSetNonWorkingDays').at(-1);
    assert.deepEqual(plain(save.payload), { days: [6, 0] });
    save.resolve({ nonWorkingDays: response }); await settle();
    assert.equal(f.state.dataGeneration, generation, JSON.stringify(response) + ': previews and paged results are kept');
    assert.ok(f.state.importState.calendar === calendar, JSON.stringify(response) + ': the calendar state is not replaced');
  }

  const generation = f.state.dataGeneration;
  box(6).checked = false; box(6).fire('change');
  const save = f.requests.filter(r => r.action === 'calendarSetNonWorkingDays').at(-1);
  assert.deepEqual(plain(save.payload), { days: [0] });
  save.resolve({ nonWorkingDays: [0] }); await settle();
  assert.equal(f.state.dataGeneration, generation + 1, 'a different set refreshes previews and paged results once');
  assert.deepEqual(plain(f.state.importState.calendar), { holidayCount: 3, makeupDayCount: 1, calendarImported: true,
    nonWorkingDays: [0], nonWorkingDaysConfigured: true });
  assert.match(calendarMeta(f), /國定假日 3 天.*補班日 1 天.*每週休息日 日/);
});

// =====================================================================================================
// 訊息面板的句子
// 前面的測試刻意不斷言訊息面板；訊息面板的句子集中在這一段斷言。
// 訊息面板的句子逐字比對，失敗訊息裡的作業名稱除外；後端提醒與原因一律用合成字串。
// =====================================================================================================

const ELAPSED = /耗時(?: \d+\.\d 秒|不到 0\.1 秒|\d+ 分 \d+ 秒)/.source;
const messageList = f => plain(f.store.getState().messages.map(m => ({ text: m.text, level: m.level })));

test('messages: creating a case logs that it was created with each backend warning, and a failed create is an error line while the reason stays on the form', async () => {
  let f = createFixture(); f.store.setView('workflow');
  let form = f.page();
  fill(form, VALID_CREATE);
  submit(form);
  f.request('projectCreate').resolve({ projectId: '合成案件甲', warnings: ['合成：建案提醒。'] }); await settle();
  f.request('projectLoad').resolve({ project: { projectId: '合成案件甲', periodStart: '2025-01-01', periodEnd: '2025-12-31', databaseProvider: 'sqlite', currentStep: 0 } });
  await settle(); await settle();
  // 後端提醒原樣顯示，屬需要注意。
  assert.deepEqual(messageList(f).slice(0, 2), [{ text: '合成：建案提醒。', level: 'warn' }, { text: '案件「合成案件甲」已建立。', level: 'info' }]);
  assert.equal(messageList(f).length, 2, 'no other completion line');

  f = createFixture(); f.store.setView('workflow');
  form = f.page();
  fill(form, VALID_CREATE);
  submit(form);
  f.request('projectCreate').reject({ code: 'invalid_payload', field: 'projectName', message: '合成原因：名稱不可用。' }); await settle();
  // 原因結尾的句號去掉，括號後面加句號。
  // 作業名稱只確認提到建立案件。
  assert.match(messageList(f)[0].text, new RegExp('^[^：]*建立[^：]*案件[^：]*失敗：合成原因：名稱不可用（' + ELAPSED + '）。$'));
  assert.equal(messageList(f)[0].level, 'error');
  // 原因寫在表單上方的錯誤框，第一步不另加提示框。
  assert.equal(f.store.getState().operationFailure, null);
});

test('messages: saving the case details logs that they were updated with each backend warning, and a failed save is an error line', async () => {
  const setup = () => {
    const f = usageJourneyFixture('create', { redraw: true });
    recordFocus(f);
    f.store.setView('workflow');
    f.store.setProject({ projectId: '合成案件甲', clientName: '合成客戶', projectCode: 'SYN-01', operatorId: '合成審計員',
      periodStart: '2025-01-01', periodEnd: '2025-12-31', prepStartDate: null, databaseProvider: 'sqlite' });
    f.click('[data-action="edit-project-metadata"]');
    const form = namedForm(f.$('[data-bind="project-update-form"]'));
    fill(form, { clientName: '新合成客戶', projectCode: 'SYN-02', prepStartDate: '2025-12-20' });
    submit(form);
    return f;
  };
  let f = setup();
  f.request('projectUpdate').resolve({ project: { projectId: '合成案件甲', clientName: '新合成客戶', projectCode: 'SYN-02', operatorId: '合成審計員',
    periodStart: '2025-01-01', periodEnd: '2025-12-31', prepStartDate: '2025-12-20', databaseProvider: 'sqlite' },
  warnings: ['合成：準備日提醒。'], reports: [] });
  await settle(); await settle();
  assert.deepEqual(messageList(f).slice(0, 2), [{ text: '合成：準備日提醒。', level: 'warn' }, { text: '案件資料已更新。', level: 'info' }]);
  assert.equal(messageList(f).length, 2, 'no other completion line');

  f = setup();
  f.request('projectUpdate').reject({ code: 'invalid_payload', message: '合成原因：期末財報準備日。' }); await settle();
  // 作業名稱只確認提到儲存案件資料；其餘句型逐字比對。
  assert.match(messageList(f)[0].text, new RegExp('^[^：]*儲存[^：]*案件[^：]*失敗：合成原因：期末財報準備日（' + ELAPSED + '）。$'));
  assert.equal(messageList(f)[0].level, 'error');
  assert.equal(f.store.getState().operationFailure, null, 'the reason is on the form, not in a step notice');
});

test('messages: holiday and make-up day imports log their day counts with the elapsed time, choosing no file logs nothing, and only a real change of non-working days is logged', async () => {
  const f = importFixture();
  f.click('[data-task-toggle="calendar"]');
  for (const [action, importAction, count, expected] of [
    ['import-holiday', 'importHolidayFromFile', 3, '國定假日清單上傳完成：3 天'],
    ['import-makeup', 'importMakeupDayFromFile', 1, '補班日清單上傳完成：1 天']]) {
    f.click('[data-action="' + action + '"]');
    f.requests.filter(r => r.action === 'hostSelectFile').at(-1).resolve({ filePath: 'synthetic\\calendar.xlsx', fileName: 'calendar.xlsx' }); await settle();
    f.request(importAction).resolve({ count }); await settle(); await settle();
    // 這兩句的句尾接上耗時。
    assert.match(messageList(f)[0].text, new RegExp('^' + expected + '，' + ELAPSED + '。$'));
    assert.equal(messageList(f)[0].level, 'info');
  }
  // 沒有選檔：這次其實沒有做事，不記。
  const before = messageList(f).length;
  f.click('[data-action="import-holiday"]');
  f.requests.filter(r => r.action === 'hostSelectFile').at(-1).resolve({ filePath: '', fileName: '' }); await settle(); await settle();
  assert.equal(messageList(f).length, before, 'choosing no file logs nothing');

  // 非工作日真的改了才記；送出同一組（只是順序不同）不記。
  f.store.setCalendarState({ holidayCount: 3, makeupDayCount: 1, calendarImported: true, nonWorkingDays: [6, 0], nonWorkingDaysConfigured: true });
  const box = day => f.$('[data-bind="nonworking-days"] input[data-weekday="' + day + '"]');
  box(6).fire('change');
  f.requests.filter(r => r.action === 'calendarSetNonWorkingDays').at(-1).resolve({ nonWorkingDays: [0, 6] }); await settle(); await settle();
  assert.equal(messageList(f).length, before, 'the same days in another order log nothing');
  box(6).checked = false; box(6).fire('change');
  f.requests.filter(r => r.action === 'calendarSetNonWorkingDays').at(-1).resolve({ nonWorkingDays: [0] }); await settle(); await settle();
  assert.deepEqual(messageList(f)[0], { text: '已更新每週休息日設定。', level: 'info' });
  assert.equal(messageList(f).length, before + 1);
});

// 國定假日清單或補班日清單正被 Excel 開著時照樣匯入，開著檔案的提醒放在卡片的匯入結果旁，
// 訊息面板也另記一則。同一份清單再匯入、檔案沒開著時，卡片上那一則提醒就消失，另一份清單的提醒不受影響。
test('a holiday or make-up day list held open by Excel shows its reminder in the calendar card until that list is imported again cleanly', async () => {
  const f = importFixture();
  f.click('[data-task-toggle="calendar"]');
  const held = name => '檔案「' + name + '」正被其他程式開啟（例如 Excel）。JET 匯入的是這個檔案最後一次存檔的內容，尚未存檔的修改沒有匯入。';
  const importList = async (action, importAction, fileName, openElsewhereWarnings) => {
    f.click('[data-action="' + action + '"]');
    f.requests.filter(r => r.action === 'hostSelectFile').at(-1).resolve({ filePath: 'synthetic\\' + fileName, fileName }); await settle();
    f.request(importAction).resolve({ count: 2, warnings: openElsewhereWarnings.slice(), openElsewhereWarnings }); await settle(); await settle();
  };
  const notices = () => f.dom.content.querySelectorAll('[data-bind="import-card-calendar"] [data-open-elsewhere]').map(node => node.textContent);

  await importList('import-holiday', 'importHolidayFromFile', '合成國定假日.xlsx', [held('合成國定假日.xlsx')]);
  await importList('import-makeup', 'importMakeupDayFromFile', '合成補班日.xlsx', [held('合成補班日.xlsx')]);
  assert.deepEqual(notices(), [held('合成國定假日.xlsx'), held('合成補班日.xlsx')]);
  const holidayNotice = f.$('[data-bind="import-card-calendar"] [data-open-elsewhere="holiday"]');
  assert.ok(holidayNotice.classList.contains('form-notice--warning'), 'an amber notice, not an error');
  assert.equal(holidayNotice.getAttribute('role'), 'status');
  assert.ok(messageList(f).some(m => m.text === held('合成補班日.xlsx') && m.level === 'warn'), 'the message panel keeps the record');

  await importList('import-holiday', 'importHolidayFromFile', '合成國定假日.xlsx', []);
  assert.deepEqual(notices(), [held('合成補班日.xlsx')]);
});

// =====================================================================================================
// 補充　建立與修改案件的表單在中央面板重畫之後
// =====================================================================================================
//
// 使用者的操作：建立案件的表單填到一半，別的作業失敗，步驟上方出現失敗提示；接著送出表單，後端指出某一欄有問題；
// 修正後回首頁，再按「新增案件」。修改案件資料時，步驟上方還留著失敗提示就按儲存，後端回報存不進去。
// 這兩個測試用整個 JET 畫面（共用夾具 bootFullApp）：中央面板由正式的 app.js 重畫，步驟上方的失敗提示出現與拿掉時
// 表單整塊換掉，所以看得出已填的值與錯誤框是不是還在換上來的那一張表單上。

// 目前步驟畫在畫面上的那一塊；每次都重新找，不留著舊的。
function shownStep(f) { const node = f.$('.stepflow-item--current .stepflow-item__body'); assert.ok(node !== null, 'the current step is shown'); return node; }
// 讓一項合成作業失敗：目前步驟的標題下方出現步驟失敗提示。
async function leaveStepFailure(f) {
  const done = f.ui.run('合成作業', () => f.api.systemPing({}));
  f.fail(f.sent('system.ping').at(-1), { code: 'synthetic', message: '合成作業原因。' }); await done; await flush();
  assert.ok(f.$('.stepflow-item__failure') !== null, 'a step failure notice is showing');
}
// 和瀏覽器一樣，改了欄位的值就送出會冒泡的 input 事件。
function typeInto(field, value) { field.value = value; field.dispatch('input'); }

test('the create form keeps what was typed and its error through redraws, and shows a failure on the form that is on screen', async () => {
  const f = await bootFullApp([]);
  f.click('[data-action="picker-new"]'); await flush();
  const form = () => namedForm(shownStep(f).querySelector('[data-bind="create-form"]'));
  const typed = { projectName: '合成新案', projectCode: 'SYN-09', clientName: '合成客戶股份有限公司',
    periodStart: '2025-01-01', periodEnd: '2025-12-31', prepStartDate: '2027-06-30' };
  const values = () => Object.fromEntries(Object.keys(typed).map(name => [name, form()[name].value]));
  const warning = () => { const node = form().querySelector('[data-bind="preparation-date-warning"]'); return node.hidden ? '' : node.textContent; };
  const box = () => { const node = form().querySelector('[data-bind="create-error"]'); return node.hidden ? '' : node.textContent; };
  const invalid = () => form().querySelectorAll('[aria-invalid="true"]').map(node => node.getAttribute('name'));
  for (const [name, value] of Object.entries(typed)) typeInto(form()[name], value);
  // 期末財報準備日晚於查核截止日超過一年：提醒顯示（只比對意思）。
  assert.match(warning(), /確認年份/);

  // 別的作業失敗，步驟上方出現失敗提示，表單整塊重畫：已填的值與提醒都還在。
  await leaveStepFailure(f);
  assert.deepEqual(values(), typed, 'what was typed is still on the form after the step was redrawn');
  assert.match(warning(), /確認年份/, 'the reminder is drawn again');

  // 送出：上一次的步驟失敗提示先拿掉（表單又重畫一次），後端指出案件名稱。原因寫在現在這張表單上，焦點到那一欄。
  form().dispatch('submit', { target: form() });
  const create = f.request('project.create');
  assert.deepEqual(plain(create.payload), typed);
  assert.ok(f.$('.stepflow-item__failure') === null, 'sending takes the earlier step notice away, which redraws the form');
  f.fail(create, { code: 'invalid_payload', field: 'projectName', message: '合成：案件名稱已經存在。' }); await flush();
  assert.equal(box(), '合成：案件名稱已經存在。', 'the reason is on the form that is on screen');
  assert.deepEqual(invalid(), ['projectName']);
  assert.equal(f.state().operationFailure, null, 'the reason is on the form, so there is no step notice');
  f.timers.run();
  assert.ok(f.dom.document.activeElement === form().projectName, 'the focus goes to the field on screen');
  // 又重畫一次：原因、標示與已填的值都還在。
  f.store.touch(); await flush();
  assert.equal(box(), '合成：案件名稱已經存在。', 'the reason is still there after a redraw');
  assert.deepEqual(invalid(), ['projectName']);
  assert.deepEqual(values(), typed);

  // 重新輸入：原因收起，重畫也不回來；新打的值留著。
  typeInto(form().projectName, '合成新案二');
  assert.equal(box(), '');
  f.store.touch(); await flush();
  assert.equal(box(), '', 'a redraw does not bring the cleared reason back');
  assert.equal(form().projectName.value, '合成新案二');

  // 送出前就看得出的問題（查核起始日晚於截止日）也一樣留到重畫之後。
  typeInto(form().periodStart, '2026-01-01');
  form().dispatch('submit', { target: form() });
  assert.equal(f.sent('project.create').length, 1, 'nothing more is sent');
  assert.match(box(), /查核起始日.*晚於.*截止日/);
  f.store.touch(); await flush();
  assert.match(box(), /查核起始日.*晚於.*截止日/, 'the problem is still shown after a redraw');
  assert.deepEqual(invalid(), ['periodStart']);

  // 回首頁再按「新增案件」：是一張空白的新表單，沒有上一次填的值與原因。
  f.click('[data-action="app-back-picker"]'); await flush();
  f.reply('project.releaseLock', {}); await flush();
  f.reply('project.listLocal', { projects: [] }); await flush();
  f.click('[data-action="picker-new"]'); await flush();
  assert.deepEqual(values(), { projectName: '', projectCode: '', clientName: '', periodStart: '', periodEnd: '', prepStartDate: '' });
  assert.equal(box(), '');
  assert.equal(warning(), '');
});

test('a failed save of the case details is shown on the edit form that is on screen', async () => {
  const CASE = { projectId: '合成案件', databaseProvider: 'sqlite' };
  const f = await bootFullApp([CASE]);
  f.click('[data-action="picker-open"]');
  f.reply('project.load', { project: { ...CASE, clientName: '合成客戶', periodStart: '2025-01-01', periodEnd: '2025-12-31', currentStep: 0 } }); await flush();
  f.reply('log.recent', { messages: [] }); await flush();
  const form = () => namedForm(shownStep(f).querySelector('[data-bind="project-update-form"]'));
  const box = () => { const node = form().querySelector('[data-bind="project-update-error"]'); return node.hidden ? '' : node.textContent; };
  f.click('[data-action="edit-project-metadata"]'); await flush();
  typeInto(form().clientName, '新合成客戶');
  await leaveStepFailure(f);
  assert.equal(form().clientName.value, '新合成客戶', 'what was typed is still on the form after the step was redrawn');
  form().dispatch('submit', { target: form() });
  const update = f.request('project.update');
  assert.ok(f.$('.stepflow-item__failure') === null, 'sending takes the earlier step notice away, which redraws the form');
  f.fail(update, { code: 'synthetic', message: '合成：案件資料存不進去。' }); await flush();
  assert.equal(box(), '合成：案件資料存不進去。', 'the reason is on the form that is on screen');
  assert.equal(form().clientName.value, '新合成客戶');
  assert.equal(f.state().operationFailure, null, 'the reason is on the form, so there is no step notice');
});

// =====================================================================================================
// 補充　案件建立成功，接著的開啟沒有成功
// =====================================================================================================
//
// 使用者的操作：填表按「建立案件」，案件建好了，接著開啟它時失敗，或使用者按了「取消作業」。
// 畫面回到案件清單，清單上方說明案件已經建立，使用者從清單開啟它。JET 放不掉這個案件時留在原畫面，使用者再按一次「建立案件」。
// 用整個 JET 畫面（共用夾具 bootFullApp）：建立、開啟、放掉案件與重讀案件清單的請求都由正式的 jet-api.js 送出，看得到先後順序。

const NEW_CASE = { projectId: '合成新案', databaseProvider: 'sqlite' };
const NEW_CASE_LOADED = { project: { ...NEW_CASE, periodStart: '2025-01-01', periodEnd: '2025-12-31', currentStep: 0 } };

// 填表建立「合成新案」，後端回覆已建立；回傳時開啟這個案件的請求已經送出、還沒回覆。
async function createCaseAwaitingOpen() {
  const f = await bootFullApp([]);
  f.click('[data-action="picker-new"]'); await flush();
  const form = namedForm(shownStep(f).querySelector('[data-bind="create-form"]'));
  for (const [name, value] of Object.entries({ projectName: '合成新案', periodStart: '2025-01-01', periodEnd: '2025-12-31' })) typeInto(form[name], value);
  form.dispatch('submit', { target: form });
  f.reply('project.create', { projectId: '合成新案', warnings: [] }); await flush();
  assert.deepEqual(plain(f.request('project.load').payload), { projectId: '合成新案' });
  return f;
}
// 案件清單上方的琥珀色提示，沒有時是 null；紅色錯誤框是另一個元素。
function listNotice(f) { const node = f.$('.picker-feedback--warning'); return node === null ? null : { text: node.textContent, role: node.getAttribute('role') }; }
function caseActions(f) { return f.posted.map(request => request.action).filter(action => action.startsWith('project.')); }
function onCaseList(f) { return f.state().view === 'picker' && f.$('[data-bind="project-picker"]').hidden === false && f.$('[data-bind="app-body"]').hidden === true; }
function listedCases(f) { return f.$('[data-bind="project-picker"]').querySelectorAll('[data-action="picker-open"]').map(node => node.getAttribute('data-project-id')); }

test('a case that was created but failed to open is released and listed, and the case list says it can be opened from there', async () => {
  const f = await createCaseAwaitingOpen();
  f.fail('project.load', { code: 'synthetic', message: '合成：案件資料庫打不開。' }, 'open-after-create'); await flush();

  // JET 先放掉這個案件，放掉以後才回到案件清單重讀清單。
  assert.deepEqual(plain(f.request('project.releaseLock').payload), {});
  assert.equal(f.sent('project.listLocal').length, 1, 'the case list is not reloaded before the case is released');
  f.reply('project.releaseLock', {}); await flush();
  assert.equal(onCaseList(f), true, 'the screen is the case list, not the create form');
  f.reply('project.listLocal', { projects: [NEW_CASE] }); await flush();
  assert.equal(f.state().busy, false);
  assert.deepEqual(caseActions(f), ['project.listLocal', 'project.create', 'project.load', 'project.releaseLock', 'project.listLocal']);
  assert.deepEqual(listedCases(f), ['合成新案'], 'the new case is in the list');

  // 提示寫案件已經建立、為什麼沒有開啟，以及接下來能做什麼；它不是紅色的錯誤框。
  assert.deepEqual(listNotice(f), { text: '案件「合成新案」已建立，但沒有開啟成功：合成：案件資料庫打不開。請從案件清單開啟這個案件。', role: 'status' });
  assert.ok(f.$('.picker-feedback') === null, 'no error box: the case was created');
  assert.deepEqual(plain(f.state().pickerFeedbackContext),
    { projectId: '合成新案', errorCode: 'synthetic', correlationId: 'open-after-create', devExportedPath: null });
  // 訊息面板：建立是成功的，失敗的是開啟。
  assert.ok(f.messages().some(m => m.text === '案件「合成新案」已建立。' && m.level === 'info'), 'the creation is logged as done');
  assert.deepEqual(f.messages().filter(m => m.level === 'error').map(m => /^[^：]*開啟[^：]*失敗：合成：案件資料庫打不開（/.test(m.text)), [true]);
  assert.deepEqual(f.messages().filter(m => /建立[^：]*失敗/.test(m.text)), [], 'nothing says the creation failed');

  // 從清單開啟：只送開啟，不再建立一次；提示拿掉。
  f.click('[data-action="picker-open"]');
  assert.deepEqual(plain(f.request('project.load').payload), { projectId: '合成新案' });
  assert.equal(f.sent('project.load').length, 2);
  assert.equal(f.sent('project.create').length, 1, 'the case is not created a second time');
  assert.equal(listNotice(f), null);
  f.reply('project.load', NEW_CASE_LOADED); await flush();
  f.reply('log.recent', { messages: [] }); await flush();
  assert.equal(f.state().view, 'workflow');
  assert.equal(f.state().project.projectId, '合成新案');
});

test('exporting the diagnostic log for a created case that failed to open leaves the case list notice as it was', async () => {
  const f = await createCaseAwaitingOpen();
  // Debug 組建才有頁尾的「輸出診斷日誌」。
  f.store.setDevToolsEnabled(true); await flush();
  f.fail('project.load', { code: 'synthetic', message: '合成：案件資料庫打不開。' }); await flush();
  f.reply('project.releaseLock', {}); await flush();
  f.reply('project.listLocal', { projects: [NEW_CASE] }); await flush();
  const notice = { text: '案件「合成新案」已建立，但沒有開啟成功：合成：案件資料庫打不開。請從案件清單開啟這個案件。', role: 'status' };
  assert.deepEqual(listNotice(f), notice);

  f.click('[data-action="picker-dev-log-export"]');
  assert.deepEqual(plain(f.request('dev.log.exportFile').payload), { projectId: '合成新案', correlationId: null });
  f.reply('dev.log.exportFile', { filePath: 'C:/synthetic/合成新案/dev-log.txt', lineCount: 3, source: 'fileSink' }); await flush();

  // 匯出成功只在頁尾多寫檔案位置；清單上方仍是同一句琥珀色提示，不變成紅色的錯誤框。
  assert.equal(f.state().busy, false);
  assert.equal(f.$('.picker-footer__result').textContent, '診斷日誌已輸出：C:/synthetic/合成新案/dev-log.txt');
  assert.ok(f.$('.picker-feedback') === null, 'no error box: the case was created');
  assert.deepEqual(listNotice(f), notice, 'the notice is still the amber one');
  assert.ok(f.$('[data-action="picker-dev-log-export"]') !== null, 'the log can be exported again');

  // 從清單開啟又失敗時是紅色的錯誤框；這時匯出成功，錯誤框與裡面的原因也照樣留著。
  f.click('[data-action="picker-open"]');
  f.fail('project.load', { code: 'synthetic', message: '合成：案件資料庫打不開。' }); await flush();
  assert.equal(listNotice(f), null);
  const reason = f.$('.picker-feedback__body').textContent;
  assert.match(reason, /^開啟案件失敗：合成：案件資料庫打不開/);
  f.click('[data-action="picker-dev-log-export"]');
  f.reply('dev.log.exportFile', { filePath: 'C:/synthetic/合成新案/dev-log-2.txt', lineCount: 5, source: 'fileSink' }); await flush();
  assert.equal(f.$('.picker-footer__result').textContent, '診斷日誌已輸出：C:/synthetic/合成新案/dev-log-2.txt');
  assert.equal(f.$('.picker-feedback__body').textContent, reason, 'the error box still gives the reason');
  assert.equal(listNotice(f), null, 'the error box is not turned into a notice');
});

test('a case that was created but whose opening was cancelled, or met another running operation, is listed without any error', async () => {
  for (const [name, error] of [['cancelled', { code: 'operation_cancelled', message: '合成：已取消' }],
    ['another operation running', { code: 'operation_in_progress', message: '合成：另一項作業還在進行' }]]) {
    const f = await createCaseAwaitingOpen();
    f.fail('project.load', error); await flush();
    f.reply('project.releaseLock', {}); await flush();
    f.reply('project.listLocal', { projects: [NEW_CASE] }); await flush();

    assert.equal(onCaseList(f), true, name + ': the screen is the case list');
    assert.equal(f.state().busy, false, name);
    assert.deepEqual(listedCases(f), ['合成新案'], name);
    assert.deepEqual(listNotice(f), { text: '案件「合成新案」已建立，尚未開啟。請從案件清單開啟這個案件。', role: 'status' }, name);
    assert.ok(f.$('.picker-feedback') === null, name + ': no error box');
    assert.equal(f.state().pickerFeedbackContext, null, name + ': no failure is recorded for the case');
    assert.deepEqual(f.messages().filter(m => m.level === 'error'), [], name + ': nothing is logged as an error');
    assert.equal(f.sent('project.create').length, 1, name);
    assert.equal(f.sent('project.releaseLock').length, 1, name);
  }
});

test('when the created case cannot be released the screen stays, and pressing create again opens that case instead of creating another', async () => {
  const f = await createCaseAwaitingOpen();
  f.fail('project.load', { code: 'synthetic', message: '合成：案件資料庫打不開。' }); await flush();
  f.fail('project.releaseLock', { code: 'synthetic', message: '合成：案件鎖沒有釋放' }); await flush();

  // 和按「回首頁」放不掉案件時一樣：留在原畫面，訊息面板寫無法離開。
  assert.equal(f.state().view, 'workflow');
  assert.equal(f.state().busy, false);
  assert.deepEqual(f.messages()[0], { text: '無法離開案件，已保留目前畫面：合成：案件鎖沒有釋放', level: 'error' });
  assert.equal(f.sent('project.listLocal').length, 1, 'the case list is not reloaded');
  assert.equal(f.state().pickerFeedback, null, 'nothing is written for a case list that is not shown');

  const pressCreate = () => { const form = namedForm(shownStep(f).querySelector('[data-bind="create-form"]')); form.dispatch('submit', { target: form }); };
  // 別的作業還在進行時按「建立案件」：這次不會開始，不開啟、不放掉案件，也不離開畫面。
  const held = f.ui.run('合成作業', () => f.api.systemPing({}));
  pressCreate(); await flush();
  assert.deepEqual(caseActions(f), ['project.listLocal', 'project.create', 'project.load', 'project.releaseLock'], 'nothing more is sent while another operation runs');
  assert.equal(f.state().view, 'workflow');
  f.reply(f.sent('system.ping').at(-1), { devToolsEnabled: false }); await held; await flush();

  // 再按一次「建立案件」：案件已經建好，只再開啟一次，不送出第二次建立。
  pressCreate();
  assert.equal(f.sent('project.create').length, 1, 'the case is not created a second time');
  assert.equal(f.sent('project.load').length, 2, 'the created case is opened again');
  assert.deepEqual(plain(f.request('project.load').payload), { projectId: '合成新案' });
  f.reply('project.load', NEW_CASE_LOADED); await flush();
  assert.equal(f.state().project.projectId, '合成新案');
  assert.ok(shownStep(f).querySelector('[data-bind="create-form"]') === null, 'the form is replaced by the case summary');
});

// =====================================================================================================
// 補充　從案件清單開啟案件，後端已經開啟，畫面沒有開成
// =====================================================================================================
//
// 使用者的操作：在案件清單按「開啟」，後端已經開啟這個案件、握著它的案件鎖，畫面套用到一半出錯。
// JET 先放掉這個案件再回到案件清單，清單上方的紅色錯誤框寫出原因，使用者可以再開一次。放不掉時和按「回首頁」放不掉一樣，留在原畫面。
// 後端自己拒絕開啟時，後端沒有開著這個案件，不送放掉案件的請求。

// 畫面套用開啟的案件、跳到上次所在的步驟時出錯一次，之後照常。
function failNextStepChange(f, message) {
  const setStepIndex = f.store.setStepIndex;
  f.store.setStepIndex = () => { f.store.setStepIndex = setStepIndex; throw new Error(message); };
}

test('a case opened from the list that the screen could not show is released, and the case list gives the reason', async () => {
  const f = await bootFullApp([NEW_CASE]);

  // 後端拒絕開啟：不送放掉案件的請求，紅色錯誤框寫原因。
  f.click('[data-action="picker-open"]');
  f.fail('project.load', { code: 'synthetic', message: '合成：案件在另一個視窗開著。' }); await flush();
  assert.deepEqual(caseActions(f), ['project.listLocal', 'project.load']);
  assert.equal(f.state().busy, false);
  assert.match(f.$('.picker-feedback__body').textContent, /^開啟案件失敗：合成：案件在另一個視窗開著/);

  // 後端開好了，畫面沒有開成：先放掉這個案件，放掉以後才回到案件清單重讀清單。
  failNextStepChange(f, '合成：畫面套用失敗');
  f.click('[data-action="picker-open"]');
  f.reply('project.load', NEW_CASE_LOADED); await flush();
  assert.deepEqual(plain(f.request('project.releaseLock').payload), {});
  assert.equal(f.sent('project.listLocal').length, 1, 'the case list is not reloaded before the case is released');
  assert.equal(f.sent('log.recent').length, 0, 'the message history of a case that did not open is not restored');
  f.reply('project.releaseLock', {}); await flush();
  assert.equal(onCaseList(f), true, 'the screen is the case list');
  f.reply('project.listLocal', { projects: [NEW_CASE] }); await flush();
  assert.equal(f.state().busy, false);
  assert.equal(f.state().project, null);
  assert.deepEqual(caseActions(f), ['project.listLocal', 'project.load', 'project.load', 'project.releaseLock', 'project.listLocal']);
  assert.deepEqual(listedCases(f), ['合成新案']);
  // 開啟失敗是紅色的錯誤框，不是琥珀色提示。
  assert.match(f.$('.picker-feedback__body').textContent, /^開啟案件失敗：合成：畫面套用失敗/);
  assert.equal(listNotice(f), null);
  assert.equal(f.state().pickerFeedbackContext.projectId, '合成新案');

  // 再開一次照常開啟。
  f.click('[data-action="picker-open"]');
  f.reply('project.load', NEW_CASE_LOADED); await flush();
  f.reply('log.recent', { messages: [] }); await flush();
  assert.equal(f.state().view, 'workflow');
  assert.equal(f.state().project.projectId, '合成新案');
  assert.equal(f.sent('project.releaseLock').length, 1);
});

test('when a case opened from the list cannot be released the screen stays and says why', async () => {
  const f = await bootFullApp([NEW_CASE]);
  failNextStepChange(f, '合成：畫面套用失敗');
  f.click('[data-action="picker-open"]');
  f.reply('project.load', NEW_CASE_LOADED); await flush();
  f.fail('project.releaseLock', { code: 'synthetic', message: '合成：案件鎖沒有釋放' }); await flush();

  // 和按「回首頁」放不掉案件時一樣：留在原畫面，訊息面板寫無法離開。
  assert.equal(f.state().view, 'workflow');
  assert.equal(f.state().busy, false);
  assert.deepEqual(f.messages()[0], { text: '無法離開案件，已保留目前畫面：合成：案件鎖沒有釋放', level: 'error' });
  assert.equal(f.sent('project.listLocal').length, 1, 'the case list is not reloaded');
  assert.equal(f.state().pickerFeedback, null, 'nothing is written for a case list that is not shown');
});
