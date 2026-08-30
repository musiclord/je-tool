/*
  Step 0：建立案件。
  渲染 metadata 表單與已建立摘要。
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;
  var Ui = global.JetUi;

  function formRow(name, label, type, required) {
    return (
      '<label class="form__row">' +
        '<span class="form__label">' + label + (required ? ' <em class="form__req">*</em>' : '') + '</span>' +
        '<input class="form__input" type="' + type + '" name="' + name + '"' + (required ? ' required' : '') + '>' +
      '</label>'
    );
  }

  function formSelect(name, label, options) {
    var opts = options.map(function (option) {
      return (
        '<option value="' + option.value + '"' + (option.selected ? ' selected' : '') + '>' +
          Ui.esc(option.text) +
        '</option>'
      );
    }).join('');
    return (
      '<label class="form__row">' +
        '<span class="form__label">' + label + '</span>' +
        '<select class="form__input" name="' + name + '">' + opts + '</select>' +
      '</label>'
    );
  }

  // databaseProvider 顯示名（與後端 ProjectDocument 的值對應）。
  function providerLabel(value) {
    if (value === 'sqlServer') { return 'SQL Server（共用實例）'; }
    if (value === 'duckdb') { return 'DuckDB（本地・分析型）'; }
    return 'SQLite（本機檔案）';
  }

  function kv(label, value) {
    return (
      '<div class="kv-list__row">' +
        '<dt class="kv-list__key">' + Ui.esc(label) + '</dt>' +
        '<dd class="kv-list__value">' + Ui.esc(value) + '</dd>' +
      '</div>'
    );
  }

  function clearCreateError(form) {
    var summary = form.querySelector('[data-bind="create-error"]');
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

  function showCreateError(form, error) {
    var summary = form.querySelector('[data-bind="create-error"]');
    if (!summary) { return; }
    var message = error && error.message ? error.message : '無法建立案件。';
    summary.textContent = message;
    summary.hidden = false;

    // field 只能來自後端結構化契約；不從 invalid_payload 或自由文字猜測。
    var target = error && error.field === 'caseName'
      ? form.elements.caseName
      : summary;
    if (target !== summary) {
      target.setAttribute('aria-invalid', 'true');
      target.setAttribute('aria-describedby', 'create-form-error');
    }

    if (global.JetFocus) {
      global.JetFocus.defer(target);
    } else {
      global.setTimeout(function () { target.focus(); }, 0);
    }
  }

  function render(container, state) {
    if (state.project) {
      container.innerHTML =
        '<div class="panel">' +
          '<h2 class="panel__title">建立案件</h2>' +
          '<dl class="kv-list">' +
            kv('案件名稱', state.project.projectId) +
            kv('案件編號', state.project.projectCode) +
            kv('客戶名稱', state.project.entityName) +
            kv('操作人員', state.project.operatorId) +
            kv('查核期間', (state.project.periodStart || '—') + ' ～ ' + (state.project.periodEnd || '—')) +
            kv('期末財報準備日', state.project.lastPeriodStart || '—') +
            kv('資料儲存方式', providerLabel(state.project.databaseProvider)) +
          '</dl>' +
          Ui.stepFooterHtml(state) +
        '</div>';

      Ui.bindStepFooter(container);
      return;
    }

    container.innerHTML =
      '<div class="panel">' +
        '<h2 class="panel__title">建立案件</h2>' +
        '<p class="panel__hint">輸入案件基本資料並建立查核案件；案件資料會保存在本機，可隨時關閉後續作。</p>' +
        '<form class="form" data-bind="create-form">' +
          formRow('caseName', '案件名稱', 'text', true) +
          formRow('projectCode', '案件編號', 'text', true) +
          formRow('entityName', '客戶名稱', 'text', true) +
          formRow('operatorId', '操作人員編號', 'text', true) +
          formRow('periodStart', '查核起始日', 'date', true) +
          formRow('periodEnd', '查核截止日', 'date', true) +
          formRow('lastPeriodStart', '期末財報準備起始日', 'date', false) +
          formSelect('databaseProvider', '資料儲存方式（建立後不可變更）', [
            { value: 'sqlite', text: 'SQLite（本機檔案）', selected: true },
            { value: 'duckdb', text: 'DuckDB（本地・分析型）' },
            { value: 'sqlServer', text: 'SQL Server（共用實例）' }
          ]) +
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
        operatorId: form.operatorId.value.trim(),
        periodStart: form.periodStart.value,
        periodEnd: form.periodEnd.value,
        lastPeriodStart: form.lastPeriodStart.value || null,
        databaseProvider: form.databaseProvider.value
      };

      Ui.run('建立案件', function () {
        return global.JetApi.projectCreate(payload).then(function (data) {
          Store.addMessage('案件「' + data.projectId + '」已建立。', 'info');
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
      }
    });
  }

  Ui.registerStep('create', render);
})(window);
