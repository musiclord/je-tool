/*
  Step 1：匯入資料（多來源匯入精靈）。
  一個 GL/TB 資料集 = 一個批次，可由多個檔案或多個工作表組成（guide §3.1.4）：
  選檔（host.selectFiles）→ 逐檔預覽（import.inspectFile：工作表清單／偵測編碼與分隔符）→
  確認後將所有來源以單一 action 匯入，同一交易整批 replace 或 append。
  前端零解析：欄名集合比對、編碼偵測、合併語意全部在後端。
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;
  var Ui = global.JetUi;

  var ENCODING_OPTIONS = ['utf-8', 'big5', 'utf-16'];
  var DELIMITER_OPTIONS = [
    { value: ',', label: '逗號 ,' },
    { value: '\t', label: 'Tab' },
    { value: ';', label: '分號 ;' },
    { value: '|', label: '直線 |' }
  ];

  // 精靈區域狀態：一次只開一個資料集的精靈。
  // pending item = { filePath, fileName, fileType, sheetName, include, encoding, delimiter, columnCount }
  var wizard = { kind: null, mode: null, pending: [] };
  var preparerDraft = null;

  // 重新匯入/加入來源完成的「卡片成功態」一次性旗標。{ kind, mode:'replace'|'append', at:ms } | null
  var justImported = null;
  var justImportedTimer = null;

  // 任務清單各列的就地展開狀態（純區域 UI，比照 wizard／justImported：不進 state.js、不持久化）。
  // key 對應四列：gl／tb／authorizedPreparer／calendar。點動作鈕切換，展開即在列下方嵌入既有卡片完整 UI。
  var expanded = { gl: false, tb: false, authorizedPreparer: false, calendar: false };

  function resetWizard() {
    preparerDraft = null;
    wizard = { kind: null, mode: null, pending: [] };
  }

  function resetExpanded() {
    expanded = { gl: false, tb: false, authorizedPreparer: false, calendar: false };
  }

  Ui.registerWorkflowReset(resetWizard);

  Ui.registerWorkflowReset(function () {
    justImported = null;
    if (justImportedTimer) { clearTimeout(justImportedTimer); justImportedTimer = null; }
    resetExpanded();
  });

  /* ---- 渲染 ----------------------------------------------------------------- */

  // 匯入步驟頂部：四列任務清單（GL／TB／授權編製人員清單／日期維度）。
  // 每列＝狀態符號＋名稱(＋必要/選用標)＋單一關鍵數字＋單一動作鈕；點動作鈕就地展開該列既有卡片完整 UI。
  // 四張卡的 render 與事件綁定一律沿用既有函式，僅改由任務清單承載入口——呈現重組，不是行為變更。
  function render(container, state) {
    if (!state.project) {
      container.innerHTML = Ui.noProjectPanel('匯入資料');
      Ui.bindNoProjectPanel(container);
      return;
    }

    var imp = state.importState;

    container.innerHTML =
      '<div class="panel">' +
        '<h2 class="panel__title">匯入資料</h2>' +
        '<p class="panel__hint panel__hint--wide">支援 Excel、CSV、文字檔及 Access，可合併欄位相同的資料。</p>' +
        '<div class="import-tasklist">' +
          datasetTask('gl', 'GL（總帳明細）', imp.gl) +
          datasetTask('tb', 'TB（試算表）', imp.tb) +
          authorizedPreparerTask(imp.authorizedPreparer) +
          calendarTask(imp.calendar) +
        '</div>' +
        Ui.stepFooterHtml(state) +
      '</div>';

    bindTaskToggles(container);
    bindDatasetCard(container, 'gl', 'GL');
    bindDatasetCard(container, 'tb', 'TB');
    bindAuthorizedPreparerCard(container);
    bindCalendarCard(container);
    Ui.bindStepFooter(container);
  }

  /* ---- 任務清單（呈現層：把四張卡收斂成四列，展開才嵌入完整卡片）------------- */

  // 任務列外框：狀態符號 + 名稱(+必要/選用標) + 單一關鍵數字 + 單一動作鈕。
  // opts.done 決定符號與名稱色（✓ 已備／○ 未匯入）；opts.actionPrimary＝主要下一步動作（動作鈕文字轉紅、外框不變）。
  // 展開（opts.open）時在列下方嵌入 cardHtml（既有卡片完整 UI，DOM 未改）。
  function taskItemHtml(key, opts, cardHtml) {
    var open = opts.open;
    var markClass = opts.done ? ' import-task__mark--done' : ' import-task__mark--todo';
    var mark = opts.done ? '✓' : '○';
    var nameClass = opts.done ? '' : ' import-task__name--todo';
    var tag = opts.required
      ? '<span class="import-task__tag import-task__tag--req">必要</span>'
      : '<span class="import-task__tag import-task__tag--opt">選用</span>';
    var actionClass = opts.actionPrimary ? ' import-task__action--primary' : '';

    return (
      '<div class="import-task' + (open ? ' import-task--open' : '') + '">' +
        '<div class="import-task__row">' +
          '<span class="import-task__mark' + markClass + '">' + mark + '</span>' +
          '<span class="import-task__name' + nameClass + '">' + opts.name + tag + '</span>' +
          '<span class="import-task__meta">' + opts.meta + '</span>' +
          '<button type="button" class="import-task__action' + actionClass + '" ' +
            'data-task-toggle="' + key + '" aria-expanded="' + (open ? 'true' : 'false') + '">' +
            opts.action + '</button>' +
        '</div>' +
        (open ? '<div class="import-task__panel">' + cardHtml + '</div>' : '') +
      '</div>'
    );
  }

  // GL／TB 列。關鍵數字一律讀既有 importState 欄位（rowCount／columns.length／sources.length，與 summaryFaceHtml 同源），
  // 前端不計算、不臆造；toLocaleString 只做千分位格式化，非計算。四類資料都由「匯入」展開操作。
  function datasetTask(kind, name, info) {
    var meta = info
      ? Number(info.rowCount).toLocaleString() + ' 列，' + info.columns.length + ' 欄，' +
          ((info.sources || []).length || 1) + ' 來源'
      : '未匯入';

    return taskItemHtml(kind, {
      open: expanded[kind],
      done: !!info,
      required: true,
      name: name,
      meta: meta,
      action: '匯入',
      actionPrimary: false
    }, datasetCard(kind, name, info));
  }

  // 授權編製人員清單列（選用）。列數讀 info.rowCount，前端不計算。
  function authorizedPreparerTask(info) {
    var meta = info
      ? Number(info.rowCount).toLocaleString() + ' 人'
      : '未匯入';

    return taskItemHtml('authorizedPreparer', {
      open: expanded.authorizedPreparer,
      done: !!info,
      required: false,
      name: '授權編製人員清單',
      meta: meta,
      action: '匯入',
      actionPrimary: !info
    }, authorizedPreparerCard(info));
  }

  // 日期維度列（選用）。完成狀態只鏡像後端持久 marker：成功匯入（即使零筆）
  // 或曾儲存每週非工作日設定；數量只負責摘要，不能反推工作流狀態。
  function calendarTask(calendar) {
    var completed = !!(calendar && (calendar.calendarImported || calendar.nonWorkingDaysConfigured));
    var parts = [];
    if (calendar && calendar.calendarImported) {
      parts.push('假日 ' + (calendar.holidayCount || 0));
      parts.push('補班 ' + (calendar.makeupDayCount || 0));
    }
    var nw = calendar && calendar.nonWorkingDaysConfigured
      ? nonWorkingSummary(calendar.nonWorkingDays)
      : '';
    if (nw) { parts.push(nw); }
    var meta = parts.length ? parts.join('，') : '尚未設定日期維度';

    return taskItemHtml('calendar', {
      open: expanded.calendar,
      done: completed,
      required: false,
      name: '日期維度',
      meta: meta,
      action: '匯入',
      actionPrimary: false
    }, calendarCard(calendar));
  }

  // 非工作日整數陣列 → 中文單字標籤（週一→週日排列），與 weekdayCheckboxesHtml 相同對照表；
  // 純顯示轉換；非陣列才視為無資料，明示空集合代表「整週皆工作日」。
  function nonWorkingSummary(nonWorkingDays) {
    if (!Array.isArray(nonWorkingDays)) { return ''; }
    if (!nonWorkingDays.length) { return '非工作日 無（整週皆工作日）'; }
    var order = [
      { v: 1, label: '一' }, { v: 2, label: '二' }, { v: 3, label: '三' },
      { v: 4, label: '四' }, { v: 5, label: '五' }, { v: 6, label: '六' }, { v: 0, label: '日' }
    ];
    var labels = order.filter(function (d) {
      return nonWorkingDays.indexOf(d.v) >= 0;
    }).map(function (d) { return d.label; });
    return labels.length ? '非工作日 ' + labels.join('、') : '';
  }

  // 就地展開／收合：切換該列的區域展開旗標並重繪。純呈現，不呼叫後端、不改任何資料。
  function bindTaskToggles(container) {
    container.querySelectorAll('[data-task-toggle]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var key = btn.getAttribute('data-task-toggle');
        expanded[key] = !expanded[key];
        Store.touch();
      });
    });
  }

  // 原檔預覽與有效授權集合分開呈現；識別值整理與比對只在後端執行。
  function authorizedPreparerCard(info) {
    var body = info
      ? '<p class="import-card__status import-card__status--ok">有效授權清單：' + info.rowCount + ' 個人員識別值' +
          (info.sourceColumn ? '，採用「' + Ui.esc(info.sourceColumn) + '」欄' : '') + '。</p>'
      : '<p class="import-card__status">尚未提供授權清單。沒有清單時不執行非授權編製人員比對。</p>';
    if (info && info.sourceRowCount != null) {
      body += '<p>原始資料 ' + info.sourceRowCount + ' 列，略過空白 ' + info.blankRowCount +
        ' 列、重複識別值 ' + info.duplicateRowCount + ' 列。</p>';
    }
    var draft = preparerDraft;
    var pending = '';
    if (draft) {
      pending = '<div class="pending-preview" data-bind="authorized-preparer-draft">' +
        '<p>來源檔：' + Ui.esc(draft.fileName) + '。請選與 GL「編製人員」使用相同值的欄位；' +
        'GL 使用員工代碼時，這裡也選代碼，不會自動將姓名轉成代碼。</p>' +
        '<label>工作表 <select data-ap-sheet>' + draft.worksheets.map(function (sheet) {
          return '<option value="' + Ui.esc(sheet.name) + '"' + (sheet.name === draft.sheetName ? ' selected' : '') +
            '>' + Ui.esc(sheet.name) + '</option>';
        }).join('') + '</select></label> ' +
        '<label>人員識別欄位 <select data-ap-column><option value="">請選擇欄位</option>' +
        draft.columns.map(function (column) {
          return '<option value="' + Ui.esc(column) + '"' + (column === draft.sourceColumn ? ' selected' : '') +
            '>' + Ui.esc(column) + '</option>';
        }).join('') + '</select></label>' +
        '<p>下方是原檔前 10 列。完成匯入後，授權清單只保留選定欄位去除空白及重複後的識別值。</p>' +
        (draft.previewError ? '<p role="alert">原檔預覽失敗：' + Ui.esc(draft.previewError) + '</p>' :
          previewTableHtml(draft)) +
        '<div class="import-card__actions"><button type="button" class="btn" data-action="commit-authorized-preparer"' +
        (draft.sourceColumn ? '' : ' disabled') + '>匯入選定欄位</button>' +
        '<button type="button" class="btn btn--ghost" data-action="retry-authorized-preparer-preview">重讀原檔預覽</button>' +
        '<button type="button" class="btn btn--ghost" data-action="cancel-authorized-preparer">取消</button></div></div>';
    }
    return '<section class="import-card" data-bind="import-card-authorized-preparer">' +
      '<h3 class="import-card__title">授權編製人員清單</h3>' + body +
      '<p>這是用於比對 GL 編製人員的授權名單；預篩選報告 R5 則彙總 GL 中實際出現的人員，兩者用途不同。</p>' +
      '<div class="import-card__actions"><button type="button" class="btn btn--ghost" data-action="import-authorized-preparer">' +
      (info ? '重新選擇授權清單檔' : '選擇授權清單檔') + '</button>' +
      (info ? '<button type="button" class="btn btn--ghost" data-action="preview-authorized-preparer">預覽有效授權清單</button>' +
        '<button type="button" class="btn btn--ghost" data-action="clear-authorized-preparer">移除授權清單</button>' : '') +
      '</div>' + pending + '</section>';
  }

  function readPreparerPreview(draft) {
    draft.previewData = null;
    draft.previewError = null;
    return global.JetApi.importPreviewFile({ filePath: draft.filePath, sheetName: draft.sheetName }).then(function (data) {
      if (preparerDraft !== draft) { return; }
      draft.previewData = data;
      Store.touch();
    }, function (error) {
      if (preparerDraft === draft) { draft.previewError = error.message; Store.touch(); }
      throw error;
    });
  }

  function bindAuthorizedPreparerCard(container) {
    function bind(action, handler) {
      var button = container.querySelector('[data-action="' + action + '"]');
      if (button) { button.addEventListener('click', handler); }
    }
    bind('preview-authorized-preparer', function () { Ui.openDataPreview('authorizedPreparers'); });
    bind('cancel-authorized-preparer', function () { preparerDraft = null; Store.touch(); });
    bind('clear-authorized-preparer', function () {
      Ui.run('移除授權清單', function () {
        return global.JetApi.importAuthorizedPreparerClear({}).then(function (result) {
          preparerDraft = null;
          Store.setAuthorizedPreparerState(null);
          Store.addMessage('已移除授權清單。原情境保留，依賴清單的結果會重新計算。', 'info');
          return result;
        });
      });
    });
    bind('retry-authorized-preparer-preview', function () {
      if (preparerDraft) { Ui.run('讀取授權清單原檔預覽', function () { return readPreparerPreview(preparerDraft); }); }
    });
    bind('commit-authorized-preparer', function () {
      var draft = preparerDraft;
      if (!draft || !draft.sourceColumn) { return; }
      Ui.run('匯入授權編製人員清單', function () {
        return global.JetApi.importAuthorizedPreparerFromFile({ filePath: draft.filePath, fileName: draft.fileName,
          sheetName: draft.sheetName, sourceColumn: draft.sourceColumn }).then(function (data) {
          if (preparerDraft === draft) { preparerDraft = null; }
          Store.setAuthorizedPreparerState(data);
          Store.addMessage('授權清單匯入完成：' + data.rowCount + ' 個有效人員識別值。', 'info');
          return data;
        });
      }, { logCompletion: true, logCompletionWhen: function (result) { return !!result; } });
    });
    bind('import-authorized-preparer', function () {
      var projectId = Store.getState().project.projectId;
      Ui.run('選擇授權編製人員清單', function () {
        return global.JetApi.hostSelectFile({ title: '授權編製人員清單', extensions: ['.xlsx'] }).then(function (file) {
          if (!file.filePath) { return; }
          return global.JetApi.importInspectFile({ filePath: file.filePath }).then(function (data) {
            if (!Store.getState().project || Store.getState().project.projectId !== projectId) { return; }
            var sheets = (data.worksheets || []).filter(function (sheet) { return sheet.columns.length > 0; });
            if (!sheets.length) { throw new Error('來源檔沒有可讀取的欄位，請確認工作表含有標頭。'); }
            var draft = { filePath: file.filePath, fileName: file.fileName, worksheets: sheets,
              sheetName: sheets[0].name, columns: sheets[0].columns, sourceColumn: '', previewData: null };
            preparerDraft = draft;
            Store.touch();
            return readPreparerPreview(draft);
          });
        });
      });
    });
    var column = container.querySelector('[data-ap-column]');
    if (column) { column.addEventListener('change', function () {
      preparerDraft.sourceColumn = column.value;
      var commit = container.querySelector('[data-action="commit-authorized-preparer"]');
      if (commit) { commit.disabled = !column.value; }
    }); }
    var sheet = container.querySelector('[data-ap-sheet]');
    if (sheet) { sheet.addEventListener('change', function () {
      var previous = preparerDraft;
      var selected = previous.worksheets.find(function (item) { return item.name === sheet.value; });
      var draft = Object.assign({}, previous, { sheetName: selected.name, columns: selected.columns,
        sourceColumn: '', previewData: null, previewError: null });
      preparerDraft = draft;
      Store.touch();
      Ui.run('讀取授權清單原檔預覽', function () { return readPreparerPreview(draft); });
    }); }
  }

  // 日期維度：上傳事務所假日／補班 .xlsx + 設定每週非工作日（週末判定）。前端零邏輯，權威在後端。
  function calendarCard(calendar) {
    var body = calendar && calendar.calendarImported
      ? '<p class="import-card__status import-card__status--ok">假日 ' +
          (calendar.holidayCount || 0) + ' 天、補班 ' + (calendar.makeupDayCount || 0) + ' 天。</p>'
      : calendar && calendar.nonWorkingDaysConfigured
        ? '<p class="import-card__status import-card__status--ok">已設定每週非工作日；尚未上傳假日／補班檔。</p>'
        : '<p class="import-card__status">尚未設定日期維度。可上傳事務所行事曆檔（.xlsx），或調整每週非工作日。</p>';

    return (
      '<section class="import-card" data-bind="import-card-calendar">' +
        '<h3 class="import-card__title">日期維度（假日／補班日）</h3>' +
        body +
        '<div class="import-card__actions">' +
          '<button type="button" class="btn btn--ghost" data-action="import-holiday">上傳假日檔</button>' +
          '<button type="button" class="btn btn--ghost" data-action="import-makeup">上傳補班檔</button>' +
        '</div>' +
        '<div class="calendar-nonworking">' +
          '<span class="calendar-nonworking__label">非工作日（週末判定）</span>' +
          '<div class="calendar-nonworking__days" data-bind="nonworking-days">' +
            weekdayCheckboxesHtml(calendar && calendar.nonWorkingDays) +
          '</div>' +
          '<p class="calendar-nonworking__hint">未調整時預設週六、週日。影響週末過帳／核准規則與週末篩選條件。</p>' +
        '</div>' +
      '</section>'
    );
  }

  // 一週七天的非工作日勾選；canonical .NET DayOfWeek（週日=0…週六=6），UI 以週一→週日排列。
  function weekdayCheckboxesHtml(nonWorkingDays) {
    var set = nonWorkingDays || [0, 6];
    var days = [
      { v: 1, label: '一' }, { v: 2, label: '二' }, { v: 3, label: '三' },
      { v: 4, label: '四' }, { v: 5, label: '五' }, { v: 6, label: '六' }, { v: 0, label: '日' }
    ];
    return days.map(function (d) {
      var checked = set.indexOf(d.v) >= 0 ? ' checked' : '';
      return '<label class="weekday-toggle">' +
        '<input type="checkbox" data-weekday="' + d.v + '"' + checked + '>' +
        '<span>' + d.label + '</span></label>';
    }).join('');
  }

  function sameNonWorkingDays(left, right) {
    var normalize = function (value) {
      var source = Array.isArray(value) ? value : [0, 6];
      return source.map(Number).filter(function (day, index, all) {
        return Number.isInteger(day) && day >= 0 && day <= 6 && all.indexOf(day) === index;
      }).sort(function (a, b) { return a - b; });
    };
    return JSON.stringify(normalize(left)) === JSON.stringify(normalize(right));
  }

  function bindCalendarCard(container) {
    var holidayBtn = container.querySelector('[data-action="import-holiday"]');
    if (holidayBtn) {
      holidayBtn.addEventListener('click', function () {
        Ui.run('上傳假日檔', function () {
          return global.JetApi.hostSelectFile({
            title: '選擇假日檔（Date_of_Holiday、Holiday_Name、IS_Holiday）',
            extensions: ['.xlsx']
          }).then(function (file) {
            if (!file.filePath) { return; }
            return global.JetApi.importHolidayFromFile({
              filePath: file.filePath,
              fileName: file.fileName
            }).then(function (data) {
              var existing = Store.getState().importState.calendar || {};
              Store.setCalendarState({
                holidayCount: data.count,
                makeupDayCount: existing.makeupDayCount || 0,
                calendarImported: true,
                nonWorkingDays: existing.nonWorkingDays,
                nonWorkingDaysConfigured: !!existing.nonWorkingDaysConfigured
              });
              Store.addMessage('假日匯入完成：' + data.count + ' 天。', 'info');
              return data;
            });
          });
        }, {
          logCompletion: true,
          logCompletionWhen: function (result) { return !!result; }
        });
      });
    }

    var makeupBtn = container.querySelector('[data-action="import-makeup"]');
    if (makeupBtn) {
      makeupBtn.addEventListener('click', function () {
        Ui.run('上傳補班檔', function () {
          return global.JetApi.hostSelectFile({
            title: '選擇補班檔（Date_of_MakeUpday、MakeUpDay_Desc）',
            extensions: ['.xlsx']
          }).then(function (file) {
            if (!file.filePath) { return; }
            return global.JetApi.importMakeupDayFromFile({
              filePath: file.filePath,
              fileName: file.fileName
            }).then(function (data) {
              var existing = Store.getState().importState.calendar || {};
              Store.setCalendarState({
                holidayCount: existing.holidayCount || 0,
                makeupDayCount: data.count,
                calendarImported: true,
                nonWorkingDays: existing.nonWorkingDays,
                nonWorkingDaysConfigured: !!existing.nonWorkingDaysConfigured
              });
              Store.addMessage('補班匯入完成：' + data.count + ' 天。', 'info');
              return data;
            });
          });
        }, {
          logCompletion: true,
          logCompletionWhen: function (result) { return !!result; }
        });
      });
    }

    // 非工作日（週幾）勾選：任一變更即蒐集勾選的 canonical 值送後端，回傳後更新狀態。
    container.querySelectorAll('[data-bind="nonworking-days"] input[data-weekday]').forEach(function (cb) {
      cb.addEventListener('change', function () {
        var days = [];
        container.querySelectorAll('[data-bind="nonworking-days"] input[data-weekday]').forEach(function (b) {
          if (b.checked) { days.push(Number(b.getAttribute('data-weekday'))); }
        });
        Ui.run('設定非工作日', function () {
          return global.JetApi.calendarSetNonWorkingDays({ days: days }).then(function (data) {
            var existing = Store.getState().importState.calendar || {};
            var changed = !sameNonWorkingDays(existing.nonWorkingDays, data.nonWorkingDays);
            if (changed) {
              Store.setCalendarState({
                holidayCount: existing.holidayCount || 0,
                makeupDayCount: existing.makeupDayCount || 0,
                calendarImported: !!existing.calendarImported,
                nonWorkingDays: data.nonWorkingDays,
                nonWorkingDaysConfigured: true
              });
            }
            Store.addMessage(changed ? '已更新非工作日設定。' : '非工作日設定未變更。', 'info');
          });
        });
      });
    });
  }

  // 卡片三狀態互斥：工作區啟動時不渲染摘要與入口鈕 → 永不三鈕同框。
  function datasetCard(kind, title, info) {
    var body;
    if (wizard.kind === kind) {
      body = wizardWorkspaceHtml(kind, info);
    } else if (info) {
      body = summaryFaceHtml(kind, info);
    } else {
      body = emptyFaceHtml(kind);
    }

    return (
      '<section class="import-card" data-bind="import-card-' + kind + '">' +
        '<h3 class="import-card__title">' + title + '</h3>' +
        body +
      '</section>'
    );
  }

  function summaryFaceHtml(kind, info) {
    var justBadge = '';
    if (justImported && justImported.kind === kind) {
      var label = justImported.mode === 'append' ? '已加入來源' : '剛剛重新匯入';
      justBadge = '<p class="import-card__just" data-bind="just-imported-' + kind + '">' +
        '<span class="import-card__just-mark" aria-hidden="true">✓</span> ' + label + '·' +
        new Date(justImported.at).toLocaleTimeString('zh-Hant', { hour12: false }) + '</p>';
    }

    return (
      justBadge +
      '<p class="import-card__status import-card__status--ok">已匯入 ' + info.rowCount + ' 列、' +
        info.columns.length + ' 欄（' + ((info.sources || []).length || 1) + ' 個來源）。</p>' +
      sourceListHtml(info.sources || []) +
      '<div class="import-card__actions">' +
        '<button type="button" class="btn btn--ghost" data-action="wizard-append-' + kind + '">加入來源</button>' +
        '<button type="button" class="btn btn--ghost" data-action="wizard-replace-' + kind + '">取代這份資料集</button>' +
      '</div>'
    );
  }

  function emptyFaceHtml(kind) {
    return (
      '<p class="import-card__status">尚未匯入。可合併欄位相同的檔案、工作表或資料表。</p>' +
      '<div class="import-card__actions">' +
        '<button type="button" class="btn" data-action="wizard-replace-' + kind + '">選擇來源檔</button>' +
      '</div>'
    );
  }

  function sourceListHtml(sources) {
    if (!sources.length) { return ''; }

    var rows = sources.map(function (s) {
      var name = Ui.esc(s.fileName) + (s.sheetName ? '<span class="source-list__sheet">' +
        Ui.esc(s.sheetName) + '</span>' : '');
      var detail = [];
      if (s.encoding) { detail.push('編碼 ' + Ui.esc(s.encoding)); }
      if (s.delimiter) { detail.push('分隔符 ' + Ui.esc(s.delimiter === '\t' ? 'Tab' : s.delimiter)); }

      return (
        '<tr>' +
          '<td class="source-list__no">' + s.sourceNo + '</td>' +
          '<td>' + name + (detail.length ? '<span class="source-list__detail">' + detail.join('，') + '</span>' : '') + '</td>' +
          '<td class="source-list__rows">' + Number(s.rowCount).toLocaleString() + '</td>' +
          '<td class="source-list__time">' + new Date(s.importedUtc).toLocaleString('zh-Hant', { hour12: false }) + '</td>' +
        '</tr>'
      );
    }).join('');

    return (
      '<table class="source-list">' +
        '<thead><tr><th>#</th><th>來源（檔案、工作表或資料表）</th><th>列數</th><th>匯入時間</th></tr></thead>' +
        '<tbody>' + rows + '</tbody>' +
      '</table>'
    );
  }

  // 模式橫幅：明講意圖與後果（append / 破壞性 replace / 首次建立）。
  // 匯入進行中改顯示中性「匯入中」：使用者已按下「開始匯入」，「即將取代」的預警已過時，
  // 不該在匯入期間（大檔可能數十秒）續掛破壞性警告，造成「完成後還掛著警告」的錯覺。
  function wizardBannerHtml(info) {
    if (wizard.importing) {
      return '<p class="wizard-pane__hint">匯入中…請稍候，完成後會顯示匯入結果。</p>';
    }
    if (wizard.mode === 'append') {
      return '<p class="wizard-pane__hint">加入來源：新檔的資料會附加到現有 ' +
        Number(info.rowCount).toLocaleString() + ' 列（欄位名稱需與現有一致）。成功加入後，這份資料集需要重新確認欄位配對，受影響的測試結果需要重算。已保存的篩選情境設定與既有底稿檔案會保留。</p>';
    }
    if (info) {
      return '<p class="wizard-pane__hint wizard-pane__hint--danger">你正在取代這個資料集。現有的 ' +
        Number(info.rowCount).toLocaleString() + ' 列會在匯入成功後被新來源取代。這份資料集需要重新確認欄位配對，受影響的測試結果需要重算。已保存的篩選情境設定與既有底稿檔案會保留。</p>';
    }
    return '<p class="wizard-pane__hint">建立資料集 — 選擇一個或多個來源檔，可合併工作表或資料表。</p>';
  }

  function wizardWorkspaceHtml(kind, info) {
    var hasFiles = wizard.pending.length > 0;
    var includedCount = wizard.pending.filter(function (i) { return i.include; }).length;
    var pickLabel = hasFiles ? '再加入檔案' : '選擇來源檔（可多選）';

    // 匯入進行中：只留橫幅與進度，收掉所有動作鈕（不能在匯入途中改選來源／取消）。
    var actions = wizard.importing ? '' : (
      '<div class="import-card__actions">' +
        '<button type="button" class="btn' + (hasFiles ? ' btn--ghost' : '') +
          '" data-action="wizard-pick">' + pickLabel + '</button>' +
        (hasFiles
          ? '<button type="button" class="btn" data-action="wizard-confirm"' +
              (includedCount === 0 ? ' disabled' : '') +
              '>開始匯入（' + includedCount + ' 個來源）</button>'
          : '') +
        '<button type="button" class="btn btn--ghost" data-action="wizard-cancel">取消</button>' +
      '</div>'
    );

    return (
      '<div class="wizard-pane">' +
        wizardBannerHtml(info) +
        (hasFiles ? pendingListHtml() : '') +
        '<div class="wizard-progress" data-bind="wizard-progress" hidden>' +
          '<p class="wizard-progress__label" data-bind="wizard-progress-label"></p>' +
          '<div class="wizard-progress__track"><div class="wizard-progress__fill" data-bind="wizard-progress-fill"></div></div>' +
        '</div>' +
        actions +
      '</div>'
    );
  }

  function pendingListHtml() {
    var rows = wizard.pending.map(function (item, index) {
      var name = Ui.esc(item.fileName) + (item.sheetName
        ? '<span class="source-list__sheet">' + Ui.esc(item.sheetName) + '</span>' : '');

      return (
        '<li class="pending-row">' +
          '<div class="pending-row__head">' +
            '<label class="pending-row__main">' +
              '<input type="checkbox" data-pending-bind="include" data-index="' + index + '"' +
                (item.include ? ' checked' : '') + '>' +
              '<span>' + name + '</span>' +
            '</label>' +
            pendingDetailHtml(item, index) +
            '<button type="button" class="btn btn--ghost btn--tiny" data-action="pending-preview" ' +
              'data-index="' + index + '">' + (item.previewOpen ? '預覽 ▾' : '預覽 ▸') + '</button>' +
            '<button type="button" class="btn btn--ghost btn--tiny" data-action="pending-remove" ' +
              'data-index="' + index + '" title="從清單移除這個來源">移除</button>' +
          '</div>' +
          (item.previewOpen ? previewTableHtml(item) : '') +
        '</li>'
      );
    }).join('');

    return '<ul class="wizard-pane__list">' + rows + '</ul>';
  }

  function pendingDetailHtml(item, index) {
    if (item.fileType === 'csv') {
      var encodingOptions = ENCODING_OPTIONS.map(function (enc) {
        return '<option value="' + enc + '"' + (item.encoding === enc ? ' selected' : '') + '>' + enc + '</option>';
      }).join('');

      var delimiterOptions = '<option value=""' + (item.delimiter ? '' : ' selected') + '>自動</option>' +
        DELIMITER_OPTIONS.map(function (d) {
          return '<option value="' + (d.value === '\t' ? '&#9;' : d.value) + '"' +
            (item.delimiter === d.value ? ' selected' : '') + '>' + d.label + '</option>';
        }).join('');

      return (
        '<span class="pending-row__detail">' +
          '<label>編碼 <select data-pending-bind="encoding" data-index="' + index + '">' +
            encodingOptions + '</select></label>' +
          '<label>分隔符 <select data-pending-bind="delimiter" data-index="' + index + '">' +
            delimiterOptions + '</select></label>' +
        '</span>'
      );
    }

    var estimate = item.rowCountEstimate != null
      ? '，約 ' + Number(item.rowCountEstimate).toLocaleString() + ' 列' : '';
    return '<span class="pending-row__detail">' + item.columnCount + ' 欄' + estimate + '</span>';
  }

  // 逐來源預覽：表頭 + 前 10 列原貌（import.previewFile）。有界，絕不載入完整母體。
  function previewTableHtml(item) {
    if (!item.previewData) {
      return '<div class="pending-preview"><p class="pending-preview__hint">載入預覽中…</p></div>';
    }

    var data = item.previewData;
    var head = '<tr>' + data.columns.map(function (c) {
      return '<th>' + Ui.esc(c) + '</th>';
    }).join('') + '</tr>';

    var body = data.sampleRows.map(function (row) {
      return '<tr>' + row.map(function (cell) {
        return cell === null ? '<td class="is-null">—</td>' : '<td>' + Ui.esc(cell) + '</td>';
      }).join('') + '</tr>';
    }).join('');

    return (
      '<div class="pending-preview">' +
        '<p class="pending-preview__hint">最上方一列是被當成欄名的標頭；若它看起來是資料而非欄名，' +
          '代表這份檔案可能沒有標頭列。</p>' +
        '<div class="pending-preview__grid">' +
          '<table class="data-preview__table"><thead>' + head + '</thead><tbody>' + body + '</tbody></table>' +
        '</div>' +
      '</div>'
    );
  }

  /* ---- 匯入進度（import.progress 事件，manifest「Host→Web 事件」） ---------------- */

  // 進度是 UX 提示：百分比以 inspect 的 rowCountEstimate 估算（dimension 推估、可能過時），
  // 權威列數以匯入 response 為準。estimate 缺席（CSV）→ 只顯示已寫入列數。
  function updateProgress(item, itemIndex, totalItems, data) {
    var pane = document.querySelector('[data-bind="wizard-progress"]');
    var label = document.querySelector('[data-bind="wizard-progress-label"]');
    var fill = document.querySelector('[data-bind="wizard-progress-fill"]');
    if (!pane || !label || !fill) { return; }

    pane.hidden = false;

    var sourceName = data.fileName + (data.sheetName ? '［' + data.sheetName + '］' : '');
    var countText = Number(data.rowsRead).toLocaleString() + ' 列';
    var position = totalItems > 1 ? '來源 ' + (itemIndex + 1) + '／' + totalItems + '：' : '';

    if (item && item.rowCountEstimate) {
      var percent = Math.min(99, Math.round(data.rowsRead / item.rowCountEstimate * 100));
      label.textContent = position + sourceName + '　已寫入 ' + countText + '（約 ' + percent + '%）';
      fill.style.width = percent + '%';
    } else {
      label.textContent = position + sourceName + '　已寫入 ' + countText;
      fill.style.width = '100%';
      fill.classList.add('wizard-progress__fill--indeterminate');
    }
  }

  /* ---- 事件 ------------------------------------------------------------------ */

  function bindDatasetCard(container, kind, label) {
    var card = container.querySelector('[data-bind="import-card-' + kind + '"]');
    if (!card) { return; }

    // 卡片成功態:約 4 秒後淡出並收回常態(一次性,避免每次重繪堆疊計時器)。
    if (justImported && justImported.kind === kind && !justImportedTimer) {
      justImportedTimer = setTimeout(function () {
        var badge = document.querySelector('[data-bind="just-imported-' + kind + '"]');
        if (badge) { badge.classList.add('is-fading'); }
        setTimeout(function () {
          justImported = null;
          justImportedTimer = null;
          Store.touch();
        }, 400);
      }, 4000);
    }

    // 摘要面 / 空狀態入口（工作區未啟動時才存在）
    var appendBtn = card.querySelector('[data-action="wizard-append-' + kind + '"]');
    if (appendBtn) {
      appendBtn.addEventListener('click', function () { openWorkspace(kind, label, 'append'); });
    }

    var replaceBtn = card.querySelector('[data-action="wizard-replace-' + kind + '"]');
    if (replaceBtn) {
      replaceBtn.addEventListener('click', function () { openWorkspace(kind, label, 'replace'); });
    }

    if (wizard.kind !== kind) { return; }

    // 工作區
    var pickBtn = card.querySelector('[data-action="wizard-pick"]');
    if (pickBtn) {
      pickBtn.addEventListener('click', function () { pickSources(kind, label); });
    }

    var confirmBtn = card.querySelector('[data-action="wizard-confirm"]');
    if (confirmBtn) {
      confirmBtn.addEventListener('click', function () { confirmWizard(kind, label); });
    }

    var cancelBtn = card.querySelector('[data-action="wizard-cancel"]');
    if (cancelBtn) {
      cancelBtn.addEventListener('click', function () { resetWizard(); Store.touch(); });
    }

    card.querySelectorAll('[data-action="pending-preview"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        togglePreview(kind, label, Number(btn.getAttribute('data-index')));
      });
    });

    // 單列移除：誤加的來源可直接從待匯入清單拿掉，不必整個精靈取消。splice 後重繪即重新編號。
    card.querySelectorAll('[data-action="pending-remove"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        wizard.pending.splice(Number(btn.getAttribute('data-index')), 1);
        Store.touch();
      });
    });

    card.querySelectorAll('[data-pending-bind]').forEach(function (control) {
      var index = Number(control.getAttribute('data-index'));
      var key = control.getAttribute('data-pending-bind');

      control.addEventListener('change', function () {
        if (key === 'include') {
          wizard.pending[index].include = control.checked;
          Store.touch(); // 重建以更新「開始匯入（n）」計數
          return;
        }
        wizard.pending[index][key] = control.value || null;
        wizard.pending[index].previewData = null; // 編碼/分隔變更 → 預覽快取失效
        if (wizard.pending[index].previewOpen) {
          wizard.pending[index].previewOpen = false;
          togglePreview(kind, label, index); // 重抓
        } else {
          Store.touch();
        }
      });
    });
  }

  // 入口：開啟工作區（設定 kind/mode、清空待匯入清單）。
  // 首次建立（空資料集、無破壞性疑慮）直接開檔，少一次點擊；
  // 已有資料時不自動開檔——讓使用者先看到模式橫幅、可在開檔前反悔（尤其破壞性的重新匯入）。
  function openWorkspace(kind, label, mode) {
    var hasData = !!Store.getState().importState[kind];
    wizard = { kind: kind, mode: mode, pending: [] };
    Store.touch();
    if (mode === 'replace' && !hasData) {
      pickSources(kind, label);
    }
  }

  // 選檔 → 逐檔 inspect → 附加到工作區待匯入清單（Open XML 活頁簿的每個非空工作表各是一個來源）。
  function pickSources(kind, label) {
    Ui.run('選擇來源檔', function () {
      return global.JetApi.hostSelectFiles({
        title: '選擇 ' + label + ' 來源檔（可多選）',
        extensions: ['.xlsx', '.xlsm', '.xls', '.csv', '.txt', '.mdb', '.accdb']
      }).then(function (data) {
        var files = data.files || [];
        if (files.length === 0) { return; }

        return files.reduce(function (chain, file) {
          return chain.then(function () {
            return global.JetApi.importInspectFile({ filePath: file.filePath }).then(function (info) {
              if (Array.isArray(info.worksheets)) {
                (info.worksheets || []).forEach(function (ws) {
                  if (ws.columns.length === 0) { return; } // 空工作表不列入
                  wizard.pending.push({
                    filePath: file.filePath,
                    fileName: file.fileName,
                    fileType: info.fileType,
                    sheetName: ws.name,
                    include: true,
                    encoding: null,
                    delimiter: null,
                    columnCount: ws.columns.length,
                    rowCountEstimate: ws.rowCountEstimate != null ? ws.rowCountEstimate : null,
                    previewOpen: false,
                    previewData: null
                  });
                });
                return;
              }

              wizard.pending.push({
                filePath: file.filePath,
                fileName: file.fileName,
                fileType: 'csv',
                sheetName: null,
                include: true,
                encoding: info.encoding,
                delimiter: info.delimiter,
                columnCount: (info.columns || []).length,
                previewOpen: false,
                previewData: null
              });
            });
          });
        }, Promise.resolve()).then(function () {
          if (wizard.pending.length === 0) {
            Store.addMessage('選取的檔案沒有含欄位的工作表或資料表。', 'warn');
          }
          Store.touch();
        });
      });
    });
  }

  // 展開/收合單一來源的「表頭 + 前 10 列」預覽；有界（limit 10），絕不載入完整母體。
  function togglePreview(kind, label, index) {
    var item = wizard.pending[index];
    if (!item) { return; }

    if (item.previewOpen) {
      item.previewOpen = false;
      Store.touch();
      return;
    }

    item.previewOpen = true;
    if (item.previewData) { Store.touch(); return; } // 已快取，直接顯示

    Store.touch(); // 先顯示「載入預覽中…」

    Ui.run('載入預覽', function () {
      var payload = { filePath: item.filePath, limit: 10 };
      if (item.sheetName) { payload.sheetName = item.sheetName; }
      if (item.encoding) { payload.encoding = item.encoding; }
      if (item.delimiter) { payload.delimiter = item.delimiter; }

      return global.JetApi.importPreviewFile(payload).then(function (data) {
        item.previewData = { columns: data.columns || [], sampleRows: data.sampleRows || [] };
        Store.touch();
      }).catch(function (error) {
        item.previewOpen = false; // 失敗則收合，避免卡在「載入中」
        item.previewData = null;
        Store.touch();
        throw error; // 交給 run() 顯示錯誤訊息
      });
    });
  }

  // 勾選來源以單一 action 整批送出；root mode 套用整批，任一來源失敗由後端 transaction 全部 rollback。
  // 失敗時保留完整待匯入清單，讓使用者修正後直接重試同一批。
  function confirmWizard(kind, label) {
    var items = wizard.pending.filter(function (i) { return i.include; });
    if (items.length === 0) { return; }

    var invoke = kind === 'gl' ? global.JetApi.importGlFromFile : global.JetApi.importTbFromFile;
    var sources = items.map(function (item) {
      var source = {
        filePath: item.filePath,
        fileName: item.fileName
      };
      if (item.sheetName) { source.sheetName = item.sheetName; }
      if (item.encoding) { source.encoding = item.encoding; }
      if (item.delimiter) { source.delimiter = item.delimiter; }
      return source;
    });

    // 匯入期間訂閱 import.progress；結束（成功或失敗）一律解除
    var onProgress = function (data) {
      if (data.kind !== kind) { return; }
      var itemIndex = Math.max(0, Math.min(items.length - 1, Number(data.sourceNo || 1) - 1));
      updateProgress(items[itemIndex], itemIndex, Number(data.sourceCount) || items.length, data);
    };
    global.JetApi.on('import.progress', onProgress);
    var unsubscribe = function () { global.JetApi.off('import.progress', onProgress); };

    var startedAt = Date.now();

    // 進入匯入態：Ui.run 內 setBusy(true) 會立即觸發重繪，橫幅換成「匯入中」、動作鈕收起。
    wizard.importing = true;

    Ui.run('匯入 ' + label, function () {
      return invoke({ mode: wizard.mode, sources: sources }).then(function (data) {
        unsubscribe();
        justImported = { kind: kind, mode: wizard.mode, at: Date.now() };
        applyResponse(kind, data);
        resetWizard();
        Store.addMessage(
          label + ' 匯入完成：' + items.length + ' 個來源、本次新增 ' +
          Number(data.addedRowCount).toLocaleString() + ' 列、批次共 ' +
          Number(data.rowCount).toLocaleString() + ' 列、' +
          data.columns.length + ' 欄，耗時 ' +
          ((Date.now() - startedAt) / 1000).toFixed(1) + ' 秒。', 'info');
      }).catch(function (error) {
        unsubscribe();
        wizard.importing = false; // 退出匯入態，橫幅恢復預警、動作鈕回來供修正後重試
        Store.touch();
        throw error; // 交給 run() 顯示錯誤訊息（如 column_mismatch 的差集說明）
      });
    });
  }

  function applyResponse(kind, data) {
    var sources = data.sources || [];
    Store.setImportResult(kind, {
      batchId: data.batchId,
      rowCount: data.rowCount,
      columns: data.columns,
      fileName: sources.length ? sources[0].fileName : '',
      sources: sources
    });
  }

  Ui.registerStep('import', render);
})(window);
