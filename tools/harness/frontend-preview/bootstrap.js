(function () {
  'use strict';
  document.addEventListener('DOMContentLoaded', async function () {
    const notice = document.getElementById('preview-status');
    try {
      const bundle = await window.JetPreview.ready;
      const params = new URLSearchParams(location.search);
      const id = params.get('scene') || 'matches';
      const mode = window.JetPreviewData ? window.JetPreviewData.modeFrom(location.search) : 'demo';
      const stale = id === 'stale';
      const fixture = bundle.fixtures.find(item => item.id === id) || bundle.fixtures[0];
      const loaded = structuredClone(bundle.loaded);
      // Preview-only project.load mirror: keep validated data, but mark the old
      // prescreen and filter results stale. Never fabricate a newly run filter.
      if (stale) {
        loaded.staleState = { validation: false, prescreen: true, filter: true };
        loaded.prescreenResult = null;
        loaded.reports = (loaded.reports || []).map(report =>
          ['prescreenReport', 'criteriaSelectionReport', 'workingPaper'].includes(report.kind)
            ? { ...report, stale: true } : report);
      }
      window.JetUi.applyLoadedProject(loaded, { stayOnCurrentStep: true });
      window.JetStore.setStepIndex(4);
      window.JetStore.setDataPreviewCollapsed(true);
      window.JetStore.setFilterDraft(window.JetPreviewData
        ? window.JetPreviewData.draftFor(fixture.scenario, mode) : structuredClone(fixture.scenario));
      if (!stale && mode !== 'worst-input') {
        const request = { populationScope: 'auditPeriod', scenario: structuredClone(fixture.scenario) };
        const result = await window.JetApi.filterPreview(request);
        result.scenario.voucherPage = await window.JetApi.queryFilterVoucherPage({ ...request, pageSize: 50 });
        result.scenario.voucherRequest = request;
        window.JetStore.setFilterPreview(result.scenario);
      }
      notice.textContent = mode === 'worst-input' ? '長條件值預覽（未執行查詢，僅供畫面調整）' : '合成資料預覽（僅供畫面調整）';
      document.getElementById('preview-scene').value = stale ? 'stale' : fixture.id;
      document.getElementById('preview-scene').onchange = event => {
        params.set('scene', event.target.value); location.search = '?' + params.toString();
      };
      const dataSwitch = document.getElementById('preview-data');
      if (dataSwitch) {
        dataSwitch.querySelectorAll('[data-preview-mode]').forEach(button => {
          button.setAttribute('aria-pressed', String(button.getAttribute('data-preview-mode') === mode));
          button.onclick = () => {
            params.set('data', button.getAttribute('data-preview-mode'));
            location.search = '?' + params.toString();
          };
        });
      }
      document.getElementById('preview-reload').onclick = () => location.reload();
      document.getElementById('preview-requests').onclick = () => {
        const output = document.getElementById('preview-request-output');
        output.hidden = !output.hidden;
        output.textContent = JSON.stringify(window.JetPreview.requests, null, 2);
      };
    } catch (error) { notice.textContent = error.message; notice.setAttribute('role', 'alert'); }
  });
})();
