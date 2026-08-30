/*
  Step 5：匯出底稿。
  使用者只選擇要納入的已存篩選情境；工作表組成、資料內容與專案內落點均由後端決定。
  wire 不傳工作表名稱、存檔路徑或實體檔案位置。
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;
  var Ui = global.JetUi;

  var selectedScenarioPositions = null;
  var selectionRevision = null;
  var lastSheetStats = null;
  var cleanupPreview = null;
  var cleanupFeedback = null;
  // Pre-screening Report 預設納入輸出家族；使用者可取消，取消後只影響這一份。
  var includePrescreenReport = true;

  Ui.registerWorkflowReset(function () {
    selectedScenarioPositions = null;
    selectionRevision = null;
    lastSheetStats = null;
    cleanupPreview = null;
    cleanupFeedback = null;
    includePrescreenReport = true;
  });

  function runId(run) {
    return run && run.resultRef ? run.resultRef.runId : null;
  }

  function populationScopeLabel() {
    return '查核期間';
  }

  /* Pre-screening Report 的匯出面決策。文案一律取自後端 renderer（Ui.prescreenPositioningCopy）；
     這裡只決定「勾了沒」「有沒有現行 prescreen run」「完整性適不適格」三個既有狀態的組合。 */
  function prescreenExportPlan(state) {
    var eligibility = Ui.completenessEligibility(state.lastRuns.validate);
    var currentRunId = runId(state.lastRuns.prescreen);
    return {
      positioning: Ui.prescreenPositioningCopy(state.lastRuns.prescreen),
      isEligible: eligibility.isEligible,
      gateReason: eligibility.reason,
      currentRunId: currentRunId,
      // 不適格時無法補跑，勾選也不生效：只產出 Working Paper。
      included: includePrescreenReport && (!!currentRunId || eligibility.isEligible),
      needsRun: includePrescreenReport && !currentRunId && eligibility.isEligible
    };
  }

  function prescreenExportOptionHtml(plan) {
    var blocked = includePrescreenReport && !plan.currentRunId && !plan.isEligible;
    var notice = plan.needsRun
      ? plan.positioning.exportPendingRunGuidance
      : (blocked
        ? plan.gateReason + ' 本次只會產生 WorkingPaper。'
        : '');
    return (
      '<section class="prescreen-export" aria-labelledby="prescreen-export-heading">' +
        '<h3 class="scenario-export-selection__title" id="prescreen-export-heading">一併產出的報告</h3>' +
        '<label class="scenario-export-option">' +
          '<input type="checkbox" data-bind="include-prescreen-report"' +
            (includePrescreenReport ? ' checked' : '') + '>' +
          '<span class="scenario-export-option__copy">' +
            '<span class="scenario-export-option__name">Pre-screening Report</span>' +
            '<span class="scenario-export-option__meta">' +
              Ui.esc(plan.positioning.exportDefaultGuidance) + '</span>' +
          '</span>' +
        '</label>' +
        (notice
          ? '<p class="prescreen-export__notice" data-bind="prescreen-export-notice">' +
            Ui.esc(notice) + '</p>'
          : '') +
      '</section>'
    );
  }

  function exportButtonLabel(plan) {
    if (plan.needsRun) { return '先執行預篩選並產生底稿'; }
    return plan.included ? '產生底稿與 Pre-screening Report' : '產生 WorkingPaper';
  }

  function filterRevision(state) {
    var resultRef = state.filterResultRef;
    if (!resultRef || resultRef.populationScope !== 'auditPeriod') { return null; }
    return resultRef.revision || null;
  }

  function allScenarioPositions(state) {
    return (state.filter.savedScenarios || []).map(function (_, index) { return index + 1; });
  }

  function ensureScenarioSelection(state) {
    var revision = filterRevision(state);
    if (selectionRevision !== revision || selectedScenarioPositions === null) {
      selectionRevision = revision;
      selectedScenarioPositions = (state.filter.savedScenarios || []).map(function (_, index) {
        return index + 1;
      });
      return;
    }

    var count = (state.filter.savedScenarios || []).length;
    selectedScenarioPositions = selectedScenarioPositions.filter(function (position) {
      return position >= 1 && position <= count;
    });
  }

  function scenarioSelectionHtml(state) {
    var scenarios = state.filter.savedScenarios || [];
    var options = scenarios.map(function (scenario, index) {
      var position = index + 1;
      var checked = selectedScenarioPositions.indexOf(position) >= 0 ? ' checked' : '';
      return (
        '<label class="scenario-export-option">' +
          '<input type="checkbox" data-scenario-position="' + position + '"' + checked + '>' +
          '<span class="scenario-export-option__copy">' +
            '<span class="scenario-export-option__name">C' + position + ' · ' + Ui.esc(scenario.name) + '</span>' +
            '<span class="scenario-export-option__meta">' + Ui.esc(scenario.rationale || '已存篩選情境') + '</span>' +
          '</span>' +
        '</label>'
      );
    }).join('');

    var empty = selectedScenarioPositions.length === 0;
    return (
      '<section class="scenario-export-selection" aria-labelledby="scenario-export-heading">' +
        '<div class="scenario-export-selection__head">' +
          '<div>' +
            '<h3 class="scenario-export-selection__title" id="scenario-export-heading">納入底稿的篩選情境</h3>' +
            '<p class="scenario-export-selection__hint">預設全選。所選情境決定 step4 的 C1–C10 情境欄；工作表固定。</p>' +
          '</div>' +
          '<div class="panel__actions">' +
            '<button type="button" class="btn btn--ghost btn--tiny" data-action="select-all-scenarios">全選</button>' +
            '<button type="button" class="btn btn--ghost btn--tiny" data-action="clear-scenario-selection">清除</button>' +
          '</div>' +
        '</div>' +
        '<div class="scenario-export-grid">' + options + '</div>' +
        '<p class="scenario-export-selection__count" data-bind="selected-scenario-count">已選 ' +
          selectedScenarioPositions.length + ' / ' + scenarios.length + ' 個情境</p>' +
        '<p class="form-notice" data-bind="scenario-selection-error"' + (empty ? '' : ' hidden') +
          '>請至少選擇一個已存篩選情境。</p>' +
      '</section>'
    );
  }

  function sheetStatsHtml() {
    if (!lastSheetStats || lastSheetStats.length === 0) { return ''; }
    var rows = lastSheetStats.map(function (sheet) {
      return '<tr><td>' + Ui.esc(sheet.sheetName) + '</td><td class="preview-table__amount">' +
        Number(sheet.rowsWritten).toLocaleString('en-US') + '</td></tr>';
    }).join('');
    return (
      '<div class="preview-table__wrap">' +
        '<table class="preview-table">' +
          '<thead><tr><th>工作表</th><th>資料列數</th></tr></thead>' +
          '<tbody>' + rows + '</tbody>' +
        '</table>' +
      '</div>'
    );
  }

  /* 完成摘要（design 02 §I 第 5 列「完成」）。
     設計稿把「完成」畫成第六個步驟；此處刻意只轉成本步驟內的完成狀態——
     流程仍是六步（最後一步＝匯出底稿），STEPS、閘門、進度持久化與 action 語意皆不變。
     出現條件＝目前版本的 CriteriaSelectionReport 與 WorkingPaper 都已產生；
     兩份產物名稱直接鏡射既有 artifact kind，不在此另行判定審計結論。 */
  function completionSummaryHtml(criteriaArtifact, workpapers) {
    if (!criteriaArtifact || workpapers.length === 0) { return ''; }
    var tags = ['CriteriaSelectionReport', 'WorkingPaper'].map(function (kind) {
      return '<span class="completion__tag">' + Ui.esc(kind) + '</span>';
    }).join('');
    return (
      '<section class="completion" role="status" aria-labelledby="completion-heading">' +
        '<span class="completion__mark" aria-hidden="true">✓</span>' +
        '<div class="completion__copy">' +
          '<h3 class="completion__title" id="completion-heading">案件流程已完成</h3>' +
          '<p class="completion__body">目前版本的條件篩選報告與 WorkingPaper 都已產生於專案目錄。' +
            '可按右上角「儲存並結束」關閉並保留進度；若要更換納入的情境或重新產生，' +
            '可直接在本步驟重新執行。</p>' +
          '<div class="completion__tags">' + tags + '</div>' +
        '</div>' +
      '</section>'
    );
  }

  function cleanupReasonLabel(reason) {
    if (reason === 'stale') { return '上游資料版本已失效'; }
    if (reason === 'retention') { return '超出保留範圍'; }
    return reason || '原因未提供';
  }

  function cleanupFeedbackHtml() {
    if (!cleanupFeedback) { return ''; }
    var level = cleanupFeedback.level === 'success'
      ? 'success'
      : (cleanupFeedback.level === 'error' ? 'error' : 'info');
    return '<p class="report-cleanup__feedback report-cleanup__feedback--' + level + '" role="status">' +
      Ui.esc(cleanupFeedback.text) + '</p>';
  }

  function cleanupPanelHtml() {
    var feedback = cleanupFeedbackHtml();
    if (!cleanupPreview) {
      return (
        '<section class="report-cleanup" aria-labelledby="report-cleanup-heading">' +
          '<div class="report-cleanup__head">' +
            '<div>' +
              '<h3 class="report-cleanup__title" id="report-cleanup-heading">清理舊報告版本</h3>' +
              '<p class="report-cleanup__hint">先由系統確認可清理清單；此步驟不會刪除、封存或移動正式報告。</p>' +
            '</div>' +
            '<button type="button" class="btn btn--ghost" data-action="preview-report-cleanup">檢查可清理版本</button>' +
          '</div>' +
          feedback +
          '<p class="report-cleanup__empty">尚未檢查。目前不會自動清理任何版本。</p>' +
        '</section>'
      );
    }

    var hasCandidates = cleanupPreview.candidateCount > 0;
    var candidates = Array.isArray(cleanupPreview.candidates) ? cleanupPreview.candidates : [];
    var candidateRows = candidates.map(function (candidate) {
      return (
        '<li class="report-cleanup__candidate">' +
          '<span class="report-cleanup__candidate-copy">' +
            '<span class="report-cleanup__candidate-name">' + Ui.esc(candidate.fileName || '未命名報告') + '</span>' +
            '<span class="report-cleanup__candidate-meta">' +
              Ui.esc(candidate.kind || '報告類型未提供') + ' · ' +
              Ui.esc(candidate.generatedUtc || '時間未提供') + ' · ' +
              Ui.esc(candidate.bytes) + ' 位元組</span>' +
            '<span class="report-cleanup__candidate-id">報告識別碼：' + Ui.esc(candidate.artifactId) + '</span>' +
          '</span>' +
          '<span class="report-cleanup__reason">' + Ui.esc(cleanupReasonLabel(candidate.reason)) + '</span>' +
        '</li>'
      );
    }).join('');

    return (
      '<section class="report-cleanup report-cleanup--' + (hasCandidates ? 'warning' : 'clear') +
        '" aria-labelledby="report-cleanup-heading">' +
        '<div class="report-cleanup__head">' +
          '<div>' +
            '<h3 class="report-cleanup__title" id="report-cleanup-heading">清理舊報告版本</h3>' +
            '<p class="report-cleanup__hint">清單由系統依目前報告紀錄判定，畫面不自行增減。</p>' +
          '</div>' +
          '<button type="button" class="btn btn--ghost" data-action="preview-report-cleanup">重新檢查</button>' +
        '</div>' +
        feedback +
        '<p class="report-cleanup__summary">可清理 ' + Ui.esc(cleanupPreview.candidateCount) +
          ' 份，共 ' + Ui.esc(cleanupPreview.candidateBytes) + ' 位元組。</p>' +
        (hasCandidates
          ? '<ul class="report-cleanup__candidates">' + candidateRows + '</ul>' +
            '<div class="report-cleanup__confirm">' +
              '<p>這會永久刪除上列正式報告，並在目前案件留下清理稽核紀錄。</p>' +
              '<button type="button" class="btn btn--danger" data-action="confirm-report-cleanup">刪除 ' +
                Ui.esc(cleanupPreview.candidateCount) + ' 份舊報告</button>' +
            '</div>'
          : '<p class="report-cleanup__empty">目前沒有需要清理的報告版本。</p>') +
      '</section>'
    );
  }

  function requestCleanupPreview(preserveFeedback) {
    var retainedFeedback = preserveFeedback ? cleanupFeedback : null;
    if (!preserveFeedback) { cleanupFeedback = null; }
    return Ui.run('檢查可清理版本', function () {
      return global.JetApi.reportCleanupPreview({})
        .then(function (data) {
          cleanupPreview = data || null;
          Store.touch();
        })
        .catch(function (error) {
          cleanupPreview = null;
          cleanupFeedback = retainedFeedback
            ? {
                level: retainedFeedback.level,
                text: retainedFeedback.text + ' 最新候選重新檢查失敗，請稍後再按「重新檢查」。'
              }
            : {
                level: 'error',
                text: '無法取得最新清理預覽；本次只執行預覽，沒有刪除報告。'
              };
          Store.touch();
          throw error;
        });
    });
  }

  function confirmReportCleanup() {
    if (!cleanupPreview || !cleanupPreview.catalogRevision || cleanupPreview.candidateCount <= 0) {
      return Promise.resolve();
    }
    var catalogRevision = cleanupPreview.catalogRevision;

    return Ui.run('刪除舊報告', function () {
      return global.JetApi.reportCleanupConfirm({ catalogRevision: catalogRevision })
        .then(function (data) {
          Store.setReportArtifacts(data.reportArtifacts || []);
          cleanupPreview = null;
          cleanupFeedback = {
            level: 'success',
            text: '已刪除 ' + data.deletedCount + ' 份舊報告（' + data.deletedBytes +
              ' 位元組），稽核編號 ' + data.auditId + '。'
          };
          Store.touch();
        })
        .catch(function (error) {
          cleanupPreview = null;
          cleanupFeedback = error && error.code === 'operation_cancelled'
            ? { level: 'info', text: '清理已取消；將重新檢查目前候選。' }
            : (error && error.code === 'artifact_catalog_changed'
              ? { level: 'info', text: '報告清單已變更；將重新檢查後再由你確認。' }
              : { level: 'error', text: '清理未完成；將重新檢查目前候選。' });
          Store.touch();
          throw error;
        });
    }, {
      refresh: function () {
        return requestCleanupPreview(true);
      },
      refreshWhen: 'settled'
    });
  }

  function render(container, state) {
    if (!state.project) {
      container.innerHTML = Ui.noProjectPanel('匯出底稿');
      Ui.bindNoProjectPanel(container);
      return;
    }

    ensureScenarioSelection(state);

    var validationRunId = runId(state.lastRuns.validate);
    var rawScope = state.filterResultRef ? state.filterResultRef.populationScope : null;
    var populationScope = rawScope === 'auditPeriod' ? rawScope : null;
    var scenarioRevision = filterRevision(state);
    var needsScenarioResave = state.filter.savedScenarios.length > 0 && !scenarioRevision;
    var criteriaArtifact = scenarioRevision
      ? Ui.findCurrentReportArtifact(state, 'criteriaSelectionReport', {
          validationRunId: validationRunId,
          scenarioRevision: scenarioRevision,
          scenarioPositions: allScenarioPositions(state)
        })
      : null;
    var workpapers = validationRunId && scenarioRevision
      ? Ui.currentReportArtifacts(state, ['workingPaper'], {
          validationRunId: validationRunId,
          scenarioRevision: scenarioRevision
        })
      : [];
    var canExport = !!validationRunId && !!scenarioRevision &&
      !!criteriaArtifact && selectedScenarioPositions.length > 0;
    var prescreenPlan = prescreenExportPlan(state);

    container.innerHTML =
      '<div class="panel">' +
        '<h2 class="panel__title">匯出底稿</h2>' +
        '<p class="panel__hint">將目前的驗證與條件篩選結果整併為 WorkingPaper，' +
          '並預設一併產出 Pre-screening Report。</p>' +
        (populationScope
          ? '<p class="panel__hint">已保存測試母體：' + Ui.esc(populationScopeLabel(populationScope)) + '。</p>'
          : '') +
        (criteriaArtifact ? '' :
          '<p class="panel__warn">' + (needsScenarioResave
            ? '已存情境缺少可用的篩選版本。請回上一步確認測試母體並重新保存。'
            : '目前篩選版本尚無 CriteriaSelectionReport，請回上一步按「完成條件篩選並產生報告」。') +
          '</p>') +
        completionSummaryHtml(criteriaArtifact, workpapers) +
        scenarioSelectionHtml(state) +
        prescreenExportOptionHtml(prescreenPlan) +
        '<div class="panel__actions export-actions">' +
          '<button type="button" class="btn" data-action="export-workpaper"' +
            (canExport ? '' : ' disabled') + '>' + Ui.esc(exportButtonLabel(prescreenPlan)) +
            '</button>' +
        '</div>' +
        '<section class="report-output">' +
          '<div class="report-output__head">' +
            '<h3 class="report-output__title">最近底稿</h3>' +
          '</div>' +
          Ui.reportArtifactListHtml(workpapers, '尚未產生目前版本的 WorkingPaper。') +
          sheetStatsHtml() +
        '</section>' +
        cleanupPanelHtml() +
        Ui.stepFooterHtml(state) +
      '</div>';

    bind(container);
    Ui.bindStepFooter(container);
  }

  function bindScenarioSelection(container) {
    var inputs = Array.prototype.slice.call(container.querySelectorAll('[data-scenario-position]'));

    function sync() {
      selectedScenarioPositions = inputs.filter(function (input) { return input.checked; })
        .map(function (input) { return Number(input.getAttribute('data-scenario-position')); });
      var count = container.querySelector('[data-bind="selected-scenario-count"]');
      if (count) {
        count.textContent = '已選 ' + selectedScenarioPositions.length + ' / ' + inputs.length + ' 個情境';
      }
      var empty = selectedScenarioPositions.length === 0;
      var notice = container.querySelector('[data-bind="scenario-selection-error"]');
      if (notice) { notice.hidden = !empty; }
      var exportButton = container.querySelector('[data-action="export-workpaper"]');
      if (exportButton) {
        var state = Store.getState();
        var revision = filterRevision(state);
        var criteria = revision
          ? Ui.findCurrentReportArtifact(state, 'criteriaSelectionReport', {
              validationRunId: runId(state.lastRuns.validate),
              scenarioRevision: revision,
              scenarioPositions: allScenarioPositions(state)
            })
          : null;
        exportButton.disabled = empty || !runId(state.lastRuns.validate) ||
          !revision || !criteria;
      }
    }

    inputs.forEach(function (input) { input.addEventListener('change', sync); });

    var allButton = container.querySelector('[data-action="select-all-scenarios"]');
    if (allButton) {
      allButton.addEventListener('click', function () {
        inputs.forEach(function (input) { input.checked = true; });
        sync();
      });
    }

    var clearButton = container.querySelector('[data-action="clear-scenario-selection"]');
    if (clearButton) {
      clearButton.addEventListener('click', function () {
        inputs.forEach(function (input) { input.checked = false; });
        sync();
      });
    }

    sync();
  }

  function bindPrescreenExportOption(container) {
    var toggle = container.querySelector('[data-bind="include-prescreen-report"]');
    if (!toggle) { return; }
    toggle.addEventListener('change', function () {
      includePrescreenReport = toggle.checked;
      // 明示補跑提示與按鈕字樣都跟著勾選狀態走，故整面重繪，不在此另算一份文案。
      Store.touch();
    });
  }

  /* 匯出面的既有 action 序列編排。三個 action 的 payload、response 與判定語意都不變：
     沒有現行 prescreen run 時先 prescreen.run，再 export.prescreenReport，最後 export.workpaperStream。
     任一步失敗或取消就停止後續；已完成的 action 保留其產物，各 action 各自維持原子性。 */
  function exportPrescreenReportForRun(sourceRunId) {
    return global.JetApi.exportPrescreenReport({ runId: sourceRunId }).then(function (data) {
      Store.upsertReportArtifacts([data.artifact]);
      Store.addMessage('已在專案目錄產生 Pre-screening Report。', 'info');
      return data;
    });
  }

  function runPrescreenForExport() {
    return global.JetApi.prescreenRun({}).then(function (data) {
      Store.setLastRun('prescreen', data);
      Store.addMessage('預篩選已完成，接著產生 Pre-screening Report。', 'info');
      return runId(data);
    });
  }

  function exportWorkpaper(payload) {
    return global.JetApi.exportWorkpaperStream(payload).then(function (data) {
      lastSheetStats = data.sheetStats || [];
      cleanupPreview = null;
      cleanupFeedback = null;
      Store.upsertReportArtifacts([data.artifact]);
      Store.addMessage('已在專案目錄產生 WorkingPaper（' +
        lastSheetStats.length + ' 張工作表）。', 'info');
      return data;
    });
  }

  function bind(container) {
    bindScenarioSelection(container);
    bindPrescreenExportOption(container);

    var cleanupPreviewButton = container.querySelector('[data-action="preview-report-cleanup"]');
    if (cleanupPreviewButton) {
      cleanupPreviewButton.addEventListener('click', function () {
        requestCleanupPreview(false);
      });
    }

    var cleanupConfirmButton = container.querySelector('[data-action="confirm-report-cleanup"]');
    if (cleanupConfirmButton) {
      cleanupConfirmButton.addEventListener('click', confirmReportCleanup);
    }

    var button = container.querySelector('[data-action="export-workpaper"]');
    if (!button) { return; }
    button.addEventListener('click', function () {
      var state = Store.getState();
      var payload = {
        validationRunId: runId(state.lastRuns.validate),
        scenarioRevision: filterRevision(state),
        scenarioPositions: selectedScenarioPositions.slice()
      };
      if (!payload.validationRunId || !payload.scenarioRevision ||
          payload.scenarioPositions.length === 0) { return; }

      var plan = prescreenExportPlan(state);
      if (!plan.included) {
        Ui.run('產生 WorkingPaper', function () {
          return exportWorkpaper(payload);
        }, { logCompletion: true });
        return;
      }

      Ui.run('產生底稿與 Pre-screening Report', function () {
        return Promise.resolve(plan.needsRun ? runPrescreenForExport() : plan.currentRunId)
          .then(function (sourceRunId) {
            return exportPrescreenReportForRun(sourceRunId);
          })
          .then(function () {
            return exportWorkpaper(payload);
          });
      }, { logCompletion: true });
    });
  }

  Ui.registerStep('export', render);
})(window);
