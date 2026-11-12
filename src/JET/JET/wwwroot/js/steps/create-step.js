/*
  Step 0：建立案件。
  渲染 metadata 表單與已建立摘要。
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;
  var Ui = global.JetUi;
  var metadataDraft = null;

  Ui.registerWorkflowReset(function () { metadataDraft = null; });

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

  // databaseProvider 顯示名。目前只建立 SQLite 案件，建案表單不讓使用者選擇。
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
    var summary = form.querySelector('[data-bind="' + (updating ? 'project-update-error' : 'create-error') + '"]');
    // 只收起不清空：錯誤訊息要帶著原文字收合；下次顯示時 showCreateError 會先寫入新訊息。
    if (summary) { summary.hidden = true; }
    var invalidFields = form.querySelectorAll('[aria-invalid="true"]');
    Array.prototype.forEach.call(invalidFields, function (field) {
      field.removeAttribute('aria-invalid');
      field.removeAttribute('aria-describedby');
    });
  }

  function showCreateError(form, error, updating) {
    var summary = form.querySelector('[data-bind="' + (updating ? 'project-update-error' : 'create-error') + '"]');
    if (!summary) { return; }
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
      draft.values = readValues(); draft.error = null;
      clearCreateError(form, true);
      var payload = { clientName: draft.values.clientName.trim(), projectCode: draft.values.projectCode.trim(),
        prepStartDate: draft.values.prepStartDate || null };
      Ui.run('儲存案件資料', function () {
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
        if (!isCurrent()) { return; }
        draft.error = error;
        showCreateError(form, error, true);
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

    container.innerHTML =
      '<div class="panel" data-motion-key="create-form-panel">' +
        '<h2 class="panel__title">建立案件</h2>' +
        '<form class="form" data-bind="create-form">' +
          formRow('projectName', '案件名稱', 'text', true) +
          formRow('projectCode', '案件編號', 'text', false) +
          formRow('clientName', '客戶名稱', 'text', false) +
          operatorNoticeHtml(state) +
          formRow('periodStart', '查核起始日', 'date', true) +
          formRow('periodEnd', '查核截止日', 'date', true) +
          formRow('prepStartDate', '期末財報準備日', 'date', false) +
          preparationWarningHtml() +
          '<p class="rule-card__sub">查核期間決定本次分析的總帳範圍；期末財報準備日供「財報準備日起核准」條件使用，不會延長查核期間。</p>' +
          '<div id="create-form-error" class="form-notice create-form__error" data-bind="create-error" data-collapsible role="alert" tabindex="-1" hidden></div>' +
          '<div class="panel__actions">' +
            '<button type="submit" class="btn">建立案件</button>' +
          '</div>' +
        '</form>' +
        Ui.stepFooterHtml(state) +
      '</div>';

    Ui.bindStepFooter(container);

    container.querySelector('[data-bind="create-form"]').addEventListener('submit', function (event) {
      event.preventDefault();

      var form = event.target;
      clearCreateError(form);
      var payload = {
        projectName: form.projectName.value.trim(),
        projectCode: form.projectCode.value.trim(),
        clientName: form.clientName.value.trim(),
        periodStart: form.periodStart.value,
        periodEnd: form.periodEnd.value,
        prepStartDate: form.prepStartDate.value || null
      };
      if (!payload.projectName) {
        showCreateError(form, { field: 'projectName', message: '案件名稱不可為空白，請輸入可辨識這個案件的名稱。' });
        return;
      }
      if (payload.periodStart && payload.periodEnd && payload.periodStart > payload.periodEnd) {
        showCreateError(form, { field: 'periodStart', message: '查核起始日不能晚於查核截止日，請調整日期後再建立案件。' });
        return;
      }

      Ui.run('建立案件', function () {
        return global.JetApi.projectCreate(payload).then(function (data) {
          Store.addMessage('案件「' + data.projectId + '」已建立。', 'info');
          (data.warnings || []).forEach(function (warning) { Store.addMessage(warning, 'warn'); });
          return global.JetApi.projectLoad({ projectId: data.projectId });
        }).then(function (loaded) {
          Ui.applyLoadedProject(loaded);
        });
      }, {
        // 原因寫在表單上方的錯誤框，不另外在步驟上方顯示。
        inlineError: true,
        onError: function (_, error) { showCreateError(form, error); }
      });
    });

    container.querySelector('[data-bind="create-form"]').addEventListener('input', function (event) {
      if (event.target && event.target.matches('input, select')) {
        clearCreateError(event.currentTarget);
        updatePreparationWarning(event.currentTarget, null);
      }
    });
  }

  Ui.registerStep('create', render);
})(window);
