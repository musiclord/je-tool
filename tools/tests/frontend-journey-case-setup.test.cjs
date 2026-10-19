'use strict';
// 情境 2：第一步建立與修改案件；情境 4：第二步匯入與行事曆（收尾四項第一項第 6 批，2026-10-13）。
//
// 審計員的操作：
//   情境 2　填表建立案件；後端指出某一欄、沒指出欄位、指出不在白名單的欄位時各失敗一次；修改案件資料時失敗；
//           填表到一半 Windows 身分才送到；建好以後在頁首與案件清單看到案件名稱與客戶名稱。
//   情境 4　選 GL 與 TB 來源檔；匯入 0 筆的假日檔與補班檔；把整週都設成工作日；重存同一組非工作日但順序不同或重複；改成不同的非工作日。
// 夾具：tools/tests/support/frontend-fixtures.cjs 的 usageJourneyFixture（正式 state、ui-core、各步驟腳本，可訂閱重繪）
// 與 remapAppFixture（另加上 app.js 的頁首、案件清單與中欄重繪）。計畫原本寫的 projectMetadataFixture 與 mp 的建案夾具
// 都是手寫的假表單，這裡改用 usageJourneyFixture 的假 DOM 畫正式的表單，畫出來的欄位、屬性與焦點都看得到。
//
// 取代的 C# 字串測試（src/JET/tests/JET.Tests/Architecture/）：
//   ApprovedUxFrontendTests.CreateProjectFailure_UsesStructuredBackendFieldWithoutGuessingFromCodeOrMessage
//     （jet-api 把 wire 錯誤轉成 err.field 的部分由 frontend-journey-transport.test.cjs 的情境 24 負責）
//   UserFeedbackStepsOneToFourFrontendTests.CreateForm_OnlyCollectsOptionalCaseMetadataAndDoesNotCollectOperator
//     （流程總覽標題的 O8 部分由 frontend-journey-overview.test.cjs 的情境 18 負責）
//   UserFeedbackStepsOneToFourFrontendTests.CreateForm_OperatorNoticeUpdatesWithoutRebuildingTheFormWhenIdentityArrives
//   UserFeedbackStepsOneToFourFrontendTests.ImportGuidance_UsesBriefCopyAndRetainsSupportedFileTypes（84ch 不搬，見計畫第 4 節）
//   FrontendHardeningContractTests.NonWorkingDayEditor_InvalidatesFrontendResultsOnlyWhenCanonicalValueChanges
//   FrontendHardeningContractTests.CalendarTask_UsesPersistedCompletionMarkersInsteadOfPositiveRowCounts
//
// 這一批刻意不斷言：訊息面板的句子（建案成功、非工作日已更新或沒有變更、匯入完成等）、步驟提示框與操作名稱，
// 它們正由文字批次 B 改寫，留給第 10 批。後端原因句子只當作原樣顯示的合成字串，不比對正式句子。
// 斷言的失敗值一律不是假 DOM 節點（節點只用 === 轉成布林）；預期值一律寫死；action payload 用 deepEqual 精確比對，並確認沒有 `__` 鍵。
const test = require('node:test');
const assert = require('node:assert/strict');
const { usageJourneyFixture, remapAppFixture, settle, plain, readWwwroot, noDunderKeys } = require('./support/frontend-fixtures.cjs');

// 瀏覽器的 form 可以用欄位名稱直接取到欄位（form.caseName）；create-step.js 送出時這樣讀，假 DOM 沒有，這裡補上。
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

const VALID_CREATE = { caseName: '合成案件甲', projectCode: 'SYN-01', entityName: '合成客戶股份有限公司',
  periodStart: '2025-01-01', periodEnd: '2025-12-31', lastPeriodStart: '2025-12-20', databaseProvider: 'duckdb' };

function labelText(row) { return row.querySelector('.form__label').textContent.replace(/\s*\*$/, ''); }

// =====================================================================================================
// 情境 2　第一步建立與修改案件
// =====================================================================================================

test('journey 2: the create form collects the case details without an operator field and labels carry no bracketed notes', () => {
  const f = createFixture();
  f.store.setCurrentUser({ shortName: '合成審計員', userNumber: 7 });
  const form = f.page();

  assert.ok(form.querySelector('[name="operatorId"]') === null, 'the form has no operator field');
  const operator = form.querySelector('[data-bind="create-operator"]');
  assert.ok(operator !== null, 'the current account is shown as a notice');
  assert.ok(operator.querySelector('input') === null, 'the current account cannot be typed in');
  assert.match(operator.textContent, /合成審計員/);

  // 使用者 2026-10-13 勾選「第一步標籤拿掉括號說明」，選項說明（docs/history/specs/2026-10-06-kct-gl-requirements-plan.md
  // 「收尾四項：調查與裁定（2026-10-13）」的「寫法統一」列）原文：「第一步「案件編號（選填）」「客戶名稱（選填）」拿掉（選填），
  // 因為必填欄已經用星號標示；「資料儲存方式（建立後不可變更）」的限制改成欄位下方的一句說明。」三個標籤逐字比對。
  const rows = form.querySelectorAll('.form__row');
  const labels = rows.map(labelText);
  for (const label of ['案件編號', '客戶名稱', '資料儲存方式']) assert.ok(labels.includes(label), label + ' is the exact label');
  assert.deepEqual(labels.filter(label => /選填|（|\(|建立後/.test(label)), [], 'no label carries a bracketed note');

  // 必填欄用星號標示，選填欄沒有星號，也不寫「選填」。
  const starred = rows.filter(row => row.querySelector('.form__req') !== null)
    .map(row => row.querySelector('input,select').getAttribute('name')).sort();
  assert.deepEqual(starred, ['caseName', 'periodEnd', 'periodStart']);
  for (const name of ['caseName', 'periodStart', 'periodEnd']) assert.equal(form[name].hasAttribute('required'), true, name + ' required');
  for (const name of ['projectCode', 'entityName', 'lastPeriodStart']) assert.equal(form[name].hasAttribute('required'), false, name + ' optional');

  // 資料儲存方式的限制寫在欄位下方（只比對意思：建立以後不能改）。
  const providerRow = form.databaseProvider.closest('.form__row');
  const siblings = form.children, after = siblings[siblings.indexOf(providerRow) + 1];
  assert.ok(after !== undefined && after.tagName === 'P', 'a sentence follows the storage field');
  assert.match(after.textContent, /建立.*不(能|可)變更/);
});

test('journey 2: creating a case sends exactly the entered details without an operator, then opens it and shows its name and client', async () => {
  const f = remapAppFixture(); f.store.resetWorkflow(); f.store.setView('workflow');
  f.store.setCurrentUser({ shortName: '合成審計員', userNumber: 7 });
  // 頁首的案件摘要用正式 index.html 的標記。
  f.dom.content.insertAdjacentHTML('beforeend', readWwwroot('index.html').match(/<section class="case-summary"[\s\S]*?<\/section>/)[0]);
  const form = namedForm(f.draw('create').querySelector('[data-bind="create-form"]'));
  fill(form, { ...VALID_CREATE, caseName: '  合成案件甲  ', projectCode: ' SYN-01 ', entityName: ' 合成客戶股份有限公司 ' });
  submit(form);

  const create = f.request('projectCreate');
  assert.deepEqual(plain(create.payload), { caseName: '合成案件甲', projectCode: 'SYN-01', entityName: '合成客戶股份有限公司',
    periodStart: '2025-01-01', periodEnd: '2025-12-31', lastPeriodStart: '2025-12-20', databaseProvider: 'duckdb' });
  assert.equal(Object.hasOwn(create.payload, 'operatorId'), false, 'the operator is taken by the backend, not sent');
  assert.ok(noDunderKeys(create.payload));

  create.resolve({ projectId: '合成案件甲', warnings: [] }); await settle();
  const load = f.request('projectLoad');
  assert.deepEqual(plain(load.payload), { projectId: '合成案件甲' });
  load.resolve({ project: { projectId: '合成案件甲', entityName: '合成客戶股份有限公司', projectCode: 'SYN-01', operatorId: '合成審計員',
    periodStart: '2025-01-01', periodEnd: '2025-12-31', lastPeriodStart: '2025-12-20', databaseProvider: 'duckdb', currentStep: 0 } });
  await settle();

  // 案件以案件名稱識別，客戶名稱另外顯示。
  assert.equal(f.state.caseId, '合成案件甲');
  assert.equal(f.state.caseClient, '合成客戶股份有限公司');
  f.app.render();
  assert.equal(f.$('[data-bind="case-id"]').textContent, '合成案件甲');
  assert.equal(f.$('[data-bind="case-client"]').textContent, '合成客戶股份有限公司');

  // 建好以後第一步顯示建案者是後端記下的帳號。
  const summary = f.draw('create').querySelector('.kv-list').textContent;
  assert.match(summary, /合成案件甲/);
  assert.match(summary, /合成審計員/);
  assert.ok(f.draw('create').querySelector('[data-bind="create-form"]') === null, 'the form is replaced by the case summary');
});

test('journey 2: the case list names each case by its case name and lists the client and case number separately', () => {
  const f = remapAppFixture(); f.store.resetWorkflow(); f.store.setView('picker');
  f.store.setProjects([
    { projectId: '合成案件甲', entityName: '合成客戶股份有限公司', projectCode: 'SYN-01', databaseProvider: 'sqlite' },
    { projectId: '合成案件乙', entityName: '', databaseProvider: 'duckdb' }
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

// 2026-10-04 第 4 批 C6（原註解在 ApprovedUxFrontendTests）：同一錯誤呈現也支援日期與資料修改欄位，依 field 白名單定位，不解析訊息。
// 後端訊息原樣顯示；這裡的訊息都是合成字串。
test('journey 2: a create failure marks only the field the backend names and never guesses a field from the code or the message', async () => {
  const cases = [
    { name: 'named case name', error: { code: 'invalid_payload', field: 'caseName', message: '合成原因：名稱不可用。' }, marked: 'caseName' },
    { name: 'named period start', error: { code: 'invalid_payload', field: 'periodStart', message: '合成原因：日期。' }, marked: 'periodStart' },
    { name: 'field outside the allow-list', error: { code: 'invalid_payload', field: 'databaseProvider', message: '合成原因：資料儲存方式。' }, marked: null },
    { name: 'prototype key', error: { code: 'invalid_payload', field: '__proto__', message: '合成原因：案件名稱。' }, marked: null },
    // 訊息與代碼都提到案件名稱，但沒有 field：不從文字猜。
    { name: 'no field', error: { code: 'invalid_payload', message: '案件名稱與客戶名稱的合成原因（caseName）。' }, marked: null }
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
    form.fire('input', { target: form.caseName });
    assert.equal(summary.hidden, true, name + ': typing hides the reason');
    assert.deepEqual(form.querySelectorAll('[aria-invalid="true"]').map(node => node.getAttribute('name')), [], name);
    assert.equal(form.entityName.value, '合成客戶股份有限公司', name + ': the entered values stay');
  }
});

test('journey 2: a failed save of the case details marks the preparation date only when the backend names it', async () => {
  for (const { field, marked } of [{ field: 'lastPeriodStart', marked: 'lastPeriodStart' }, { field: 'databaseProvider', marked: null }, { field: undefined, marked: null }]) {
    const f = usageJourneyFixture('create', { redraw: true });
    const focus = recordFocus(f);
    f.store.setProject({ projectId: '合成案件甲', entityName: '合成客戶', projectCode: 'SYN-01', operatorId: '合成審計員',
      periodStart: '2025-01-01', periodEnd: '2025-12-31', lastPeriodStart: null, databaseProvider: 'sqlite' });
    f.click('[data-action="edit-project-metadata"]');
    let form = namedForm(f.$('[data-bind="project-update-form"]'));
    // 修改表單的標籤一樣不寫「（選填）」（同上，2026-10-13 裁定）。客戶名稱與案件編號是裁定過的名稱，逐字比對；
    // 「期末財報準備日」不在裁定裡，照計畫第 1 節只比對意思。列數與順序照樣鎖住。
    const updateLabels = form.querySelectorAll('.form__row').map(labelText);
    assert.equal(updateLabels.length, 3);
    assert.deepEqual(updateLabels.slice(0, 2), ['客戶名稱', '案件編號']);
    assert.match(updateLabels[2], /準備日/);
    assert.deepEqual(updateLabels.filter(label => /選填|（|\(/.test(label)), [], 'no label carries a bracketed note');
    fill(form, { entityName: ' 新合成客戶 ', projectCode: 'SYN-02', lastPeriodStart: '2025-12-20' });
    submit(form);
    const update = f.request('projectUpdate');
    assert.deepEqual(plain(update.payload), { entityName: '新合成客戶', projectCode: 'SYN-02', lastPeriodStart: '2025-12-20' });
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
    assert.equal(f.state.project.entityName, '合成客戶', label + ': the saved case details are unchanged');
  }
});

// C9 U48/L87（原註解在 UserFeedbackStepsOneToFourFrontendTests）：舊的身分重繪鍵會清掉未儲存的表單，改為只更新操作人員那一行。
// 原方法的第一次失敗收據：20261004-084402361-d3857beabfbf4b5689007c0ac3130c98。
test('journey 2: a late Windows identity updates only the account line and keeps the typed case name, its selection and focus', () => {
  const f = remapAppFixture(); f.store.resetWorkflow(); f.store.setView('workflow');
  const step = f.store.STEPS[0];
  f.app.renderContent(f.state, step);
  const notice = () => f.$('[data-bind="create-operator"]').textContent;
  // 還沒有身分時說明是目前的 Windows 帳號（只比對意思）。
  assert.match(notice(), /Windows 帳號/);

  const input = f.$('[name="caseName"]');
  assert.ok(input !== null, 'case name input rendered');
  input.value = '未儲存的合成案件'; input.selectionStart = 2; input.selectionEnd = 5; input.focus();

  f.store.setCurrentUser({ shortName: '合成審計員', userNumber: 7, principal: 'SYNTHETIC\\合成審計員' });
  f.app.renderContent(f.state, step);
  assert.ok(f.$('[name="caseName"]') === input, 'the same input stays on screen');
  assert.equal(input.isConnected, true);
  assert.equal(input.value, '未儲存的合成案件');
  assert.equal(input.selectionStart, 2); assert.equal(input.selectionEnd, 5);
  assert.ok(f.dom.document.activeElement === input, 'focus stays in the case name');
  assert.match(notice(), /合成審計員/);

  // 身分沒有簡稱時仍說明是目前的 Windows 帳號，不顯示空白或 undefined。
  f.store.setCurrentUser({ userNumber: 8, principal: 'SYNTHETIC\\no-short-name' });
  f.app.renderContent(f.state, step);
  assert.ok(f.$('[name="caseName"]') === input, 'the same input stays on screen');
  assert.match(notice(), /Windows 帳號/);
  assert.doesNotMatch(notice(), /undefined|null|合成審計員/);
});

// =====================================================================================================
// 情境 4　第二步匯入與行事曆
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

test('journey 4: choosing GL and TB source files offers exactly the seven supported file types and the guidance names them', async () => {
  const f = importFixture();
  // 只比對意思（計畫第 2 節情境 4）：說明列出 Excel、CSV、文字檔與 Access，試算表要同一查核期間。
  const hint = f.$('.panel__hint').textContent;
  for (const word of [/Excel/, /CSV/, /文字檔/, /Access/, /同一查核期間/]) assert.match(hint, word);
  // 已撤下的舊說明不再出現在第二步。
  const panel = f.$('[data-motion-key="import-panel"]').textContent;
  assert.doesNotMatch(panel, /完整資料直接匯入案件資料庫|解鎖「非授權編製人員」/);

  for (const [kind, name] of [['gl', /PBC-GL/], ['tb', /TB/]]) {
    f.click('[data-task-toggle="' + kind + '"]');
    f.click('[data-action="wizard-replace-' + kind + '"]');
    const pick = f.request('hostSelectFiles');
    assert.deepEqual(Object.keys(pick.payload).sort(), ['extensions', 'title']);
    // 選檔對話框的副檔名是前後端契約，精確比對。
    assert.deepEqual(plain(pick.payload.extensions), ['.xlsx', '.xlsm', '.xls', '.csv', '.txt', '.mdb', '.accdb']);
    assert.match(pick.payload.title, name);
    pick.resolve({ files: [] }); await settle();
    f.click('[data-action="wizard-cancel"]');
  }
  assert.equal(f.requests.filter(r => r.action === 'hostSelectFiles').length, 2);
});

// 使用者 2026-10-16 指出第二步只有「授權編製人員清單」的「操作」有外框，展開後內容下緣還多一條淡線。
// 四列的「操作」是同一種每列重複的動作，要長得一樣；展開中的那一列標出 aria-expanded，樣式由它保留淡藍底。
// 嵌進列裡的匯入卡不再帶獨立卡片的陰影與圓角。
test('journey 4: every import task uses the same action button and an opened task shows no card edge', () => {
  const f = importFixture();
  const actions = ['gl', 'tb', 'authorizedPreparer', 'calendar'].map(kind => f.$('[data-task-toggle="' + kind + '"]'));
  assert.ok(actions.every(action => action !== null), 'all four tasks are listed');
  // 2026-10-19 使用者要求同功能的元件只留一套，展開用的按鈕都接共用的展開箭頭 jet-disclosure；四列仍是同一種按鈕
  // （第一次失敗：FrontendMapping 收據 20261019-095317346-4d75b1d78d9a4296a887c2e7cfb720df）。
  assert.deepEqual(actions.map(action => action.getAttribute('class')), Array(4).fill('import-task__action jet-disclosure'));
  f.click('[data-task-toggle="tb"]');
  assert.equal(f.$('[data-task-toggle="tb"]').getAttribute('aria-expanded'), 'true');
  assert.equal(f.$('[data-task-toggle="gl"]').getAttribute('aria-expanded'), 'false');
  const css = require('node:fs').readFileSync(require('node:path').join(__dirname, '../../src/JET/JET/wwwroot/css/app.css'), 'utf8');
  assert.doesNotMatch(css, /import-task__action--primary/, 'no row action is styled apart from the others');
  const panelCard = css.match(/\.import-task__panel \.import-card \{([^}]*)\}/);
  assert.ok(panelCard !== null, 'the opened card has its own rule');
  assert.match(panelCard[1], /box-shadow: none;/);
  assert.match(panelCard[1], /border-radius: 0;/);
  assert.match(css, /\.import-task__action\[aria-expanded="true"\],/);
});

test('journey 4: a holiday file and a make-up day file with zero rows still complete the calendar task', async () => {
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
  assert.match(calendarMeta(f), /假日 0/);
  assert.match(calendarMeta(f), /補班 0/);
  assert.match(f.$('[data-bind="import-card-calendar"] .import-card__status').textContent, /假日 0.*補班 0/);
});

test('journey 4: making every weekday a working day is saved and the summary says there are no non-working days', async () => {
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
  // 只比對意思（計畫第 4 節 B9）：整週都是工作日時，摘要寫非工作日為無。
  assert.match(calendarMeta(f), /非工作日\s*無/);
  assert.deepEqual(boxes().filter(box => box.checked).map(box => box.getAttribute('data-weekday')), [], 'no weekday stays ticked');
});

test('journey 4: saving the same non-working days in another order or with repeats keeps results, and a different set refreshes them', async () => {
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
  assert.match(calendarMeta(f), /假日 3.*補班 1.*非工作日 日/);
});

// =====================================================================================================
// 第 10 批補：訊息面板的句子（收尾四項第一項第 10 批，2026-10-14）
// 第 6 批刻意不斷言訊息面板，留給第 10 批在文字確認並改進產品後補上。逐字比對的句子都寫出 final-wording.json 的編號；
// 後端提醒與原因一律用合成字串。耗時的寫法是使用者 2026-10-13「保留，改成秒並合併」的裁定與 2026-10-14 第三輪 R3-26。
// =====================================================================================================

const ELAPSED = /耗時(?: \d+\.\d 秒|不到 0\.1 秒|\d+ 分 \d+ 秒)/.source;
const messageList = f => plain(f.store.getState().messages.map(m => ({ text: m.text, level: m.level })));

test('journey 2 messages: creating a case logs that it was created with each backend warning, and a failed create is an error line while the reason stays on the form', async () => {
  let f = createFixture(); f.store.setView('workflow');
  let form = f.page();
  fill(form, VALID_CREATE);
  submit(form);
  f.request('projectCreate').resolve({ projectId: '合成案件甲', warnings: ['合成：建案提醒。'] }); await settle();
  f.request('projectLoad').resolve({ project: { projectId: '合成案件甲', periodStart: '2025-01-01', periodEnd: '2025-12-31', databaseProvider: 'duckdb', currentStep: 0 } });
  await settle(); await settle();
  // 使用者 2026-10-13 勾選頁確認 64（final-wording.json，維持原句）；後端提醒原樣、屬需要注意。
  assert.deepEqual(messageList(f).slice(0, 2), [{ text: '合成：建案提醒。', level: 'warn' }, { text: '案件「合成案件甲」已建立。', level: 'info' }]);
  assert.equal(messageList(f).length, 2, 'no other completion line');

  f = createFixture(); f.store.setView('workflow');
  form = f.page();
  fill(form, VALID_CREATE);
  submit(form);
  f.request('projectCreate').reject({ code: 'invalid_payload', field: 'caseName', message: '合成原因：名稱不可用。' }); await settle();
  // 使用者 2026-10-13 勾選頁確認 19（final-wording.json），2026-10-14 第三輪 R3-27：原因的句號拿掉，括號後加句號。
  // 作業名稱沒有使用者裁定，只確認提到建立案件的意思。
  assert.match(messageList(f)[0].text, new RegExp('^[^：]*建立[^：]*案件[^：]*失敗：合成原因：名稱不可用（' + ELAPSED + '）。$'));
  assert.equal(messageList(f)[0].level, 'error');
  // 原因已寫在表單上方的錯誤框，第一步不再加提示框（使用者 2026-10-13「失敗提示」選項說明）。
  assert.equal(f.store.getState().operationFailure, null);
});

test('journey 2 messages: saving the case details logs that they were updated with each backend warning, and a failed save is an error line', async () => {
  const setup = () => {
    const f = usageJourneyFixture('create', { redraw: true });
    recordFocus(f);
    f.store.setView('workflow');
    f.store.setProject({ projectId: '合成案件甲', entityName: '合成客戶', projectCode: 'SYN-01', operatorId: '合成審計員',
      periodStart: '2025-01-01', periodEnd: '2025-12-31', lastPeriodStart: null, databaseProvider: 'sqlite' });
    f.click('[data-action="edit-project-metadata"]');
    const form = namedForm(f.$('[data-bind="project-update-form"]'));
    fill(form, { entityName: '新合成客戶', projectCode: 'SYN-02', lastPeriodStart: '2025-12-20' });
    submit(form);
    return f;
  };
  let f = setup();
  f.request('projectUpdate').resolve({ project: { projectId: '合成案件甲', entityName: '新合成客戶', projectCode: 'SYN-02', operatorId: '合成審計員',
    periodStart: '2025-01-01', periodEnd: '2025-12-31', lastPeriodStart: '2025-12-20', databaseProvider: 'sqlite' },
  warnings: ['合成：準備日提醒。'], reportArtifacts: [] });
  await settle(); await settle();
  // 使用者 2026-10-13 勾選頁確認 62（final-wording.json）。
  assert.deepEqual(messageList(f).slice(0, 2), [{ text: '合成：準備日提醒。', level: 'warn' }, { text: '案件資料已更新。', level: 'info' }]);
  assert.equal(messageList(f).length, 2, 'no other completion line');

  f = setup();
  f.request('projectUpdate').reject({ code: 'invalid_payload', message: '合成原因：期末財報準備日。' }); await settle();
  // 作業名稱沒有使用者裁定，只確認提到儲存案件資料的意思；句型是勾選頁確認 19 與第三輪 R3-27。
  assert.match(messageList(f)[0].text, new RegExp('^[^：]*儲存[^：]*案件[^：]*失敗：合成原因：期末財報準備日（' + ELAPSED + '）。$'));
  assert.equal(messageList(f)[0].level, 'error');
  assert.equal(f.store.getState().operationFailure, null, 'the reason is on the form, not in a step notice');
});

test('journey 4 messages: holiday and make-up day imports log their day counts with the elapsed time, choosing no file logs nothing, and only a real change of non-working days is logged', async () => {
  const f = importFixture();
  f.click('[data-task-toggle="calendar"]');
  for (const [action, importAction, count, expected] of [
    ['import-holiday', 'importHolidayFromFile', 3, '假日匯入完成：3 天'],
    ['import-makeup', 'importMakeupDayFromFile', 1, '補班匯入完成：1 天']]) {
    f.click('[data-action="' + action + '"]');
    f.requests.filter(r => r.action === 'hostSelectFile').at(-1).resolve({ filePath: 'synthetic\\calendar.xlsx', fileName: 'calendar.xlsx' }); await settle();
    f.request(importAction).resolve({ count }); await settle(); await settle();
    // 使用者 2026-10-13 勾選頁確認 42、43（final-wording.json，維持原句），句尾接上耗時（同日確認 15）。
    assert.match(messageList(f)[0].text, new RegExp('^' + expected + '，' + ELAPSED + '。$'));
    assert.equal(messageList(f)[0].level, 'info');
  }
  // 沒有選檔：這次其實沒有做事，不記。
  const before = messageList(f).length;
  f.click('[data-action="import-holiday"]');
  f.requests.filter(r => r.action === 'hostSelectFile').at(-1).resolve({ filePath: '', fileName: '' }); await settle(); await settle();
  assert.equal(messageList(f).length, before, 'choosing no file logs nothing');

  // 非工作日真的改了才記；送出同一組（只是順序不同）不記（使用者 2026-10-13 勾選頁確認 44a 保留、44b 刪除）。
  f.store.setCalendarState({ holidayCount: 3, makeupDayCount: 1, calendarImported: true, nonWorkingDays: [6, 0], nonWorkingDaysConfigured: true });
  const box = day => f.$('[data-bind="nonworking-days"] input[data-weekday="' + day + '"]');
  box(6).fire('change');
  f.requests.filter(r => r.action === 'calendarSetNonWorkingDays').at(-1).resolve({ nonWorkingDays: [0, 6] }); await settle(); await settle();
  assert.equal(messageList(f).length, before, 'the same days in another order log nothing');
  box(6).checked = false; box(6).fire('change');
  f.requests.filter(r => r.action === 'calendarSetNonWorkingDays').at(-1).resolve({ nonWorkingDays: [0] }); await settle(); await settle();
  assert.deepEqual(messageList(f)[0], { text: '已更新非工作日設定。', level: 'info' });
  assert.equal(messageList(f).length, before + 1);
});
