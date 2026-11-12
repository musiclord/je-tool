'use strict';
require('./support/resource-guard.cjs');
// 第五步留白的情境名稱與動機。
//
// 審計員在第五步編輯一個已儲存的情境，名稱與動機仍是依條件自動帶入的文字，按「儲存情境」。畫面送出的名稱與動機，
// 要和後端在名稱或動機留白時補上的文字相同，前後端不能各自產生不同的文字。
// 預期文字寫死在 tests/JET.Tests/Fixtures/filter-scenario-automatic-metadata.json，前後端共用同一份：
//   - 後端：tests/JET.Tests/Application/FilterScenarioAutomaticMetadataTests.cs 留白儲存後讀回名稱與動機；
//   - 前端：這個檔用正式第五步畫面編輯已存情境，按儲存後讀送出的名稱與動機。
// 預期文字照第五步現在的自動帶入規則逐字寫下，不由程式算出。全部資料都是程式生成的合成資料。
// 夾具也涵蓋舊式文字條件的關鍵字，以及同一組裡混用且與或（只有這一組、旁邊另有一組）。
// 文字條件那一則帶重複的關鍵字，前端讀回和送出時用同一寫法。
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const F = require('./support/frontend-fixtures.cjs');
const { plain, settle } = F;

const entries = JSON.parse(fs.readFileSync(path.join(__dirname, '../JET.Tests/Fixtures/filter-scenario-automatic-metadata.json'), 'utf8'));

function existingScenario(name) {
  return { name, rationale: '合成自擬動機', groups: [{ join: 'AND', matchScope: 'row',
    conditions: [{ type: 'drCrOnly', join: 'AND', drCr: 'debit' }] }],
    editorOrigins: { version: 2, nameIsAutomatic: false, rationaleIsAutomatic: false, groups: [{ cards: [null] }] } };
}

for (const entry of entries) {
  test('automatic metadata: saving keeps the shared automatic name and rationale: ' + entry.id, async () => {
    const f = F.usageJourneyFixture('filter', { redraw: true });
    f.store.setResult('validate', { resultRef: { resultId: 'v1' }, completenessTest: { eligibility: { isEligible: true } } });
    f.store.setAccountMappingState({ hasAnyCategory: true, hasRevenue: true, hasCounterpart: true });
    const target = { name: '合成舊名稱', rationale: '合成舊動機', ...plain(entry.scenario) };
    const saved = entry.existingNames.map(existingScenario).concat([target]);
    f.store.setSavedScenarios(saved);
    f.store.setFilterResultRef({ revision: 'r1', populationScope: 'auditPeriod' });
    f.store.setFilterDraft(null);
    f.click('[data-action="edit-scenario"][data-index="' + (saved.length - 1) + '"]');
    f.click('[data-action="save-scenario"]'); await settle();
    const sent = f.request('filterScenarioSave').payload.scenario;
    assert.equal(sent.name, entry.name);
    assert.equal(sent.rationale, entry.rationale);
  });
}
