/*
  第五步：進階條件篩選 —— 已存情境、矩陣與條件篩選報告。
  已存清單的儲存、重新檢查與移除流程、展開後的命中預覽、標記矩陣與條件篩選報告的匯出。命中與排序都交給後端分頁。
  第五步分成五個檔案，說明見 filter-step.js 開頭；各檔把自己的函式掛在 JetUi.FilterStep，其他檔案透過它呼叫。
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;
  var Ui = global.JetUi;
  // 第五步五個檔案共用的函式與檢視狀態；各檔在結尾把自己的函式掛上去。
  var FilterStep = Ui.FilterStep = Ui.FilterStep || {};
  // 其他檔案定義的函式：呼叫時才去 FilterStep 取，所以和載入順序無關。
  function forward(name) { return function () { return FilterStep[name].apply(null, arguments); }; }
  var showOnly = forward('showOnly');
  var selectedPopulationScope = forward('selectedPopulationScope');
  var committedPopulationScope = forward('committedPopulationScope');
  var scenarioResponseGuard = forward('scenarioResponseGuard');
  var dataGeneration = forward('dataGeneration');
  var sameGeneration = forward('sameGeneration');
  var announceClearedComposition = forward('announceClearedComposition');
  var fillSmoothly = forward('fillSmoothly');
  var toWireScenario = forward('toWireScenario');
  var toWireDraft = forward('toWireDraft');
  var setActiveGroup = forward('setActiveGroup');
  var readBackHtml = forward('readBackHtml');
  var render = forward('render');
  var restoreScenarioRemovalFocus = forward('restoreScenarioRemovalFocus');
  // 其他檔案定義的常數：只讀，定義它的檔案先載入。
  var matrixResponseGuard = FilterStep.matrixResponseGuard;
  var COMMON_CARD_KEY = FilterStep.COMMON_CARD_KEY;

  // JSON 物件屬性順序不是內容；清單或規則的陣列順序則仍有意義。
  function scenarioContentKey(scenario) {
    return JSON.stringify(scenario, function (_, value) {
      if (!value || typeof value !== 'object' || Array.isArray(value)) { return value; }
      var ordered = Object.create(null);
      Object.keys(value).sort().forEach(function (key) { ordered[key] = value[key]; });
      return ordered;
    });
  }

  function savedFeedbackText(draft) {
    if (FilterStep.viewState.lastSavedName && draftIsBlank(draft)) {
      return '已儲存「' + Ui.esc(FilterStep.viewState.lastSavedName) + '」。可在「已儲存情境與矩陣」查看或修改。';
    }
    if (!FilterStep.viewState.savedDraftKey || typeof draft.__editingIndex !== 'number') { return ''; }
    return scenarioContentKey(toWireDraft(draft)) === FilterStep.viewState.savedDraftKey
      ? '已儲存「' + Ui.esc(draft.name) + '」。' : '條件或說明已變更，尚未儲存。';
  }

  // 草稿內容的比較鍵：和儲存時送出的是同一個投影，只看條件、名稱與動機，不看畫面狀態。
  function draftContentKey(draft) {
    return scenarioContentKey(toWireDraft(draft));
  }

  // 還沒存成情境的草稿，沒有條件、也沒有手寫的名稱或動機時，換掉不會少任何東西。
  function draftIsBlank(draft) {
    function written(text, dirty) { return !!dirty && !!String(text || '').trim(); }
    return (draft.groups || []).every(function (group) { return !(group.rules || []).length; }) &&
      !written(draft.name, draft.__nameDirty) && !written(draft.rationale, draft.__rationaleDirty);
  }

  function editedSavedScenario(state) {
    var draft = state.filter.draft;
    if (typeof draft.__editingIndex !== 'number' || draft.__editingSavedRef !== state.filter.savedScenarios) { return null; }
    return state.filter.savedScenarios[draft.__editingIndex] || null;
  }

  // 有沒有還沒存的修改：剛儲存過的情境和儲存的內容比，與「已儲存」提示同一個比較；
  // 其他草稿和換成它之後第一次畫好時的內容比。
  function hasUnsavedScenarioChanges() {
    var state = Store.getState(), draft = state.filter.draft;
    var edited = editedSavedScenario(state);
    if (!edited && draftIsBlank(draft)) { return false; }
    var key = draftContentKey(draft);
    if (edited && FilterStep.viewState.savedDraftKey) { return key !== FilterStep.viewState.savedDraftKey; }
    return FilterStep.viewState.cleanDraftKey !== null && key !== FilterStep.viewState.cleanDraftKey;
  }

  function replacementWording(kind, name) {
    if (kind === 'template') { return { effect: '套用範例「' + name + '」會換掉', verb: '套用' }; }
    var verb = kind === 'copy' ? '複製' : '開啟';
    return { effect: verb + '「' + name + '」會換掉', verb: verb };
  }

  FilterStep.saveDraftForReplacement = null; // bind 時接上目前畫面的儲存動作，回傳 Promise。

  // 使用者 2026-10-11 裁定：目前的情境有修改還沒儲存時，開啟或複製已存情境與套用範例都先詢問，
  // 用第四步科目分類也在用的共用對話框（Ui.confirmUnsaved）；沒有修改時直接換。取消編輯是明確放棄修改，不經過這裡。
  // 不儲存時修改會被換掉，所以第二顆按鈕寫「不儲存，直接…」，和科目分類結束 JET 時的寫法相同。
  // 儲存情境不會清掉其他結果，預設焦點停在儲存，比照科目分類沒有東西會被清除時的做法。
  function guardDraftReplacement(kind, name, proceed) {
    if (!hasUnsavedScenarioChanges()) { proceed(); return; }
    var edited = editedSavedScenario(Store.getState());
    var wording = replacementWording(kind, name);
    Ui.confirmUnsaved({
      id: 'filter-pending',
      title: '篩選情境尚未儲存',
      body: edited
        ? '情境「' + edited.name + '」的修改尚未儲存。' + wording.effect + '這些修改。'
        : '目前的條件尚未儲存為情境。' + wording.effect + '這些條件。',
      continueLabel: '不儲存，直接' + wording.verb,
      saveLabel: '儲存並' + wording.verb,
      // 儲存失敗或被擋下時留在原情境，原因照舊寫在儲存按鈕上方與訊息紀錄。
      save: function () {
        return Promise.resolve(FilterStep.saveDraftForReplacement ? FilterStep.saveDraftForReplacement() : null)
          .then(function () { return !hasUnsavedScenarioChanges(); });
      },
      proceed: proceed
    });
  }

  // 保存只改變目前編輯器的狀態，不切換到其他工作。
  // 保留預覽，並讓之後的保存寫回同一個位置，避免重複新增。
  function draftMetadataKey(draft) {
    return JSON.stringify([draft.name, draft.rationale, !!draft.__nameDirty, !!draft.__rationaleDirty]);
  }

  // 錯誤細節帶 scenarioPosition 時，只有屬於這次儲存的那個情境才標在目前草稿上；沒帶位置的照舊視為目前草稿。
  function belongsToTarget(detail, targetPosition) {
    return typeof targetPosition !== 'number' || typeof detail.scenarioPosition !== 'number' ||
      detail.scenarioPosition === targetPosition;
  }

  // 儲存按鈕上方的失敗說明。錯誤屬於別的情境時寫出是哪一個情境（2026-10-13 暫定句，之後的文字批次統一改字）。
  function saveFailureText(error, targetPosition) {
    var details = error && Array.isArray(error.details) ? error.details : [];
    var other = details.filter(function (detail) { return detail && !belongsToTarget(detail, targetPosition); })[0];
    var reason = Ui.errorText(error, '請稍後再試。');
    return other
      ? '儲存失敗：情境 ' + other.scenarioPosition + '「' + (other.scenarioName || '') + '」：' + reason
      : '儲存失敗：' + reason;
  }

  // 「重新檢查並儲存情境」的結果（使用者 2026-10-13 勾選頁確認 54）。還有要修改的情境不算失敗。
  function scenarioRecheckSentence(data) {
    var check = data && data.filterScenarioCheck;
    var remaining = check && Array.isArray(check.problems) ? check.problems.length : 0;
    if (!remaining) { return '已依目前規則重新檢查 ' + data.savedCount + ' 個情境，全部已更新。'; }
    return '已依目前規則重新檢查情境：' + (data.savedCount - remaining) + ' 個已更新，' + remaining +
      ' 個仍需修改，請見上方清單。';
  }

  function filterCommitSnapshot(state) {
    return { project: state.project, generation: dataGeneration(state), savedRef: state.filter.savedScenarios,
      draft: state.filter.draft, revision: Store.getFilterDraftRev(),
      metadataKey: draftMetadataKey(state.filter.draft) };
  }

  function acceptsFilterCommit(snapshot) {
    var latest = Store.getState();
    return !!latest.project && latest.project === snapshot.project &&
      latest.filter.savedScenarios === snapshot.savedRef && sameGeneration(dataGeneration(latest), snapshot.generation);
  }

  // 清單換版時，編輯中的情境必須跟著移位；只有移除該情境才清空草稿。
  function completeScenarioListChange(snapshot, data, removedIndex) {
    if (!acceptsFilterCommit(snapshot)) { return false; }
    var draft = Store.getState().filter.draft;
    var linked = draft.__editingSavedRef === snapshot.savedRef && typeof draft.__editingIndex === 'number';
    var clear = linked && draft.__editingIndex === removedIndex;
    var patch = linked && !clear ? { __editingSavedRef: data.scenarios,
      __editingIndex: draft.__editingIndex - (draft.__editingIndex > removedIndex ? 1 : 0) } : null;
    if (clear) {
      FilterStep.viewState.lastSavedName = null; FilterStep.viewState.savedDraftKey = null; FilterStep.viewState.disclosures = {}; FilterStep.selectedRule = null;
    }
    FilterStep.viewState.pendingRemovalIndex = null;
    Store.applyFilterCommit(data, patch, clear);
    return true;
  }

  function completeScenarioSave(snapshot, data, index, authored) {
    if (!acceptsFilterCommit(snapshot)) { return false; }
    var latest = Store.getState();
    var sameDraft = latest.filter.draft === snapshot.draft;
    var unchanged = sameDraft && Store.getFilterDraftRev() === snapshot.revision &&
      draftMetadataKey(latest.filter.draft) === snapshot.metadataKey;
    var patch = null;
    if (sameDraft) {
      var saved = data.scenarios[index] || authored;
      // 請求期間繼續編輯時保留新修改，並接上本次儲存的版本；原草稿未變才清空。
      patch = { __editingIndex: index, __editingSavedRef: data.scenarios, __templateName: false, __templateRationale: false };
      if (unchanged) {
        FilterStep.viewState.lastSavedName = saved.name;
        FilterStep.viewState.disclosures = {};
        FilterStep.selectedRule = null;
      }
      // 比對基準的條件用送出時的內容：後端存檔時只保留每種條件用得到的欄位（使用者 2026-10-15 裁定），回應裡的條件
      // 比草稿精簡，直接拿來比會一直判成尚未儲存。名稱與動機仍用回應，有卡片的情境名稱留白時後端會補上預設名稱。
      FilterStep.viewState.savedDraftKey = scenarioContentKey(toWireScenario(
        Object.assign({}, authored, { name: saved.name, rationale: saved.rationale })));
    }
    // 請求執行期間，使用者可能已開啟另一個已保存的情境。
    if (!sameDraft && latest.filter.draft.__editingSavedRef === snapshot.savedRef) {
      patch = { __editingSavedRef: data.scenarios };
    }
    Store.applyFilterCommit(data, patch, unchanged);
    if (unchanged && FilterStep.viewState.workspacePane === 'filter' && global.JetFocus) {
      global.JetFocus.defer(function () { return document.querySelector('.filter-workspace [data-builder-title]'); });
    }
    return true;
  }

  function scenarioPillsHtml(groups) {
    return readBackHtml({ groups: groups || [], __preserveJoins: true });
  }

  function savedScenariosHtml(state) {
    var saved = state.filter.savedScenarios;
    var items = saved.length === 0
      ? '<p class="empty-state" data-motion-key="saved-empty">尚未儲存情境。請到「篩選與檢視」設定條件並儲存。</p>'
      : saved.map(function (s, i) {
          // 展開狀態取自 viewState：草稿編輯的 bump 重繪不得把已展開的詳情收回（內容由
          // restoreViewState 以快取回填，不重抓）。
          var open = !!FilterStep.viewState.openScenarios[i];
          var confirmingRemoval = FilterStep.viewState.pendingRemovalIndex === i;
          // 整列標題就是開關，再點一次收起；展開的內容放在下凹的底槽裡和下一個情境分開。使用者 2026-10-29 指出情境之間只有不明顯的分隔線，
          // 請 Claude 研究改善；點標題就收合原本是使用者對「傳票XXX的分錄」標題的期望，依 2026-10-19 同功能的元件只留一套，情境的整列標題也用同一種開關。
          return (
            '<article class="saved-scenario' + (open ? ' saved-scenario--open' : '') + '" data-motion-key="' + Ui.esc('saved-' + s.name) + '">' +
              '<div class="saved-scenario__head">' +
                '<button type="button" class="saved-scenario__toggle" data-action="toggle-scenario" data-index="' + i + '"' +
                  Ui.disclosureAttributes(open, 'scenario-body-' + i) + '>' +
                  '<span class="saved-scenario__name">' + (i + 1) + '. ' + Ui.esc(s.name) + '</span>' +
                '</button>' +
                // 「操作」是共用選單（ui-core.js 的 bindMenus），和資料預覽的「其他資料」同一套。
                '<span class="jet-menu">' +
                  '<button type="button" class="btn btn--ghost btn--tiny jet-menu__toggle" aria-haspopup="menu" aria-expanded="false" ' +
                    'aria-controls="saved-actions-' + i + '" aria-label="操作：' + Ui.esc(s.name) + '">操作</button>' +
                  '<div class="jet-menu__items" id="saved-actions-' + i + '" role="menu" aria-label="操作：' + Ui.esc(s.name) + '" hidden>' +
                    '<button type="button" class="jet-menu__item" role="menuitem" tabindex="-1" data-action="edit-scenario" data-index="' + i + '">編輯</button>' +
                    '<button type="button" class="jet-menu__item" role="menuitem" tabindex="-1" data-action="copy-scenario" data-index="' + i + '">另存副本</button>' +
                    '<button type="button" class="jet-menu__item" role="menuitem" tabindex="-1" data-action="remove-scenario" data-index="' + i +
                      '"' + (confirmingRemoval ? ' hidden' : '') + '>移除</button>' +
                  '</div>' +
                '</span>' +
              '</div>' +
              (confirmingRemoval
                ? '<div class="saved-scenario__confirm" data-motion-key="' + Ui.esc('confirm-' + s.name) + '" role="group" aria-label="確認移除篩選情境">' +
                    '<p class="form-notice form-notice--warning">移除這個情境；若正在編輯它，目前的條件也會清空。</p>' +
                    '<div class="saved-scenario__confirm-actions">' +
                      '<button type="button" class="btn btn--ghost" data-action="cancel-remove-scenario" ' +
                        'data-index="' + i + '">取消</button>' +
                      '<button type="button" class="btn btn--danger" data-action="confirm-remove-scenario" ' +
                        'data-index="' + i + '">確認移除</button>' +
                    '</div>' +
                  '</div>'
                : '') +
              '<div class="saved-scenario__body" id="scenario-body-' + i + '" data-bind="scenario-body-' + i + '" data-collapsible' + (open ? '' : ' hidden') + '>' +
                // 只用自訂條件時自動產生的動機只是把篩選條件再念一次，下面已經列出條件，所以不列；其他動機照列。
                (Ui.isGeneratedRationale(s.rationale) ? '' : '<p class="saved-scenario__rationale">' + Ui.esc(s.rationale) + '</p>') +
                '<div class="scenario-pills">' + scenarioPillsHtml(s.groups) + '</div>' +
                '<div class="saved-scenario__preview" data-bind="scenario-preview-' + i + '" data-motion-key="' + Ui.esc('saved-preview-' + s.name) + '"></div>' +
              '</div>' +
            '</article>'
          );
        }).join('');

    return (
      '<section class="rule-card" data-motion-list="saved">' +
        '<h3 class="rule-card__title" data-bind="saved-scenarios-title" tabindex="-1">已儲存篩選情境' +
          '<span class="check-item__count">' + saved.length + ' / 10</span></h3>' +
        // 有情境無法套用目前規則時，原因、下一步與「重新檢查並儲存情境」只在上方「GL：查核期間內的分錄」說一次；
        // 這裡和下面的矩陣、報告只寫目前狀態（使用者 2026-10-13 選擇處理同一畫面重複的說明）。
        // 原本標題下另寫一句已儲存版本的 GL 範圍，使用者 2026-10-29 指出多餘後拿掉；範圍只在上方說一次。
        items +
      '</section>'
    );
  }

  function criteriaReportHtml(state) {
    var resultRef = state.filterResultRef;
    var committedScope = committedPopulationScope(state);
    var validationRunId = state.lastRuns.validate && state.lastRuns.validate.resultRef
      ? state.lastRuns.validate.resultRef.runId : null;
    var artifact = committedScope && resultRef && validationRunId
      ? Ui.findCurrentReportArtifact(state, 'criteriaSelectionReport', {
          validationRunId: validationRunId,
          scenarioRevision: resultRef.revision
        })
      : null;
    var canExport = !!committedScope && !!validationRunId &&
      state.filter.savedScenarios.length > 0;
    // 只要曾經產生過報告或上游資料已變更，按鈕就叫「重新產生」，和第六步提示裡指的按鈕名稱一致。
    var regenerate = !!artifact || (state.staleState && state.staleState.filter) ||
      Ui.reportArtifactHistory(state, 'criteriaSelectionReport').length > 0;
    return (
      '<section class="report-output">' +
        '<div class="report-output__head">' +
          '<div>' +
            // 報告是否為目前版本由按鈕名稱與下方清單表示，標題下不另寫「條件或資料變更後，請重新產生」（使用者 2026-10-29 指出常駐說明太多）。
            '<h3 class="report-output__title">條件篩選報告</h3>' +
          '</div>' +
          '<button type="button" class="btn btn--ghost" data-action="export-criteria-report"' +
            (canExport ? '' : ' disabled') + '>' +
            (regenerate ? '重新產生條件篩選報告' : '產生條件篩選報告') +
          '</button>' +
        '</div>' +
        Ui.reportArtifactListHtml(artifact ? [artifact] : [],
          canExport ? '尚未產生目前版本的報告。' :
            (state.filter.savedScenarios.length > 0 && !committedPopulationScope(state)
              ? '已儲存的篩選情境尚未套用目前規則，暫時無法產生報告。'
              : '請先完成目前版本的資料驗證並儲存至少一個篩選情境。'), { reveal: true, motion: 'criteria' }) +
      '</section>'
    );
  }

  /* ---- 高風險條件矩陣預覽：符合條件摘要與傳票交叉表 ----
     全程零商業邏輯：只把 wire 回來的 matchedPositions 對映成 C 欄 ✓／空白、組表；
     查詢/pivot/計算/SQL 全在後端三 action（tagMatrixScenarios/VoucherPage/RowPage）。 */

  // 摺疊外殼；展開時惰性載入(bind() 的 toggle 處理)。無已存情境時不顯示展開鈕,直接友善空狀態。
  function tagMatrixHtml(state) {
    var saved = state.filter.savedScenarios;
    if (saved.length === 0) {
      return (
        '<section class="rule-card">' +
          '<h3 class="rule-card__title">高風險條件矩陣</h3>' +
          '<p class="empty-state">尚未儲存任何篩選情境；先在上方儲存情境，每個情境會成為矩陣的一欄。</p>' +
        '</section>'
      );
    }

    if (!committedPopulationScope(state)) {
      return (
        '<section class="rule-card">' +
          '<h3 class="rule-card__title">高風險條件矩陣</h3>' +
          '<p class="empty-state">已儲存的篩選情境尚未套用目前規則，矩陣暫不顯示。</p>' +
        '</section>'
      );
    }

    // 展開狀態取自 viewState（跨 bump 重繪保留）；內容由 restoreViewState 以摘要快取回填。
    var open = FilterStep.viewState.matrixOpen;
    return (
      '<section class="rule-card">' +
        '<div class="rule-card__head">' +
          '<h3 class="rule-card__title">高風險條件矩陣</h3>' +
          '<button type="button" class="btn btn--ghost jet-disclosure" data-action="toggle-matrix"' +
            Ui.disclosureAttributes(open, 'filter-matrix-body') + '>查看矩陣</button>' +
        '</div>' +
        '<div class="saved-scenario__body" id="filter-matrix-body" data-bind="matrix-body" data-motion-key="matrix-body" data-collapsible' + (open ? '' : ' hidden') + '>' +
          '<div class="filter-matrix-tabs" role="tablist" aria-label="矩陣檢視">' +
            matrixViewButton('scenarios', '符合條件摘要') + matrixViewButton('vouchers', '傳票交叉表') + '</div>' +
          '<div data-bind="matrix-scenarios" id="matrix-panel-scenarios" role="tabpanel" aria-labelledby="matrix-tab-scenarios" data-motion-pane></div>' +
          '<div data-bind="matrix-vouchers" id="matrix-panel-vouchers" role="tabpanel" aria-labelledby="matrix-tab-vouchers" data-motion-pane></div>' +
        '</div>' +
      '</section>'
    );
  }

  // 由情境摘要組「C 欄定義」：依 position 升冪,每欄 { position, label:'C{p}', name }。
  // C 欄欄序與標頭一律以此為準(brief：依 tagMatrixScenarios 回的 position 升冪)。
  function buildTagColumns(scenarios) {
    return (scenarios || [])
      .slice()
      .sort(function (a, b) { return a.position - b.position; })
      .map(function (s) {
        return { position: s.position, label: 'C' + s.position, name: s.name };
      });
  }

  // 情境摘要表(step3 交叉參考)：位置/條件名稱(=C 欄)/傳票命中數/分錄筆數。分錄筆數用後端的 summaryRowCount，
  // 和條件篩選報告摘要同一個口徑（只用「未預期借貸組合」一個條件時是整張傳票的分錄數）。
  function scenarioSummaryTableHtml(columns) {
    var rows = columns.map(function (c) {
      var s = c.summary;
      return (
        '<tr>' +
          '<td>' + Ui.esc(c.label) + '</td>' +
          '<td>' + Ui.esc(s.name) + '</td>' +
          '<td class="preview-table__amount">' + Number(s.voucherHitCount).toLocaleString() + '</td>' +
          '<td class="preview-table__amount">' + Number(s.summaryRowCount).toLocaleString() + '</td>' +
        '</tr>'
      );
    }).join('');

    return (
      '<h4 class="rule-card__title">情境摘要</h4>' +
      '<div class="preview-table__wrap">' +
        '<table class="preview-table">' +
          '<thead><tr><th>欄</th><th>高風險條件（情境名稱）</th><th>符合條件的傳票數量</th><th>符合條件的分錄筆數</th></tr></thead>' +
          '<tbody>' + rows + '</tbody>' +
        '</table>' +
      '</div>'
    );
  }

  // 動態 C 欄的標頭只顯示 C{p}，完整情境名稱放在 title 與輔助技術可讀的隱藏文字；
  // 名稱對照在「符合條件摘要」。十個條件的交叉表因此不必橫向捲動（使用者 2026-10-16 選定）。
  function tagColumnHeadHtml(columns) {
    return columns.map(function (c) {
      return '<th class="preview-table__tag" title="' + Ui.esc(c.name) + '">' + Ui.esc(c.label) +
        '<span class="visually-hidden filter-matrix-name">' + Ui.esc(c.name) + '</span></th>';
    }).join('');
  }

  // 把 matchedPositions(陣列)對映成每個 C 欄的 ✓／空白 column 取值函式(供 appendRowsToTbody)。
  // 純對映:matchedPositions 含該欄 position → '✓',否則空白。不做任何查詢/計算。
  function tagCellColumns(columns) {
    return columns.map(function (c) {
      return { cell: function (row) {
        var hit = (row.matchedPositions || []).indexOf(c.position) >= 0;
        return hit ? '✓' : '';
      }, className: 'preview-table__tag' };
    });
  }

  // 已存情境展開後的傳票清單。s 是 filter.preview 回傳的 scenario 物件，已附上 query.filterVoucherPage 的第一頁。
  // 後端一律回傳傳票清單；原本只在清單是空值時才改列完整命中分錄的那段走不到，使用者 2026-10-19 裁定刪除。
  function renderScenarioHitPreview(previewEl, s) {
    previewEl.innerHTML = Ui.FilterVouchers.summary(s);
    previewEl.setAttribute('data-loaded', '1');
    Ui.FilterVouchers.mount(previewEl.querySelector('.filter-voucher-host'), s);
  }

  // 確保某個展開中的已存情境詳情有命中預覽：優先用 viewState 快取（bump 重繪後不重抓；已存定義只在
  // commit 時變，跨草稿編輯保留快取是安全的），沒有快取才發 filter.preview 並寫入快取。
  // data-loaded 旗標防同一份 DOM 生命週期內重複載入（重複展開/收合不重抓，行為同改版前）。
  function ensureScenarioPreview(container, index) {
    var previewEl = Ui.findLive(container, '[data-bind="scenario-preview-' + index + '"]');
    if (!previewEl || previewEl.getAttribute('data-loaded') === '1') { return; }

    var currentState = Store.getState();
    var committedScope = committedPopulationScope(currentState);
    var populationScope = committedScope || selectedPopulationScope(currentState);
    var hasSavedVersion = !!committedScope;

    var scenarioPosition = index + 1;
    // 沒有 committed revision 時，bounded preview 依目前 draft scope 即時計算，不跨 scope 快取。
    var cached = hasSavedVersion ? FilterStep.viewState.scenarioPreviews[index] : null;
    if (cached) {
      renderScenarioHitPreview(previewEl, cached);
      return;
    }

    var savedRef = currentState.filter.savedScenarios;
    var resultRef = currentState.filterResultRef;
    var projectId = currentState.project ? currentState.project.projectId : null;
    var projectRef = currentState.project, generation = dataGeneration(currentState);
    var scenario = savedRef[index];
    if (!scenario) { return; }
    var acceptResponse = scenarioResponseGuard(index).issue(function () {
      var latest = Store.getState();
      return !!latest.project && latest.project.projectId === projectId &&
        latest.project === projectRef && sameGeneration(dataGeneration(latest), generation) &&
        latest.filter.savedScenarios === savedRef &&
        latest.filterResultRef === resultRef &&
        (hasSavedVersion || selectedPopulationScope(latest) === populationScope);
    });

    previewEl.textContent = '載入中…';
    Ui.runBackground('預覽已儲存情境', function () {
      // savedScenarios 保存時即經 toWireScenario 剝乾淨；此處再過一次同一投影，保證送出形狀一致。
      return global.JetApi.filterPreview({
        populationScope: populationScope,
        scenario: toWireScenario(scenario)
      }).then(function (data) {
        if (!acceptResponse()) { return null; }
        var request = hasSavedVersion ? { scenarioPosition: scenarioPosition, scenarioRevision: resultRef.revision, populationScope: populationScope }
          : { scenario: toWireScenario(scenario), populationScope: populationScope };
        return global.JetApi.queryFilterVoucherPage(Object.assign({}, request, { pageSize: Ui.TABLE_PAGE_SIZE })).then(function (page) {
          data.scenario.voucherPage = page; data.scenario.voucherRequest = request; return data;
        });
      }).then(function (data) {
        // 回應期間已存清單被替換（commit／移除／resume）：index 對位已變，結果作廢，
        // 不寫入剛被 syncViewState 重置的快取（防止舊定義的預覽掛到新清單的同一序位上）。
        if (!acceptResponse()) { return; }
        if (hasSavedVersion) { FilterStep.viewState.scenarioPreviews[index] = data.scenario; }
        fillSmoothly(previewEl, function () { renderScenarioHitPreview(previewEl, data.scenario); });
      }).catch(function (error) {
        // 舊案件／舊 revision 的失敗不可污染目前案件訊息；當前失敗則留可重試提示並交給共用訊息區。
        if (!acceptResponse()) { return; }
        // 會讓已存情境失效的上游修改都會清掉情境（使用者 2026-10-07 裁定），這裡只剩暫時性的讀取失敗，提供重試即可。
        fillSmoothly(previewEl, function () {
          previewEl.innerHTML = '<p role="status">載入失敗：' + Ui.esc(Ui.errorText(error, '請稍後重試。')) + '</p>' +
            '<p>可以直接重試讀取結果。</p>' +
            '<button type="button" class="btn btn--ghost" data-scenario-retry>重試讀取結果</button>';
        });
        previewEl.querySelector('[data-scenario-retry]').addEventListener('click', function () {
          ensureScenarioPreview(container, index);
        });
        throw error;
      });
    });
  }

  // 矩陣展開時讀取情境摘要，再依選中的頁籤呈現摘要或傳票交叉表。
  // 以 matrix-body 的 data-loaded 旗標當快取,重複展開不重抓。零商業邏輯:只發 action、組表、對映 ✓。
  function matrixViewButton(key, label) {
    return '<button type="button" data-matrix-view="' + key + '"' +
      Ui.tabAttributes(FilterStep.viewState.matrixView === key, 'matrix-panel-' + key, 'matrix-tab-' + key) + '>' + label + '</button>';
  }

  function showMatrixView(body) {
    var boxes = ['scenarios', 'vouchers'].map(function (key) { return body.querySelector('[data-bind="matrix-' + key + '"]'); });
    // 換檢視時新的表格原地交叉淡化、高度平順改變；還沒畫過的表格在顯示之後、量高度之前先畫好。
    showOnly(boxes, function (box) { return box.getAttribute('data-bind') === 'matrix-' + FilterStep.viewState.matrixView; }, function () {
      var key = FilterStep.viewState.matrixView;
      var box = body.querySelector('[data-bind="matrix-' + key + '"]');
      if (!box || !body._matrixColumns || box.dataset.rendered) { return; }
      if (key === 'scenarios') { box.innerHTML = scenarioSummaryTableHtml(body._matrixColumns); }
      else if (key === 'vouchers') { renderVoucherMatrix(box, body._matrixColumns); }
      box.dataset.rendered = '1';
    });
    Ui.selectTab(body.querySelector('[data-matrix-view="' + FilterStep.viewState.matrixView + '"]'));
  }

  function bindTagMatrix(container) {
    container.querySelectorAll('[data-matrix-view]').forEach(function (button) {
      button.addEventListener('click', function () {
        FilterStep.viewState.matrixView = button.dataset.matrixView;
        showMatrixView(container.querySelector('[data-bind="matrix-body"]'));
      });
    });
    var toggle = container.querySelector('[data-action="toggle-matrix"]');
    if (!toggle) { return; }

    toggle.addEventListener('click', function () {
      var body = container.querySelector('[data-bind="matrix-body"]');
      if (!body) { return; }

      body.hidden = !body.hidden;
      toggle.setAttribute('aria-expanded', String(!body.hidden));
      FilterStep.viewState.matrixOpen = !body.hidden; // 跨 bump 重繪保留展開狀態
      if (body.hidden) { return; }

      ensureMatrixContent(container);
    });
  }

  // 矩陣內容實體渲染（展開惰性載入與 bump 重繪後的快取還原共用）。scenarios 為 queryTagMatrixScenarios
  // 回傳的摘要陣列；傳票交叉表重新建立時從第一頁開始。
  function renderMatrixContent(body, scenarios) {
    var scenariosBox = body.querySelector('[data-bind="matrix-scenarios"]');
    var vouchersBox = body.querySelector('[data-bind="matrix-vouchers"]');

    var columns = buildTagColumns(scenarios || []);
    // 把摘要掛回欄定義供摘要表顯示名稱與命中數。
    columns.forEach(function (c) {
      c.summary = (scenarios || []).filter(function (s) { return s.position === c.position; })[0];
    });

    if (columns.length === 0) {
      scenariosBox.innerHTML =
        '<p class="empty-state">尚無可用情境，請先在上方儲存篩選情境。</p>';
      return;
    }

    body._matrixColumns = columns;
    showMatrixView(body);
    body.setAttribute('data-loaded', '1');
  }

  // 確保展開中的矩陣有內容：優先用 viewState 摘要快取（草稿編輯不影響已存情境命中，跨重繪保留安全；
  // savedScenarios 替換時由 syncViewState 重置），沒有快取才發 queryTagMatrixScenarios 並寫入。
  function ensureMatrixContent(container) {
    var body = Ui.findLive(container, '[data-bind="matrix-body"]');
    if (!body || body.getAttribute('data-loaded') === '1') { return; }
    if (!committedPopulationScope(Store.getState())) {
      body.innerHTML = '<p class="empty-state">已儲存的篩選情境尚未套用目前規則，矩陣暫不顯示。</p>';
      body.setAttribute('data-loaded', '1');
      return;
    }

    if (FilterStep.viewState.matrixData) {
      renderMatrixContent(body, FilterStep.viewState.matrixData);
      return;
    }

    var scenariosBox = body.querySelector('[data-bind="matrix-scenarios"]');
    scenariosBox.textContent = '載入中…';

    var requestState = Store.getState();
    var savedRef = requestState.filter.savedScenarios;
    var resultRef = requestState.filterResultRef;
    var projectId = requestState.project ? requestState.project.projectId : null;
    var projectRef = requestState.project, generation = dataGeneration(requestState);
    var acceptResponse = matrixResponseGuard.issue(function () {
      var latest = Store.getState();
      return !!latest.project && latest.project.projectId === projectId &&
        latest.project === projectRef && sameGeneration(dataGeneration(latest), generation) &&
        latest.filter.savedScenarios === savedRef && latest.filterResultRef === resultRef;
    });
    Ui.runBackground('載入高風險條件矩陣', function () {
      return global.JetApi.queryTagMatrixScenarios({}).then(function (data) {
        if (!acceptResponse()) { return; }
        FilterStep.viewState.matrixData = data.scenarios || [];
        fillSmoothly(body, function () { renderMatrixContent(body, FilterStep.viewState.matrixData); });
      }).catch(function (error) {
        // 舊案件／舊 revision 的失敗安靜作廢；當前失敗保持未載入狀態，讓使用者可重新展開重試。
        if (!acceptResponse()) { return; }
        scenariosBox.textContent = '載入失敗；請收合後再展開以重試。';
        throw error;
      });
    });
  }

  // 傳票交叉表包含固定傳票欄位與動態 C 欄，每頁最多 200 筆。
  // 矩陣直接由共用分頁載入，欄位及命中判定都來自後端。
  function renderVoucherMatrix(box, columns) {
    box.innerHTML =
      '<h4 class="rule-card__title">傳票與篩選條件對照</h4>' +
      Ui.pageSearchHtml('依傳票號碼查看') +
      '<div class="preview-table__wrap">' +
        '<table class="preview-table">' +
          '<thead><tr>' + Ui.sortableHeadCellsHtml('query.tagMatrixVoucherPage', [
            { key: 'documentNumber', label: '傳票號碼' }, { key: 'postDate', label: '總帳日期' },
            { key: 'createdBy', label: '編製人員' }, { key: 'voucherTotal', label: '傳票總額' }]) +
            tagColumnHeadHtml(columns) + '</tr></thead>' +
          '<tbody></tbody>' +
        '</table>' +
      '</div>';

    var tbody = box.querySelector('.preview-table tbody');
    var fixedCols = [
      function (r) { return r.documentNumber; },
      function (r) { return r.postDate; },
      function (r) { return r.createdBy || ''; },
      { cell: function (r) { return Ui.money(r.voucherTotal); },
        className: 'preview-table__amount' }
    ];
    var allCols = fixedCols.concat(tagCellColumns(columns));

    Ui.bindPagedTable(box, {
      autoLoad: true,
      sourceKind: 'filter',
      fetchPage: function (cursor, sort, search) {
        return global.JetApi.queryTagMatrixVoucherPage({ cursor: cursor, pageSize: 200, sort: sort || null, search: search || null });
      },
      appendRows: function (rows) { Ui.appendRowsToTbody(tbody, rows, allCols); },
      clearRows: function () { tbody.innerHTML = ''; },
      table: box.querySelector('.preview-table'),
      search: box.querySelector('[data-page-search]')
    });
  }

  // 已存情境：編輯、複製、取消編輯、重新檢查、條件篩選報告、展開、移除與矩陣（原本 bind 裡的三段）。
  function bindSavedScenarios(container) {
    function editScenario(index, copy) {
      var state = Store.getState(), saved = state.filter.savedScenarios[index];
      if (!saved) { return; }
      var draft = JSON.parse(JSON.stringify(saved));
      draft.__preserveJoins = true;
      var origins = draft.editorOrigins;
      if (origins) {
        draft.__restoredOrigins = true;
        // 第 2 版的編輯來源逐條記卡片代號，重開時把卡片標記放回條件上。
        // 舊版前端可能在 editorOrigins 留下 presetGroup；2026-10-02 使用者裁定非營業日不再自成一組，不再讀它；
        // 舊的非營業日組重開後就是一般條件組裡的兩條預篩選，計算與連接方式照原條件樹。
        if (origins.version === 2) {
          draft.groups.forEach(function (group, gi) {
            var origin = origins.groups[gi];
            group.rules.forEach(function (rule, ri) { if (origin.cards[ri]) { rule[COMMON_CARD_KEY] = origin.cards[ri]; } });
          });
        }
      }
      // 2026-10-29 以前儲存的情境用第 1 版編輯來源記卡片字母，這一版對應不回卡片：條件照原樣編輯，
      // 畫面說明一次；重新儲存後改存第 2 版，之後就是一般情境（使用者 2026-10-29 選擇程式內部一起改，舊案件另做相容或重建）。
      draft.__legacyCards = !!origins && origins.version !== 2 && (origins.groups || []).some(function (group) {
        return (group.letters || []).some(Boolean);
      });
      setActiveGroup(draft, draft.groups[0] || null);
      // 第 1 版的自動名稱與動機是用當時的卡片字母產生的，這一版對應不回卡片，重算會換成另一段文字；
      // 使用者 2026-10-29 選「拿掉字母」時，選項說明寫明已經儲存的情境名稱不會變，所以舊版情境的名稱與動機一律照原樣保留。
      var keepSavedText = !!origins && origins.version !== 2;
      draft.__nameDirty = keepSavedText || !(origins && origins.nameIsAutomatic === true);
      draft.__rationaleDirty = keepSavedText || !(origins && origins.rationaleIsAutomatic === true);
      if (!copy) { draft.__editingIndex = index; draft.__editingSavedRef = state.filter.savedScenarios; }
      else {
        draft.__nameDirty = true;
        var copyBaseName = draft.name || '篩選情境', copyNumber = 1;
        do { draft.name = copyBaseName + '（副本' + (copyNumber === 1 ? '' : ' ' + copyNumber) + '）'; copyNumber++; }
        while ((Store.getState().filter.savedScenarios || []).some(function (item) { return item.name === draft.name; }));
      }
      // 改去編輯或複製情境時，收起先前按「移除」打開的確認區。
      FilterStep.viewState.workspacePane = 'filter'; FilterStep.viewState.lastSavedName = null; FilterStep.viewState.savedDraftKey = null;
      FilterStep.viewState.pendingRemovalIndex = null;
      FilterStep.selectedRule = null;
      Store.setFilterDraft(draft);
      if (global.JetFocus) { global.JetFocus.defer(function () { return document.querySelector('.filter-workspace [data-builder-title]'); }); }
    }
    container.querySelectorAll('[data-action="edit-scenario"], [data-action="copy-scenario"]').forEach(function (button) {
      button.addEventListener('click', function () {
        var index = Number(button.dataset.index), copy = button.dataset.action === 'copy-scenario';
        var target = Store.getState().filter.savedScenarios[index];
        if (!target) { return; }
        guardDraftReplacement(copy ? 'copy' : 'edit', target.name, function () { editScenario(index, copy); });
      });
    });
    var cancelEdit = container.querySelector('[data-action="cancel-edit-scenario"]');
    if (cancelEdit) { cancelEdit.addEventListener('click', function () {
      FilterStep.viewState.lastSavedName = null; FilterStep.viewState.disclosures = {}; FilterStep.viewState.savedDraftKey = null;
      FilterStep.selectedRule = null;
      announceClearedComposition();
      Store.setFilterDraft({ name: '', rationale: '', groups: [] });
    }); }
    var resaveButton = container.querySelector('[data-action="resave-scenarios"]');
    if (resaveButton) {
      resaveButton.addEventListener('click', function () {
        Ui.run('重新檢查並儲存情境', function () {
          var state = Store.getState();
          if (state.filter.savedScenarios.length === 0) { return Promise.resolve(); }
          var snapshot = filterCommitSnapshot(state);
          // 不送清單：後端從資料庫讀出全部情境，能套用目前規則的就更新，其餘列在回應的待修正清單，不整批失敗。
          return global.JetApi.filterScenarioRecheck({
            populationScope: selectedPopulationScope(state)
          }).then(function (data) {
            if (!completeScenarioListChange(snapshot, data)) { return; }
            // 完成句由 Ui.run 接上耗時後記一則（使用者 2026-10-13 裁定成功時只留一則）。
            return scenarioRecheckSentence(data);
          });
        }, { completionMessage: function (sentence) { return sentence || ''; } });
      });
    }

    var criteriaBtn = container.querySelector('[data-action="export-criteria-report"]');
    if (criteriaBtn) {
      criteriaBtn.addEventListener('click', function () {
        var state = Store.getState();
        var resultRef = state.filterResultRef;
        var validationRunId = state.lastRuns.validate && state.lastRuns.validate.resultRef
          ? state.lastRuns.validate.resultRef.runId : null;
        if (!committedPopulationScope(state) || !resultRef || !resultRef.revision ||
            !validationRunId) { return; }
        Ui.run('產生條件篩選報告', function () {
          return global.JetApi.exportCriteriaSelectionReport({
            validationRunId: validationRunId,
            revision: resultRef.revision
          })
            .then(function (data) {
              Store.applyReportExport(data);
              return data;
            });
        }, { completionMessage: function (data) { return data ? '已在案件資料夾產生條件篩選報告。' : ''; } });
      });
    }

    container.querySelectorAll('[data-action="toggle-scenario"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var index = Number(btn.getAttribute('data-index'));
        var body = container.querySelector('[data-bind="scenario-body-' + index + '"]');
        if (!body) { return; }

        body.hidden = !body.hidden;
        btn.setAttribute('aria-expanded', String(!body.hidden));
        var article = btn.closest('.saved-scenario');
        if (article) { article.classList.toggle('saved-scenario--open', !body.hidden); }
        FilterStep.viewState.openScenarios[index] = !body.hidden; // 跨 bump 重繪保留展開狀態
        if (body.hidden) { return; }

        // 首次展開時惰性載入該情境的命中預覽（重用既有 filter.preview，顯示前 10 筆）；
        // 快取與還原邏輯收斂在 ensureScenarioPreview。
        ensureScenarioPreview(container, index);
      });
    });

    // 確認區直接在這一步裡重繪；經過 JetMotion，確認區長出來與收起來。
    function redrawStep() {
      if (global.JetMotion) { global.JetMotion.morph(container, function () { render(container, Store.getState()); }); }
      else { render(container, Store.getState()); }
    }

    container.querySelectorAll('[data-action="remove-scenario"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var index = Number(btn.getAttribute('data-index'));
        FilterStep.viewState.pendingRemovalIndex = index;
        redrawStep();
        if (global.JetFocus) {
          global.JetFocus.defer(function () {
            return container.querySelector(
              '[data-action="cancel-remove-scenario"][data-index="' + index + '"]');
          });
        }
      });
    });

    container.querySelectorAll('[data-action="cancel-remove-scenario"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var index = Number(btn.getAttribute('data-index'));
        FilterStep.viewState.pendingRemovalIndex = null;
        redrawStep();
        restoreScenarioRemovalFocus(container, index);
      });
    });

    container.querySelectorAll('[data-action="confirm-remove-scenario"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var index = Number(btn.getAttribute('data-index'));
        var removalSucceeded = false;
        Ui.run('移除篩選情境', function () {
          var removeState = Store.getState();
          var target = removeState.filter.savedScenarios[index];
          if (!target) { return Promise.resolve(); }
          var snapshot = filterCommitSnapshot(removeState);
          // 只送要移除的那一個；其他情境由後端照資料庫原樣保留，不重新檢查。
          return global.JetApi.filterScenarioRemove({
            populationScope: selectedPopulationScope(removeState),
            position: index + 1,
            expectedName: target.name
          }).then(function (data) {
            if (!completeScenarioListChange(snapshot, data, index)) { return; }
            Store.addMessage('已移除情境（剩餘 ' + data.savedCount + ' / 10）。', 'info');
            removalSucceeded = true;
          });
        }, {
          onError: function () {
            if (global.JetFocus) {
              global.JetFocus.defer(function () {
                return container.querySelector(
                  '[data-action="confirm-remove-scenario"][data-index="' + index + '"]');
              });
            }
          }
        }).then(function () {
          if (removalSucceeded) { restoreScenarioRemovalFocus(container, index); }
        });
      });
    });

    bindTagMatrix(container);
  }

  // 掛到共用的 FilterStep，供其他檔案與測試取用。
  FilterStep.scenarioContentKey = scenarioContentKey;
  FilterStep.savedFeedbackText = savedFeedbackText;
  FilterStep.draftContentKey = draftContentKey;
  FilterStep.draftIsBlank = draftIsBlank;
  FilterStep.editedSavedScenario = editedSavedScenario;
  FilterStep.hasUnsavedScenarioChanges = hasUnsavedScenarioChanges;
  FilterStep.replacementWording = replacementWording;
  FilterStep.guardDraftReplacement = guardDraftReplacement;
  FilterStep.draftMetadataKey = draftMetadataKey;
  FilterStep.belongsToTarget = belongsToTarget;
  FilterStep.saveFailureText = saveFailureText;
  FilterStep.scenarioRecheckSentence = scenarioRecheckSentence;
  FilterStep.filterCommitSnapshot = filterCommitSnapshot;
  FilterStep.acceptsFilterCommit = acceptsFilterCommit;
  FilterStep.completeScenarioListChange = completeScenarioListChange;
  FilterStep.completeScenarioSave = completeScenarioSave;
  FilterStep.scenarioPillsHtml = scenarioPillsHtml;
  FilterStep.savedScenariosHtml = savedScenariosHtml;
  FilterStep.criteriaReportHtml = criteriaReportHtml;
  FilterStep.tagMatrixHtml = tagMatrixHtml;
  FilterStep.buildTagColumns = buildTagColumns;
  FilterStep.scenarioSummaryTableHtml = scenarioSummaryTableHtml;
  FilterStep.tagColumnHeadHtml = tagColumnHeadHtml;
  FilterStep.tagCellColumns = tagCellColumns;
  FilterStep.renderScenarioHitPreview = renderScenarioHitPreview;
  FilterStep.ensureScenarioPreview = ensureScenarioPreview;
  FilterStep.matrixViewButton = matrixViewButton;
  FilterStep.showMatrixView = showMatrixView;
  FilterStep.bindTagMatrix = bindTagMatrix;
  FilterStep.renderMatrixContent = renderMatrixContent;
  FilterStep.ensureMatrixContent = ensureMatrixContent;
  FilterStep.renderVoucherMatrix = renderVoucherMatrix;
  FilterStep.bindSavedScenarios = bindSavedScenarios;
})(window);
