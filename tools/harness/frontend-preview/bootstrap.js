(function () {
  'use strict';
  document.addEventListener('DOMContentLoaded', async function () {
    const notice = document.getElementById('preview-status');
    try {
      const bundle = await window.JetPreview.ready;
      const id = new URLSearchParams(location.search).get('scene') || 'matches';
      const stale = id === 'stale';
      const fixture = bundle.fixtures.find(item => item.id === id) || bundle.fixtures[0];
      const loaded = structuredClone(bundle.loaded);
      // Preview-only project.load mirror: keep validated data, but mark the old
      // prescreen and filter results stale. Never fabricate a newly run filter.
      if (stale) {
        loaded.staleState = { validation: false, prescreen: true, filter: true };
        if (loaded.latestRuns) loaded.latestRuns.prescreen = null;
        loaded.reportArtifacts = (loaded.reportArtifacts || []).map(artifact =>
          ['prescreenReport', 'criteriaSelectionReport', 'workingPaper'].includes(artifact.kind)
            ? { ...artifact, stale: true } : artifact);
      }
      window.JetUi.applyLoadedProject(loaded, { stayOnCurrentStep: true });
      window.JetStore.setStepIndex(4);
      window.JetStore.setDataPreviewCollapsed(true);
      window.JetStore.setFilterDraft(structuredClone(fixture.scenario));
      if (!stale) {
        const request = { populationScope: 'auditPeriod', scenario: structuredClone(fixture.scenario) };
        const result = await window.JetApi.filterPreview(request);
        result.scenario.voucherPage = await window.JetApi.queryFilterVoucherPage({ ...request, pageSize: 50 });
        result.scenario.voucherRequest = request;
        window.JetStore.setFilterPreview(result.scenario);
      }
      notice.textContent = '合成資料預覽（僅供畫面調整）';
      document.getElementById('preview-scene').value = stale ? 'stale' : fixture.id;
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
