'use strict';
require('./support/resource-guard.cjs');
// 第五步留白的情境名稱與動機（裁定 2026-10-29-16 與 2026-10-30-14）。
//
// 審計員在第五步編輯一個已儲存的情境，名稱與動機仍是依條件自動帶入的文字，按「儲存情境」。畫面送出的名稱與動機，
// 要和後端在名稱或動機留白時補上的文字相同，前後端不能各自產生不同的文字。
// 預期文字寫死在 tools/tests/fixtures/filter-scenario-automatic-metadata.json，前後端共用同一份：
//   - 後端：src/JET/tests/JET.Tests/Application/FilterScenarioAutomaticMetadataTests.cs 留白儲存後讀回名稱與動機；
//   - 前端：這個檔用正式第五步畫面編輯已存情境，按儲存後讀送出的名稱與動機。
// 預期文字由 Claude 照第五步現在的自動帶入規則逐字寫下，不由程式算出。全部資料都是程式生成的合成資料。
// 這個檔在改動前的前端就通過（FrontendMapping 收據 20261030-232349902-dbb29d8564bb41c586f73ee8fa863f3b），證明預期文字照現在的自動帶入規則寫；
// 後端那一半的第一次失敗是 Focused 收據 20261030-232334254-f59f8cb133ac46619d8de8eb3e67adff。
// 2026-11-03 補三則：舊式文字條件的關鍵字，以及同一組裡混用且與或（只有這一組、旁邊另有一組）。文字條件那則加上重複的關鍵字後，
// 前端讀回沒有去掉重複，第一次失敗：FrontendMapping 收據 20261102-235132195-ec74fbe882354b7f8fb443c6deec3db4；
// 依裁定 2026-11-03-01 改成和送出時同一寫法。
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const F = require('./support/frontend-fixtures.cjs');
const { plain, settle } = F;

const entries = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures/filter-scenario-automatic-metadata.json'), 'utf8'));

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
