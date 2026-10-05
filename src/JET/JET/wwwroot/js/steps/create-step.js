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
        '<input class="form__input" type="' + type + '" name="' + name + '"' + (required ? ' required' : '') +
          (value == null ? '' : ' value="' + Ui.esc(value) + '"') + '>' +
      '</label>'
    );
  }

  function formSelect(name, label, options, hint) {
    var opts = options.map(function (option) {
      return (
        '<option value="' + option.value + '"' + (option.selected ? ' selected' : '') +
          (option.disabled ? ' disabled' : '') + '>' +
          Ui.esc(option.text) +
        '</option>'
      );
    }).join('');
    return (
      '<label class="form__row">' +
        '<span class="form__label">' + label + '</span>' +
        '<select class="form__input" name="' + name + '">' + opts + '</select>' +
      '</label>' +
      '<p class="rule-card__sub" data-bind="' + name + '-hint" role="status"' + (hint ? '' : ' hidden') + '>' +
        Ui.esc(hint || '') + '</p>'
    );
  }

  var SQL_SERVER_NOT_CONFIGURED_HINT = '這台電腦沒有設定線上資料庫，請改選 SQLite 或 DuckDB。';

  // 只有 system.databaseInfo 明確回報沒有設定（false）才停用 SQL Server；還沒查到（null）或
  // 已設定但連不上（true）都維持可選，連不上時照舊在建立案件時回報錯誤。
  function sqlServerUnavailable(state) {
    return state.sqlServerConfigured === false;
  }

  // system.databaseInfo 可能在輸入案件名稱後才回來；只改選項可用性與提示。
  // 已選的資料庫不代替使用者切換，表單、焦點與尚未儲存的內容都留在原節點。
  Ui.refreshCreateDatabaseAvailability = function (container, state) {
    if (state.project) { return; }
    var unavailable = sqlServerUnavailable(state);
    var option = container.querySelector('[data-bind="create-form"] option[value="sqlServer"]');
    var hint = container.querySelector('[data-bind="create-form"] [data-bind="databaseProvider-hint"]');
    if (option) { option.disabled = unavailable; }
    if (hint) {
      hint.textContent = unavailable ? SQL_SERVER_NOT_CONFIGURED_HINT : '';
      hint.hidden = !unavailable;
    }
  };

  // databaseProvider 顯示名（與後端 ProjectDocument 的值對應）。
  function providerLabel(value) {
    if (value === 'sqlServer') { return 'SQL Server（共用實例）'; }
    if (value === 'duckdb') { return 'DuckDB（本機分析資料庫）'; }
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
    return '<p class="form-notice create-form__operator" data-bind="create-operator">目前帳號：<strong>' + Ui.esc(name) +
      '</strong>。建立案件時由系統取得，不能自行更改。</p>';
  }

  // whoAmI 可能晚於使用者開始填表；只更新身分這一行，不重建未儲存的案件表單。
  Ui.refreshCreateIdentity = function (container, state) {
    if (state.project) { return; }
    var notice = container.querySelector('[data-bind="create-operator"]');
    if (notice) { notice.outerHTML = operatorNoticeHtml(state); }
  };

  function clearCreateError(form, updating) {
    var summary = form.querySelector('[data-bind="' + (updating ? 'project-update-error' : 'create-error') + '"]');
    if (summary) {
      summary.hidden = true;
      summary.textContent = '';
    }
    var invalidFields = form.querySelectorAll('[aria-invalid="true"]');
    Array.prototype.forEach.call(invalidFields, function (field) {
      field.removeAttribute('aria-invalid');
      field.removeAttribute('aria-describedby');
    });
  }

  function showCreateError(form, error, updating) {
    var summary = form.querySelector('[data-bind="' + (updating ? 'project-update-error' : 'create-error') + '"]');
    if (!summary) { return; }
    var message = error && error.message ? error.message : (updating ? '無法儲存案件資料。' : '無法建立案件。');
    summary.textContent = message;
    summary.hidden = false;

    // 欄位只依結構化 field 標記，不從錯誤代碼或自由文字猜測。
    var allowed = ['caseName', 'periodStart', 'periodEnd', 'lastPeriodStart', 'entityName', 'projectCode'];
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
    var value = form.elements.lastPeriodStart.value;
    var start = project ? project.periodStart : form.elements.periodStart.value;
    var end = project ? project.periodEnd : form.elements.periodEnd.value;
    var validDate = /^\d{4}-\d{2}-\d{2}$/;
    var unusual = validDate.test(value) && ((validDate.test(start) && value < start) ||
      (validDate.test(end) && value > oneYearAfter(end)));
    warning.textContent = unusual
      ? '期末財報準備日早於查核起始日，或晚於查核截止日超過一年，請確認年份。仍可照目前日期儲存。' : '';
    warning.hidden = !unusual;
  }

  function preparationWarningHtml() {
    return '<p class="form-notice" data-bind="preparation-date-warning" role="status" hidden></p>';
  }

  function metadataEditorHtml() {
    if (!metadataDraft) {
      return '<div class="panel__actions"><button type="button" class="btn btn--ghost" ' +
        'data-action="edit-project-metadata">修改案件資料</button></div>';
    }
    var values = metadataDraft.values;
    return '<form class="form" data-bind="project-update-form">' +
      '<h3>修改案件資料</h3>' +
      '<p>案件名稱與查核期間不能修改。已匯出的報告與工作底稿不會改寫；之後匯出時使用新資料。</p>' +
      formRow('entityName', '客戶名稱（選填）', 'text', false, values.entityName) +
      formRow('projectCode', '案件編號（選填）', 'text', false, values.projectCode) +
      formRow('lastPeriodStart', '期末財報準備日', 'date', false, values.lastPeriodStart) +
      '<p class="rule-card__sub">修改期末財報準備日後，用到這個日期的預篩選與篩選結果需要重新計算。</p>' +
      preparationWarningHtml() +
      '<div id="project-update-error" class="form-notice" data-bind="project-update-error" role="alert" tabindex="-1" hidden></div>' +
      '<div class="panel__actions"><button type="submit" class="btn">儲存案件資料</button>' +
      '<button type="button" class="btn btn--ghost" data-action="cancel-project-metadata">取消</button></div></form>';
  }

  function bindMetadataEditor(container, project) {
    var edit = container.querySelector('[data-action="edit-project-metadata"]');
    if (edit) { edit.addEventListener('click', function () {
      metadataDraft = { project: project, error: null, values: {
        entityName: project.entityName || '', projectCode: project.projectCode || '', lastPeriodStart: project.lastPeriodStart || ''
      } };
      Store.touch();
    }); }
    var cancel = container.querySelector('[data-action="cancel-project-metadata"]');
    if (cancel) { cancel.addEventListener('click', function () { metadataDraft = null; Store.touch(); }); }
    var form = container.querySelector('[data-bind="project-update-form"]');
    if (!form) { return; }
    var draft = metadataDraft;
    function isCurrent() { return Store.getState().project === project && metadataDraft === draft; }
    function readValues() {
      return { entityName: form.elements.entityName.value, projectCode: form.elements.projectCode.value,
        lastPeriodStart: form.elements.lastPeriodStart.value };
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
      var payload = { entityName: draft.values.entityName.trim(), projectCode: draft.values.projectCode.trim(),
        lastPeriodStart: draft.values.lastPeriodStart || null };
      Ui.run('儲存案件資料', function () {
        return global.JetApi.projectUpdate(payload).then(function (data) {
          if (!isCurrent()) { return; }
          metadataDraft = null;
          Store.updateProjectMetadata(data);
          Store.addMessage('案件資料已更新。既有匯出檔保持原樣；之後匯出會使用新資料。', 'info');
          (data.warnings || []).forEach(function (warning) { Store.addMessage(warning, 'warn'); });
        }).catch(function (error) {
          if (isCurrent()) { throw error; }
        });
      }, { onError: function (_, error) {
        if (!isCurrent()) { return; }
        draft.error = error;
        showCreateError(form, error, true);
      } });
    });
  }

  function documentDateReuseHtml(state) {
    var summary = state.lastRuns.validate && state.lastRuns.validate.documentDateReuse;
    var known = !(state.staleState && state.staleState.validation) && summary &&
      Number.isInteger(summary.documentNumberCount) && summary.documentNumberCount >= 0 &&
      Number.isInteger(summary.entryCount) && summary.entryCount >= 0;
    var text;
    if (!known) {
      text = '傳票號碼是否用於不同總帳入帳日尚未確認。請在第四步執行資料驗證後查看；仍可繼續目前作業。';
    } else if (summary.documentNumberCount === 0) {
      text = '目前有效總帳未發現同一傳票號碼出現在不同總帳入帳日。';
    } else {
      text = '有效總帳有 ' + summary.documentNumberCount.toLocaleString() + ' 個傳票號碼出現在不同總帳入帳日，' +
        '共影響 ' + summary.entryCount.toLocaleString() + ' 列分錄。請確認來源系統是否重複使用號碼；JET 不會因此更改傳票比對鍵。';
    }
    return '<p class="form-notice" data-bind="document-date-reuse" role="status">' + Ui.esc(text) + '</p>';
  }

  function render(container, state) {
    if (metadataDraft && metadataDraft.project !== state.project) { metadataDraft = null; }
    if (state.project) {
      container.innerHTML =
        '<div class="panel">' +
          '<h2 class="panel__title">建立案件</h2>' +
          '<dl class="kv-list">' +
            kv('案件名稱', state.project.projectId) +
            kv('案件編號', state.project.projectCode) +
            kv('客戶名稱', state.project.entityName) +
            kv('建案者', state.project.operatorId) +
            kv('查核期間', (state.project.periodStart || '—') + ' ～ ' + (state.project.periodEnd || '—')) +
            kv('期末財報準備日', state.project.lastPeriodStart || '—') +
            kv('資料儲存方式', providerLabel(state.project.databaseProvider)) +
          '</dl>' +
          documentDateReuseHtml(state) +
          metadataEditorHtml() +
          Ui.stepFooterHtml(state) +
        '</div>';

      Ui.bindStepFooter(container);
      bindMetadataEditor(container, state.project);
      return;
    }

    container.innerHTML =
      '<div class="panel">' +
        '<h2 class="panel__title">建立案件</h2>' +
        '<form class="form" data-bind="create-form">' +
          formRow('caseName', '案件名稱', 'text', true) +
          formRow('projectCode', '案件編號（選填）', 'text', false) +
          formRow('entityName', '客戶名稱（選填）', 'text', false) +
          operatorNoticeHtml(state) +
          formRow('periodStart', '查核起始日', 'date', true) +
          formRow('periodEnd', '查核截止日', 'date', true) +
          formRow('lastPeriodStart', '期末財報準備日', 'date', false) +
          preparationWarningHtml() +
          '<p class="rule-card__sub">查核期間決定本次分析的總帳範圍；期末財報準備日供「財報準備日起核准」條件使用，不會延長查核期間。</p>' +
          formSelect('databaseProvider', '資料儲存方式（建立後不可變更）', [
            { value: 'sqlite', text: 'SQLite（本機檔案）', selected: true },
            { value: 'duckdb', text: 'DuckDB（本機分析資料庫）' },
            { value: 'sqlServer', text: 'SQL Server（共用實例）', disabled: sqlServerUnavailable(state) }
          ], sqlServerUnavailable(state) ? SQL_SERVER_NOT_CONFIGURED_HINT : '') +
          '<div id="create-form-error" class="form-notice create-form__error" data-bind="create-error" role="alert" tabindex="-1" hidden></div>' +
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
        caseName: form.caseName.value.trim(),
        projectCode: form.projectCode.value.trim(),
        entityName: form.entityName.value.trim(),
        periodStart: form.periodStart.value,
        periodEnd: form.periodEnd.value,
        lastPeriodStart: form.lastPeriodStart.value || null,
        databaseProvider: form.databaseProvider.value
      };
      if (!payload.caseName) {
        showCreateError(form, { field: 'caseName', message: '案件名稱不可為空白，請輸入可辨識這個案件的名稱。' });
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
