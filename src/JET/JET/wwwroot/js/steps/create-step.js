/*
  第一步：建立案件。
  渲染 metadata 表單與已建立摘要。
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;
  var Ui = global.JetUi;
  var metadataDraft = null;

  // 建立案件表單填到一半的內容與表單上的錯誤：{ values, error, createdProjectId }。中央面板重畫時表單整個換新，
  // 照這裡把已填的值與錯誤畫回去。createdProjectId 是這張表單已經建立、還沒開啟的案件，還沒建立時是 null。
  // 回首頁或開了案件就清掉，下一次「新增案件」是空白表單。
  var createDraft = null;
  var CREATE_FIELDS = ['projectName', 'projectCode', 'clientName', 'periodStart', 'periodEnd', 'prepStartDate'];

  // 兩張表單現在畫在畫面上的那一個元素。後端的回應回來時，錯誤標在這裡記的表單上，不標在送出當時的那一個：
  // 等待回應的期間中央面板可能已經重畫，原本那一個不在畫面上了。
  var shownForms = { create: null, update: null };

  Ui.registerWorkflowReset(function () { metadataDraft = null; createDraft = null; });

  function freshCreateDraft() {
    var values = {};
    CREATE_FIELDS.forEach(function (name) { values[name] = ''; });
    return { values: values, error: null, createdProjectId: null };
  }

  function readCreateValues(form) {
    var values = {};
    CREATE_FIELDS.forEach(function (name) { values[name] = form.elements[name].value; });
    return values;
  }

  function formRow(name, label, type, required, value) {
    return (
      '<label class="form__row">' +
        '<span class="form__label">' + label + (required ? ' <em class="form__req">*</em>' : '') + '</span>' +
        (type === 'date' ? Ui.dateInputHtml({ name: name, required: required, value: value }) :
          '<input class="form__input" type="' + type + '" name="' + name + '"' + (required ? ' required' : '') +
          (value == null ? '' : ' value="' + Ui.esc(value) + '"') + '>') +
      '</label>'
    );
  }

  // databaseProvider 顯示名。JET 只建立 SQLite 案件，建案表單不讓使用者選擇。
  function providerLabel() {
    return 'SQLite（本機檔案）';
  }

  function kv(label, value) {
    return (
      '<div class="kv-list__row">' +
        '<dt class="kv-list__key">' + Ui.esc(label) + '</dt>' +
        '<dd class="kv-list__value">' + Ui.esc(value || '—') + '</dd>' +
      '</div>'
    );
  }

  function operatorNoticeHtml(state) {
    var user = state.currentUser;
    var name = user && user.shortName ? user.shortName : '目前 Windows 帳號';
    return '<p class="form-notice form-notice--info create-form__operator" data-bind="create-operator"><span>目前帳號：<strong>' + Ui.esc(name) +
      '</strong>。建立案件時由系統取得，不能自行更改。</span></p>';
  }

  // whoAmI 可能晚於使用者開始填表；只更新身分這一行，不重建未儲存的案件表單。
  Ui.refreshCreateIdentity = function (container, state) {
    if (state.project) { return; }
    var notice = Ui.findLive(container, '[data-bind="create-operator"]');
    if (notice) { notice.outerHTML = operatorNoticeHtml(state); }
  };

  function clearCreateError(form, updating) {
    if (!form) { return; }
    var summary = form.querySelector('[data-bind="' + (updating ? 'project-update-error' : 'create-error') + '"]');
    // 只收起不清空：錯誤訊息要帶著原文字收合；下次顯示時 showCreateError 會先寫入新訊息。
    if (summary) { summary.hidden = true; }
    var invalidFields = form.querySelectorAll('[aria-invalid="true"]');
    Array.prototype.forEach.call(invalidFields, function (field) {
      field.removeAttribute('aria-invalid');
      field.removeAttribute('aria-describedby');
    });
  }

  // 把錯誤畫到表單上：原因寫進錯誤框，後端指明的欄位標成有誤。回傳該拿焦點的元素（那一欄，沒有指明欄位時是錯誤框）。
  // 重畫之後照記下的錯誤再畫一次時只呼叫這一個，不移動焦點。
  function markCreateError(form, error, updating) {
    var summary = form ? form.querySelector('[data-bind="' + (updating ? 'project-update-error' : 'create-error') + '"]') : null;
    if (!summary) { return null; }
    var message = Ui.errorText(error, updating ? '無法儲存案件資料。' : '無法建立案件。');
    summary.textContent = message;
    summary.hidden = false;

    // 欄位只依結構化 field 標記，不從錯誤代碼或自由文字猜測。
    var allowed = ['projectName', 'periodStart', 'periodEnd', 'prepStartDate', 'clientName', 'projectCode'];
    var target = error && allowed.indexOf(error.field) >= 0 && form.elements[error.field]
      ? form.elements[error.field] : summary;
    if (target !== summary) {
      target.setAttribute('aria-invalid', 'true');
      target.setAttribute('aria-describedby', updating ? 'project-update-error' : 'create-form-error');
    }
    return target;
  }

  // 錯誤剛發生時：畫到表單上，並把焦點移到有誤的欄位或錯誤框。
  function showCreateError(form, error, updating) {
    var target = markCreateError(form, error, updating);
    if (!target) { return; }

    if (global.JetFocus) {
      global.JetFocus.defer(target);
    } else {
      global.setTimeout(function () { target.focus(); }, 0);
    }
  }

  // 使用年月日做比較，不把不含時區的案件日期換算成使用者電腦的時區。
  // 一年後若沒有同一天（2 月 29 日），和後端 AddYears 一樣取該月最後一天。
  function oneYearAfter(date) {
    var parts = date.split('-').map(Number);
    var year = parts[0] + 1;
    var leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
    var day = parts[1] === 2 && parts[2] === 29 && !leap ? 28 : parts[2];
    return String(year).padStart(4, '0') + '-' + String(parts[1]).padStart(2, '0') + '-' + String(day).padStart(2, '0');
  }

  function updatePreparationWarning(form, project) {
    var warning = form.querySelector('[data-bind="preparation-date-warning"]');
    if (!warning) { return; }
    var value = form.elements.prepStartDate.value;
    var start = project ? project.periodStart : form.elements.periodStart.value;
    var end = project ? project.periodEnd : form.elements.periodEnd.value;
    var validDate = /^\d{4}-\d{2}-\d{2}$/;
    var unusual = validDate.test(value) && ((validDate.test(start) && value < start) ||
      (validDate.test(end) && value > oneYearAfter(end)));
    // 早於查核起始日，或晚於查核截止日超過一年時提醒；只顯示狀態，不在提示裡解釋判斷規則。
    // 句子和後端 ProjectMetadataRules.PreparationDateWarning 一字不差，儲存後回傳的提醒才不會換一種說法。
    // 顯示時先寫文字再展開；收起時保留原文字，讓提示帶著內容收合。
    if (unusual) { warning.textContent = '期末財報準備日與查核期間不相符，請確認年份；不影響後續操作。'; }
    warning.hidden = !unusual;
  }

  // 查核期間建案後不能修改，年份明顯打錯時在送出前指出是哪一欄。
  // 兩句和後端 ProjectMetadataRules 的 PeriodStartYearOutOfRange、PeriodEndYearOutOfRange 一字不差。
  var periodYearMessages = {
    periodStart: '查核起始日的年份須在 1900 到 2100 之間。請修正年份後再建立案件。',
    periodEnd: '查核截止日的年份須在 1900 到 2100 之間。請修正年份後再建立案件。'
  };

  // 回傳第一個年份不在 1900 到 2100 的欄位名稱，都合格時回傳 null。年份多於四位數也算不合格；
  // 空白不在這裡判斷，由必填檢查處理。
  function periodYearProblemField(payload) {
    var fields = ['periodStart', 'periodEnd'];
    for (var i = 0; i < fields.length; i++) {
      var value = payload[fields[i]];
      if (!value) { continue; }
      var match = /^(\d{4})-\d{2}-\d{2}$/.exec(value);
      if (!match || Number(match[1]) < 1900 || Number(match[1]) > 2100) { return fields[i]; }
    }
    return null;
  }

  function preparationWarningHtml() {
    return '<p class="form-notice form-notice--warning" data-bind="preparation-date-warning" data-collapsible role="status" hidden></p>';
  }

  function metadataEditorHtml() {
    if (!metadataDraft) {
      return '<div class="panel__actions" data-motion-key="metadata-actions"><button type="button" class="btn btn--ghost" ' +
        'data-action="edit-project-metadata">修改案件資料</button></div>';
    }
    var values = metadataDraft.values;
    return '<form class="form" data-bind="project-update-form" data-motion-key="metadata-form">' +
      '<h3>修改案件資料</h3>' +
      // 這裡不寫「已匯出的報告與工作底稿不會改寫」這類保證句，免得常駐說明太多。
      '<p>案件名稱與查核期間不能修改。</p>' +
      formRow('clientName', '客戶名稱', 'text', false, values.clientName) +
      formRow('projectCode', '案件編號', 'text', false, values.projectCode) +
      formRow('prepStartDate', '期末財報準備日', 'date', false, values.prepStartDate) +
      '<p class="rule-card__sub">修改期末財報準備日後，預篩選需要重新計算。</p>' +
      Ui.downstreamResetNoticeHtml(Store.getState()) +
      preparationWarningHtml() +
      '<div id="project-update-error" class="form-notice" data-bind="project-update-error" data-collapsible role="alert" tabindex="-1" hidden></div>' +
      '<div class="panel__actions"><button type="button" class="btn btn--ghost" data-action="cancel-project-metadata">取消</button>' +
      '<button type="submit" class="btn">儲存案件資料</button></div></form>';
  }

  // 按鈕和表單互換，按下的那一個跟著不見：打開表單時焦點交給第一個欄位，收起時交回「修改案件資料」；
  // 不這樣做的話焦點會落到整個頁面。
  function focusMetadata(selector) {
    if (global.JetFocus && global.JetFocus.deferFirst) { global.JetFocus.deferFirst([selector]); }
  }

  function bindMetadataEditor(container, project) {
    var edit = container.querySelector('[data-action="edit-project-metadata"]');
    if (edit) { edit.addEventListener('click', function () {
      metadataDraft = { project: project, error: null, values: {
        clientName: project.clientName || '', projectCode: project.projectCode || '', prepStartDate: project.prepStartDate || ''
      } };
      Store.touch();
      focusMetadata('[data-bind="project-update-form"] [name="clientName"]');
    }); }
    var cancel = container.querySelector('[data-action="cancel-project-metadata"]');
    if (cancel) { cancel.addEventListener('click', function () {
      metadataDraft = null;
      Store.touch();
      focusMetadata('[data-action="edit-project-metadata"]');
    }); }
    var form = container.querySelector('[data-bind="project-update-form"]');
    shownForms.update = form;
    if (!form) { return; }
    var draft = metadataDraft;
    function isCurrent() { return Store.getState().project === project && metadataDraft === draft; }
    function readValues() {
      return { clientName: form.elements.clientName.value, projectCode: form.elements.projectCode.value,
        prepStartDate: form.elements.prepStartDate.value };
    }
    updatePreparationWarning(form, project);
    if (draft.error) { showCreateError(form, draft.error, true); }
    form.addEventListener('input', function () {
      if (!isCurrent()) { return; }
      draft.values = readValues(); draft.error = null;
      clearCreateError(form, true); updatePreparationWarning(form, project);
    });
    form.addEventListener('submit', function (event) {
      event.preventDefault();
      if (!isCurrent()) { return; }
      draft.values = readValues();
      var payload = { clientName: draft.values.clientName.trim(), projectCode: draft.values.projectCode.trim(),
        prepStartDate: draft.values.prepStartDate || null };
      Ui.run('儲存案件資料', function () {
        // 真的開始儲存時才收起上一次的錯誤；另一項作業還在進行時它照樣留著。
        draft.error = null;
        clearCreateError(shownForms.update, true);
        return global.JetApi.projectUpdate(payload).then(function (data) {
          if (!isCurrent()) { return; }
          metadataDraft = null;
          Store.updateProjectMetadata(data);
          focusMetadata('[data-action="edit-project-metadata"]');
          Store.addMessage('案件資料已更新。', 'info');
          (data.warnings || []).forEach(function (warning) { Store.addMessage(warning, 'warn'); });
        }).catch(function (error) {
          if (isCurrent()) { throw error; }
        });
      }, { inlineError: true, onError: function (_, error) {
        if (!isCurrent() || Ui.isCancelledOrInProgress(error)) { return; }
        draft.error = error;
        showCreateError(shownForms.update, error, true);
      } });
    });
  }

  function voucherNumberAcrossGlDatesHtml(state) {
    var summary = state.results.validate && state.results.validate.voucherNumberAcrossGlDates;
    var known = !(state.staleState && state.staleState.validation) && summary &&
      Number.isInteger(summary.voucherNumberCount) && summary.voucherNumberCount >= 0 &&
      Number.isInteger(summary.entryCount) && summary.entryCount >= 0;
    var text;
    // 尚未確認與未發現只是說明，用中性樣式；有發現時需要注意但不擋人，用注意色。紅色只留給錯誤。
    var variant = 'form-notice--info';
    if (!known) {
      text = '傳票號碼是否用於不同總帳日期尚未確認。請在第四步執行資料驗證後查看；仍可繼續目前作業。';
    } else if (summary.voucherNumberCount === 0) {
      text = '目前 GL 中未發現同一傳票號碼出現在不同總帳日期。';
    } else {
      variant = 'form-notice--warning';
      text = 'GL 有 ' + Ui.countText(summary.voucherNumberCount) + ' 個傳票號碼出現在不同總帳日期（共 ' +
        Ui.countText(summary.entryCount) + ' 筆分錄），請確認來源系統是否重複使用號碼。';
    }
    return '<p class="form-notice ' + variant + '" data-bind="document-date-reuse" role="status">' + Ui.esc(text) + '</p>';
  }

  function render(container, state) {
    if (metadataDraft && metadataDraft.project !== state.project) { metadataDraft = null; }
    if (state.project) {
      // 已經開了案件：建立表單不在畫面上，填到一半的內容不再需要。
      createDraft = null;
      shownForms.create = null;
      container.innerHTML =
        '<div class="panel" data-motion-key="create-summary-panel">' +
          '<h2 class="panel__title">建立案件</h2>' +
          '<dl class="kv-list">' +
            kv('案件名稱', state.project.projectId) +
            kv('案件編號', state.project.projectCode) +
            kv('客戶名稱', state.project.clientName) +
            kv('建案者', state.project.operatorId) +
            kv('查核期間', (state.project.periodStart || '—') + ' ～ ' + (state.project.periodEnd || '—')) +
            kv('期末財報準備日', state.project.prepStartDate || '—') +
            kv('資料儲存方式', providerLabel()) +
          '</dl>' +
          voucherNumberAcrossGlDatesHtml(state) +
          metadataEditorHtml() +
          Ui.stepFooterHtml(state) +
        '</div>';

      Ui.bindStepFooter(container);
      bindMetadataEditor(container, state.project);
      return;
    }

    shownForms.update = null;
    if (!createDraft) { createDraft = freshCreateDraft(); }
    var draft = createDraft;
    // 還沒填的欄位不寫 value 屬性，和空白表單畫出來的一樣。
    function filled(name) { return draft.values[name] || null; }
    container.innerHTML =
      '<div class="panel" data-motion-key="create-form-panel">' +
        '<h2 class="panel__title">建立案件</h2>' +
        '<form class="form" data-bind="create-form">' +
          formRow('projectName', '案件名稱', 'text', true, filled('projectName')) +
          formRow('projectCode', '案件編號', 'text', false, filled('projectCode')) +
          formRow('clientName', '客戶名稱', 'text', false, filled('clientName')) +
          operatorNoticeHtml(state) +
          formRow('periodStart', '查核起始日', 'date', true, filled('periodStart')) +
          formRow('periodEnd', '查核截止日', 'date', true, filled('periodEnd')) +
          formRow('prepStartDate', '期末財報準備日', 'date', false, filled('prepStartDate')) +
          preparationWarningHtml() +
          '<p class="rule-card__sub">查核期間決定哪些分錄納入測試。案件名稱與查核期間建立後不能修改，填錯時要另外建立新案件。期末財報準備日用於「財報準備日起核准」條件，可以先留空，建立後再按「修改案件資料」補填。</p>' +
          '<div id="create-form-error" class="form-notice create-form__error" data-bind="create-error" data-collapsible role="alert" tabindex="-1" hidden></div>' +
          '<div class="panel__actions">' +
            '<button type="submit" class="btn">建立案件</button>' +
          '</div>' +
        '</form>' +
        Ui.stepFooterHtml(state) +
      '</div>';

    Ui.bindStepFooter(container);

    var form = container.querySelector('[data-bind="create-form"]');
    shownForms.create = form;
    // 重畫之後照記下的內容畫回去：準備日的提醒依已填的日期重算，錯誤照原樣標上，不移動焦點。
    updatePreparationWarning(form, null);
    if (draft.error) { markCreateError(form, draft.error); }

    form.addEventListener('submit', function (event) {
      event.preventDefault();

      // 這張表單的案件已經建立、只是沒有開啟，而且當時後端也放不掉這個案件，畫面才會還在這裡：再按一次只開啟它，不再建立一次。
      if (draft.createdProjectId) {
        Ui.openCreatedProject(draft.createdProjectId);
        return;
      }

      draft.values = readCreateValues(form);
      var payload = {
        projectName: draft.values.projectName.trim(),
        projectCode: draft.values.projectCode.trim(),
        clientName: draft.values.clientName.trim(),
        periodStart: draft.values.periodStart,
        periodEnd: draft.values.periodEnd,
        prepStartDate: draft.values.prepStartDate || null
      };
      // 送出前就看得出的問題：換掉表單上原本的錯誤，不送出。
      function showProblem(problem) {
        draft.error = problem;
        clearCreateError(form);
        showCreateError(form, problem);
      }
      if (!payload.projectName) {
        showProblem({ field: 'projectName', message: '案件名稱不可為空白，請輸入可辨識這個案件的名稱。' });
        return;
      }
      // 年份先看：年份多於四位數的日期做字串比較會排錯順序，不能落到下面的起訖比較。
      var yearProblemField = periodYearProblemField(payload);
      if (yearProblemField) {
        showProblem({ field: yearProblemField, message: periodYearMessages[yearProblemField] });
        return;
      }
      if (payload.periodStart && payload.periodEnd && payload.periodStart > payload.periodEnd) {
        showProblem({ field: 'periodStart', message: '查核起始日不能晚於查核截止日，請調整日期後再建立案件。' });
        return;
      }

      Ui.run('建立案件', function () {
        // 真的開始建立時才收起上一次的錯誤；另一項作業還在進行時它照樣留著。
        draft.error = null;
        clearCreateError(shownForms.create);
        return global.JetApi.projectCreate(payload).then(function (data) {
          draft.createdProjectId = data.projectId;
          Store.addMessage('案件「' + data.projectId + '」已建立。', 'info');
          (data.warnings || []).forEach(function (warning) { Store.addMessage(warning, 'warn'); });
        });
      }, {
        // 原因寫在表單上方的錯誤框，不另外在步驟上方顯示。
        inlineError: true,
        onError: function (_, error) {
          if (Ui.isCancelledOrInProgress(error)) { return; }
          draft.error = error;
          showCreateError(shownForms.create, error);
        }
      }).then(function () {
        // 建立與開啟是兩項作業：建立成功才開啟。開啟沒有成功時案件已經存在，由開啟那一邊帶回案件清單，不寫成建立失敗。
        if (draft.createdProjectId) { return Ui.openCreatedProject(draft.createdProjectId); }
      });
    });

    form.addEventListener('input', function (event) {
      if (event.target && event.target.matches('input, select')) {
        // 每次輸入都記下整張表單的值；重新輸入後上一次的錯誤不再適用。
        draft.values = readCreateValues(form);
        draft.error = null;
        clearCreateError(form);
        updatePreparationWarning(form, null);
      }
    });
  }

  Ui.registerStep('create', render);
})(window);
