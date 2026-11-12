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
  // 只在第五步真的有尚未儲存的條件時出現，不另寫常駐說明。
  var UNSAVED_DRAFT_NOTICE = '「進階條件篩選」還有尚未儲存的條件，儲存成情境後才會納入底稿。';
  // 預篩選報告預設納入輸出家族；使用者可取消，取消後只影響這一份。
  var includePrescreenReport = true;

  Ui.registerWorkflowReset(function () {
    selectedScenarioPositions = null;
    selectionRevision = null;
    lastSheetStats = null;
    historyPage = 0;
    historyNewestId = null;
    includePrescreenReport = true;
  });

  function resultId(run) {
    return run && run.resultRef ? run.resultRef.resultId : null;
  }

  /* 預篩選報告的匯出面決策。文案一律取自後端 renderer（Ui.prescreenPositioningCopy）；
     這裡只決定「勾了沒」「有沒有現行 prescreen run」「完整性適不適格」三個既有狀態的組合。 */
  function prescreenExportPlan(state) {
    var eligibility = Ui.completenessEligibility(state.results.validate);
    var currentResultId = state.staleState && state.staleState.prescreen ? null : resultId(state.results.prescreen);
    return {
      positioning: Ui.prescreenPositioningCopy(state.results.prescreen),
      isEligible: eligibility.isEligible,
      gateReason: eligibility.reason,
      currentResultId: currentResultId,
      // 不適格時無法補跑，勾選也不生效：只產出工作底稿。
      included: includePrescreenReport && (!!currentResultId || eligibility.isEligible),
      needsRun: includePrescreenReport && !currentResultId && eligibility.isEligible
    };
  }

  function prescreenExportOptionHtml(plan) {
    var blocked = includePrescreenReport && !plan.currentResultId && !plan.isEligible;
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
          ? '<p class="prescreen-export__notice" data-bind="prescreen-export-notice" data-motion-key="prescreen-export-notice">' +
            Ui.esc(notice) + '</p>'
          : '') +
      '</section>'
    );
  }

  function exportButtonLabel(plan) {
    if (plan.needsRun) { return '先執行預篩選並產生底稿'; }
    return plan.included ? '產生工作底稿與預篩選報告' : '產生工作底稿';
  }

  // 動機和第五步的已儲存情境用同一個列法（Ui.scenarioRationaleText）；沒有要列的動機時，改列條件本身。
  function scenarioOptionMetaHtml(scenario) {
    var rationale = Ui.scenarioRationaleText(scenario);
    if (rationale) { return Ui.esc(rationale); }
    var readBack = Ui.FilterStep && Ui.FilterStep.readBackExpressionHtml;
    return (readBack && readBack({ groups: scenario.groups || [], __preserveJoins: true }, true)) || '已儲存篩選情境';
  }

  function filterRevision(state) {
    var resultRef = state.filterResultRef;
    if (!resultRef || resultRef.populationScope !== 'auditPeriod') { return null; }
    return resultRef.revision || null;
  }

  // 匯出按鈕可用的條件：有目前可用的資料驗證結果，而且已儲存的情境都能套用目前規則。沒有情境、或一個都沒勾選，
  // 照樣能產生底稿。
  function canExportWorkingPaper(state) {
    var hasScenarios = (state.filter.savedScenarios || []).length > 0;
    return !!resultId(state.results.validate) && (!hasScenarios || !!filterRevision(state)) &&
      Ui.completenessEligibility(state.results.validate).isEligible;
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
            '<span class="scenario-export-option__meta">' + scenarioOptionMetaHtml(scenario) + '</span>' +
          '</span>' +
        '</label>'
      );
    }).join('');

    return (
      '<section class="scenario-export-selection" aria-labelledby="scenario-export-heading">' +
        '<div class="scenario-export-selection__head">' +
          '<div>' +
            '<h3 class="scenario-export-selection__title" id="scenario-export-heading">納入底稿的篩選情境</h3>' +
            '<p class="scenario-export-selection__hint">條件儲存時間：' +
              Ui.esc(filterRevision(state) ? Ui.formatDateTime(filterRevision(state)) : '尚未套用目前規則') + '。</p>' +
            (Ui.FilterStep && Ui.FilterStep.hasUnsavedScenarioChanges && Ui.FilterStep.hasUnsavedScenarioChanges()
              ? '<p class="form-notice form-notice--warning" data-bind="export-unsaved-draft">' + Ui.esc(UNSAVED_DRAFT_NOTICE) + '</p>'
              : '') +
          '</div>' +
          '<div class="panel__actions panel__actions--start">' +
            '<button type="button" class="btn btn--ghost btn--tiny" data-action="select-all-scenarios">全選</button>' +
            '<button type="button" class="btn btn--ghost btn--tiny" data-action="clear-scenario-selection">全部取消</button>' +
          '</div>' +
        '</div>' +
        '<div class="scenario-export-grid">' + options + '</div>' +
        '<p class="scenario-export-selection__count" data-bind="selected-scenario-count">已選 ' +
          selectedScenarioPositions.length + ' / ' + scenarios.length + ' 個情境</p>' +
      '</section>'
    );
  }

  // 工作表與資料列數描述的是某一版已存情境的匯出；上游修改清掉情境、或情境換了版本後就不再顯示。
  function sheetStatsHtml(state) {
    if (!lastSheetStats || lastSheetStats.sheets.length === 0 || lastSheetStats.revision !== filterRevision(state)) { return ''; }
    var rows = lastSheetStats.sheets.map(function (sheet) {
      return '<tr><td>' + Ui.esc(sheet.sheetName) + '</td><td class="preview-table__amount">' +
        Ui.countText(sheet.rowsWritten) + '</td></tr>';
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
     出現條件＝目前版本的工作底稿已產生（Ui.currentWorkingPaperReport），與左側進度、流程總覽同一個判斷。
     條件篩選報告是獨立輸出，不是前置。 */
  function completionSummaryHtml(criteriaReport, currentWorkingPaper) {
    if (!currentWorkingPaper) { return ''; }
    var tags = (criteriaReport ? ['條件篩選報告', '工作底稿'] : ['工作底稿']).map(function (kind) {
      return '<span class="completion__tag">' + Ui.esc(kind) + '</span>';
    }).join('');
    return (
      '<section class="completion" data-motion-key="completion" role="status" aria-labelledby="completion-heading">' +
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

    var validationResultId = resultId(state.results.validate);
    var scenarioRevision = filterRevision(state);
    var needsScenarioResave = state.filter.savedScenarios.length > 0 && !scenarioRevision;
    // 沒有已儲存的情境也能產生工作底稿，第三、四步留空，由審計員在底稿說明。
    var noScenario = state.filter.savedScenarios.length === 0;
    var criteriaReport = scenarioRevision
      ? Ui.findCurrentReport(state, 'criteriaSelectionReport', {
          validationResultId: validationResultId,
          scenarioRevision: scenarioRevision,
          scenarioPositions: allScenarioPositions(state)
        })
      : null;
    var currentWorkingPaper = Ui.currentWorkingPaperReport(state);
    var workingPapers = Ui.reportHistory(state, 'workingPaper');
    var newestId = workingPapers.length ? workingPapers[0].reportId : null;
    if (historyNewestId !== newestId) { historyPage = 0; historyNewestId = newestId; }
    var pageCount = Math.max(1, Math.ceil(workingPapers.length / HISTORY_PAGE_SIZE));
    historyPage = Math.min(historyPage, pageCount - 1);
    var visibleWorkingPapers = workingPapers.slice(historyPage * HISTORY_PAGE_SIZE, (historyPage + 1) * HISTORY_PAGE_SIZE);
    var canExport = canExportWorkingPaper(state);
    var prescreenPlan = prescreenExportPlan(state);

    container.innerHTML =
      '<div class="panel" data-motion-key="export-panel">' +
        '<h2 class="panel__title">匯出底稿</h2>' +
        (needsScenarioResave ? Ui.filterScenarioProblemsHtml(state) : '') +
        completionSummaryHtml(criteriaReport, currentWorkingPaper) +
        (noScenario ? '' : scenarioSelectionHtml(state)) +
        prescreenExportOptionHtml(prescreenPlan) +
        '<div class="panel__actions export-actions">' +
          '<button type="button" class="btn" data-action="export-working-paper"' +
            (canExport ? '' : ' disabled') + '>' + Ui.esc(exportButtonLabel(prescreenPlan)) +
            '</button>' +
        '</div>' +
        '<section class="report-output">' +
          '<div class="report-output__head">' +
            '<h3 class="report-output__title">工作底稿版本紀錄</h3>' +
          '</div>' +
          // 翻頁是整頁換掉：新的一頁只淡入，不讓五十份文件卡一起收起又長出來；同一頁裡新產生的底稿才長出來。
          '<div data-motion="fade" data-motion-key="workingPapers-page-' + historyPage + '">' +
          Ui.reportListHtml(visibleWorkingPapers, '尚未產生工作底稿。', { history: true, reveal: true, motion: 'workingPapers' }) +
          '</div>' +
          // 換頁沿用明細表的共用分頁外觀；一頁仍是 50 份文件，只有一頁時不顯示。
          Ui.pageControlsHtml(historyPage + 1, workingPapers.length, { pageSize: HISTORY_PAGE_SIZE,
            previousAttribute: 'data-history-page="-1"', nextAttribute: 'data-history-page="1"' }) +
          sheetStatsHtml(state) +
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
      var exportButton = container.querySelector('[data-action="export-working-paper"]');
      if (exportButton) { exportButton.disabled = !canExportWorkingPaper(Store.getState()); }
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
        // 清除時被取消勾選的情境先輕微晃一下，讓使用者知道哪些被拿掉了。
        // 只晃原本有勾的；東西不會離開畫面，所以直接用 shake，不經過 removing。
        if (global.JetMotion && typeof global.JetMotion.shake === 'function') {
          var cleared = [];
          inputs.forEach(function (input) {
            var option = input.checked && typeof input.closest === 'function' ? input.closest('.scenario-export-option') : null;
            if (option) { cleared.push(option); }
          });
          if (cleared.length) { global.JetMotion.shake(cleared); }
        }
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
     沒有現行 prescreen run 時先 prescreen.run，再 export.prescreenReport，最後 export.workingPaperStream。
     任一步失敗或取消就停止後續；已完成的 action 保留其產物，各 action 各自維持原子性。 */
  function exportPrescreenReportForRun(sourceResultId, project, payload) {
    return global.JetApi.exportPrescreenReport({ resultId: sourceResultId }).then(function (data) {
      requireExportActive(project, payload, true);
      Store.applyReportExport(data);
      Store.addMessage('已在案件資料夾產生預篩選報告。', 'info');
      return data;
    });
  }

  function runPrescreenForExport(project, payload) {
    return global.JetApi.prescreenRun({}).then(function (data) {
      requireExportActive(project, payload, true);
      // 不另記「預篩選已完成，接著產生預篩選報告」。
      Store.setResult('prescreen', data);
      return resultId(data);
    });
  }

  // 完成句由 Ui.run 接上耗時後記一則；成功時只留一則。
  function exportWorkingPaper(payload, project) {
    return global.JetApi.exportWorkingPaperStream(payload).then(function (data) {
      requireExportActive(project, payload, true);
      lastSheetStats = { revision: payload.scenarioRevision, sheets: data.sheetStats || [] };
      Store.applyReportExport(data);
      return data;
    });
  }

  function workingPaperCompletionMessage(data) {
    return data ? '已在案件資料夾產生工作底稿（' + (data.sheetStats || []).length + ' 張工作表）。' : '';
  }

  function requireExportActive(project, payload, completed) {
    var state = Store.getState();
    if ((!completed && state.cancellationRequested) || state.project !== project
        || resultId(state.results.validate) !== payload.validationResultId
        || filterRevision(state) !== payload.scenarioRevision) {
      var error = new Error('後續底稿產生已取消，已完成的報告會保留。');
      error.code = 'operation_cancelled';
      error.completedResultsPreserved = true;
      throw error;
    }
  }

  function bind(container) {
    Ui.bindReports(container);
    Array.prototype.forEach.call(container.querySelectorAll('[data-history-page]'), function (button) {
      button.addEventListener('click', function () {
        historyPage += Number(button.getAttribute('data-history-page'));
        Store.touch();
      });
    });
    bindScenarioSelection(container);
    bindPrescreenExportOption(container);

    var button = container.querySelector('[data-action="export-working-paper"]');
    if (!button) { return; }
    button.addEventListener('click', function () {
      var state = Store.getState();
      var project = state.project;
      var payload = {
        validationResultId: resultId(state.results.validate),
        scenarioRevision: filterRevision(state),
        scenarioPositions: selectedScenarioPositions.slice()
      };
      if (!canExportWorkingPaper(state)) { return; }

      var plan = prescreenExportPlan(state);
      if (!plan.included) {
        Ui.run('產生工作底稿', function () {
          requireExportActive(project, payload);
          return exportWorkingPaper(payload, project);
        }, { completionMessage: workingPaperCompletionMessage });
        return;
      }

      // 要先補跑預篩選、第四步又有還沒儲存的科目分類時，和第四步的「執行預篩選」一樣先問。
      // 選「儲存並繼續」會清掉已存情境，這次匯出就不再送出。
      if (plan.needsRun && Ui.pendingAccountCategoryCount()) {
        Ui.confirmPendingAccountCategories('預篩選只會使用已儲存的分類。', '先不儲存，直接執行').then(function (proceed) {
          var latest = Store.getState();
          if (proceed && latest.project === project && filterRevision(latest) === payload.scenarioRevision) { exportWithPrescreen(); }
        });
        return;
      }
      exportWithPrescreen();

      function exportWithPrescreen() {
        Ui.run('產生工作底稿與預篩選報告', function () {
          requireExportActive(project, payload);
          return Promise.resolve(plan.needsRun ? runPrescreenForExport(project, payload) : plan.currentResultId)
            .then(function (sourceResultId) {
              requireExportActive(project, payload);
              return exportPrescreenReportForRun(sourceResultId, project, payload);
            })
            .then(function () {
              requireExportActive(project, payload);
              return exportWorkingPaper(payload, project);
            });
        }, { completionMessage: workingPaperCompletionMessage });
      }
    });
  }

  Ui.registerStep('export', render);
})(window);
