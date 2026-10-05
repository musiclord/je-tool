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
  var historyPage = 0;
  var historyNewestId = null;
  var HISTORY_PAGE_SIZE = Ui.REPORT_HISTORY_PAGE_SIZE;
  var NO_SCENARIO_NOTICE = '目前沒有已儲存的篩選情境，無法匯出工作底稿。請到「進階條件篩選」儲存至少一個篩選情境，再匯出工作底稿。';
  // Pre-screening Report 預設納入輸出家族；使用者可取消，取消後只影響這一份。
  var includePrescreenReport = true;

  Ui.registerWorkflowReset(function () {
    selectedScenarioPositions = null;
    selectionRevision = null;
    lastSheetStats = null;
    historyPage = 0;
    historyNewestId = null;
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
    var currentRunId = state.staleState && state.staleState.prescreen ? null : runId(state.lastRuns.prescreen);
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
        ? plan.gateReason + ' 本次只會產生工作底稿。'
        : '');
    return (
      '<section class="prescreen-export" aria-labelledby="prescreen-export-heading">' +
        '<h3 class="scenario-export-selection__title" id="prescreen-export-heading">一併產出的報告</h3>' +
        '<label class="scenario-export-option">' +
          '<input type="checkbox" data-bind="include-prescreen-report"' +
            (includePrescreenReport ? ' checked' : '') + '>' +
          '<span class="scenario-export-option__copy">' +
            '<span class="scenario-export-option__name">預篩選報告</span>' +
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
    return plan.included ? '產生工作底稿與預篩選報告' : '產生工作底稿';
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
            '<span class="scenario-export-option__name">C' + position + '，' + Ui.esc(scenario.name) + '</span>' +
            '<span class="scenario-export-option__meta">' + Ui.esc(scenario.rationale || '已儲存篩選情境') + '</span>' +
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
            '<p class="scenario-export-selection__hint">預設全選。底稿會標示符合所選情境的分錄，並保留同傳票參考分錄。</p>' +
            '<p class="scenario-export-selection__hint">條件儲存時間：' +
              Ui.esc(filterRevision(state) ? Ui.formatDateTime(filterRevision(state)) : '尚無可用版本') + '。' +
              '使用目前資料及已儲存條件；草稿不會自動納入。</p>' +
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
          '>請至少選擇一個已儲存篩選情境。</p>' +
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

  /* 完成摘要。
     設計稿把「完成」畫成第六個步驟；此處刻意只轉成本步驟內的完成狀態——
     流程仍是六步（最後一步＝匯出底稿），STEPS、閘門、進度持久化與 action 語意皆不變。
     出現條件＝目前版本的工作底稿已產生（Ui.currentWorkpaperArtifact），與左側進度、流程總覽同一個判斷。
     條件篩選報告是獨立輸出，不再是前置。 */
  function completionSummaryHtml(criteriaArtifact, currentWorkpaper) {
    if (!currentWorkpaper) { return ''; }
    var tags = (criteriaArtifact ? ['條件篩選報告', '工作底稿'] : ['工作底稿']).map(function (kind) {
      return '<span class="completion__tag">' + Ui.esc(kind) + '</span>';
    }).join('');
    return (
      '<section class="completion" role="status" aria-labelledby="completion-heading">' +
        '<span class="completion__mark" aria-hidden="true">✓</span>' +
        '<div class="completion__copy">' +
          '<h3 class="completion__title" id="completion-heading">案件流程已完成</h3>' +
          '<p class="completion__body">本次工作底稿已儲存至案件資料夾。' +
            '需要調整情境時，可重新選擇後再次匯出。</p>' +
          '<div class="completion__tags">' + tags + '</div>' +
        '</div>' +
      '</section>'
    );
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
    // 沒有已儲存的情境時，第六步仍可因既有版本紀錄而進入查看，但不能匯出。
    var noScenario = state.filter.savedScenarios.length === 0;
    var criteriaArtifact = scenarioRevision
      ? Ui.findCurrentReportArtifact(state, 'criteriaSelectionReport', {
          validationRunId: validationRunId,
          scenarioRevision: scenarioRevision,
          scenarioPositions: allScenarioPositions(state)
        })
      : null;
    var currentWorkpaper = Ui.currentWorkpaperArtifact(state);
    var workpapers = Ui.reportArtifactHistory(state, 'workingPaper');
    var newestId = workpapers.length ? workpapers[0].artifactId : null;
    if (historyNewestId !== newestId) { historyPage = 0; historyNewestId = newestId; }
    var pageCount = Math.max(1, Math.ceil(workpapers.length / HISTORY_PAGE_SIZE));
    historyPage = Math.min(historyPage, pageCount - 1);
    var visibleWorkpapers = workpapers.slice(historyPage * HISTORY_PAGE_SIZE, (historyPage + 1) * HISTORY_PAGE_SIZE);
    var canExport = !!validationRunId && !!scenarioRevision &&
      Ui.completenessEligibility(state.lastRuns.validate).isEligible && selectedScenarioPositions.length > 0;
    var prescreenPlan = prescreenExportPlan(state);

    container.innerHTML =
      '<div class="panel">' +
        '<h2 class="panel__title">匯出底稿</h2>' +
        (populationScope
          ? '<p class="panel__hint">已儲存的分錄測試範圍：' + Ui.esc(populationScopeLabel(populationScope)) + '。</p>'
          : '') +
        (needsScenarioResave ? Ui.filterScenarioProblemsHtml(state) : '') +
        (noScenario ? '<p class="form-notice" data-bind="export-no-scenario">' + Ui.esc(NO_SCENARIO_NOTICE) + '</p>' : '') +
        completionSummaryHtml(criteriaArtifact, currentWorkpaper) +
        (noScenario ? '' : scenarioSelectionHtml(state)) +
        prescreenExportOptionHtml(prescreenPlan) +
        '<div class="panel__actions export-actions">' +
          '<button type="button" class="btn" data-action="export-workpaper"' +
            (canExport ? '' : ' disabled') + '>' + Ui.esc(exportButtonLabel(prescreenPlan)) +
            '</button>' +
        '</div>' +
        '<section class="report-output">' +
          '<div class="report-output__head">' +
            '<h3 class="report-output__title">工作底稿版本紀錄</h3>' +
          '</div>' +
          '<p class="panel__hint">每次匯出會新增版本，不覆蓋既有底稿。</p>' +
          Ui.reportArtifactListHtml(visibleWorkpapers, '尚未產生工作底稿。', { history: true, reveal: true }) +
          (pageCount > 1 ? '<div class="panel__actions">' +
            '<button type="button" class="btn btn--ghost btn--tiny" data-history-page="-1"' +
              (historyPage === 0 ? ' disabled' : '') + '>上一頁</button>' +
            '<span>第 ' + (historyPage + 1) + ' 頁，共 ' + pageCount + ' 頁（' + workpapers.length + ' 份）</span>' +
            '<button type="button" class="btn btn--ghost btn--tiny" data-history-page="1"' +
              (historyPage + 1 === pageCount ? ' disabled' : '') + '>下一頁</button></div>' : '') +
          sheetStatsHtml() +
        '</section>' +
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
        exportButton.disabled = empty || !runId(state.lastRuns.validate) ||
          !revision || !Ui.completenessEligibility(state.lastRuns.validate).isEligible;
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
  function exportPrescreenReportForRun(sourceRunId, project, payload) {
    return global.JetApi.exportPrescreenReport({ runId: sourceRunId }).then(function (data) {
      requireExportActive(project, payload, true);
      Store.applyReportExport(data);
      Store.addMessage('已在案件資料夾產生預篩選報告。', 'info');
      return data;
    });
  }

  function runPrescreenForExport(project, payload) {
    return global.JetApi.prescreenRun({}).then(function (data) {
      requireExportActive(project, payload, true);
      Store.setLastRun('prescreen', data);
      Store.addMessage('預篩選已完成，接著產生預篩選報告。', 'info');
      return runId(data);
    });
  }

  function exportWorkpaper(payload, project) {
    return global.JetApi.exportWorkpaperStream(payload).then(function (data) {
      requireExportActive(project, payload, true);
      lastSheetStats = data.sheetStats || [];
      Store.applyReportExport(data);
      Store.addMessage('已在案件資料夾產生工作底稿（' +
        lastSheetStats.length + ' 張工作表）。', 'info');
      return data;
    });
  }

  function requireExportActive(project, payload, completed) {
    var state = Store.getState();
    if ((!completed && state.cancellationRequested) || state.project !== project
        || runId(state.lastRuns.validate) !== payload.validationRunId
        || filterRevision(state) !== payload.scenarioRevision) {
      var error = new Error('後續底稿產生已取消，已完成的報告會保留。');
      error.code = 'operation_cancelled';
      error.completedResultsPreserved = true;
      throw error;
    }
  }

  function bind(container) {
    Ui.bindReportArtifacts(container);
    Array.prototype.forEach.call(container.querySelectorAll('[data-history-page]'), function (button) {
      button.addEventListener('click', function () {
        historyPage += Number(button.getAttribute('data-history-page'));
        Store.touch();
      });
    });
    bindScenarioSelection(container);
    bindPrescreenExportOption(container);

    var button = container.querySelector('[data-action="export-workpaper"]');
    if (!button) { return; }
    button.addEventListener('click', function () {
      var state = Store.getState();
      var project = state.project;
      var payload = {
        validationRunId: runId(state.lastRuns.validate),
        scenarioRevision: filterRevision(state),
        scenarioPositions: selectedScenarioPositions.slice()
      };
      if (!payload.validationRunId || !payload.scenarioRevision ||
          payload.scenarioPositions.length === 0) { return; }

      var plan = prescreenExportPlan(state);
      if (!plan.included) {
        Ui.run('產生工作底稿', function () {
          requireExportActive(project, payload);
          return exportWorkpaper(payload, project);
        }, { logCompletion: true });
        return;
      }

      Ui.run('產生工作底稿與預篩選報告', function () {
        requireExportActive(project, payload);
        return Promise.resolve(plan.needsRun ? runPrescreenForExport(project, payload) : plan.currentRunId)
          .then(function (sourceRunId) {
            requireExportActive(project, payload);
            return exportPrescreenReportForRun(sourceRunId, project, payload);
          })
          .then(function () {
            requireExportActive(project, payload);
            return exportWorkpaper(payload, project);
          });
      }, { logCompletion: true });
    });
  }

  Ui.registerStep('export', render);
})(window);
