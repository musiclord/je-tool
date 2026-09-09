(function () {
  'use strict';
  document.addEventListener('DOMContentLoaded', async function () {
    const notice = document.getElementById('preview-status');
    try {
      const bundle = await window.JetPreview.ready;
      const id = new URLSearchParams(location.search).get('scene') || 'matches';
      const fixture = bundle.fixtures.find(item => item.id === id) || bundle.fixtures[0];
      window.JetUi.applyLoadedProject(structuredClone(bundle.loaded), { stayOnCurrentStep: true });
      window.JetStore.setStepIndex(4);
      window.JetStore.setDataPreviewCollapsed(true);
      window.JetStore.setFilterDraft(structuredClone(fixture.scenario));
      const request = { populationScope: 'auditPeriod', scenario: structuredClone(fixture.scenario) };
      const result = await window.JetApi.filterPreview(request);
      result.scenario.voucherPage = await window.JetApi.queryFilterVoucherPage({ ...request, pageSize: 50 });
      result.scenario.voucherRequest = request;
      window.JetStore.setFilterPreview(result.scenario);
      notice.textContent = '合成資料・設計預覽｜修改後刷新即載入正式前端；未知條件不計算。';
      document.getElementById('preview-scene').value = fixture.id;
      document.getElementById('preview-scene').onchange = event => { location.search = '?scene=' + event.target.value; };
      document.getElementById('preview-reload').onclick = () => location.reload();
      document.getElementById('preview-requests').onclick = () => {
        const output = document.getElementById('preview-request-output');
        output.hidden = !output.hidden;
        output.textContent = JSON.stringify(window.JetPreview.requests, null, 2);
      };
    } catch (error) { notice.textContent = error.message; notice.setAttribute('role', 'alert'); }
  });
})();
