(function (global) {
  'use strict';
  var Ui = global.JetUi, Store = global.JetStore;
  var model = null;
  function key(state) {
    return JSON.stringify([state.project.projectId, state.importState.gl && state.importState.gl.batchId,
      state.importState.tb && state.importState.tb.batchId,
      state.importState.accountMapping && state.importState.accountMapping.batchId,
      state.mapping.gl.committed, state.mapping.tb.committed]);
  }
  function current(state) {
    if (!model || model.key !== key(state)) {
      model = { key: key(state), rows: null, changes: Object.create(null), cursor: null, previous: [], nextCursor: null,
        search: '', selected: Object.create(null), anchor: null, category: '', loading: false, error: '', notice: '' };
    }
    return model;
  }
  function dirty(m) { return Object.keys(m.changes).length > 0; }
  function load(m) {
    if (m.loading) { return Promise.resolve(); }
    m.loading = true; m.error = ''; m.rows = null; m.nextCursor = null;
    m.selected = Object.create(null); m.anchor = null; Store.touch();
    return global.JetApi.queryAccountMappingPage({ cursor: m.cursor, pageSize: 100, search: m.search })
      .then(function (data) {
        if (model !== m) { return; }
        m.rows = data.rows; m.nextCursor = data.nextCursor;
      }).catch(function (error) {
        if (model === m) { m.error = error.message || '科目清單載入失敗，請重試。'; }
      }).finally(function () {
        if (model === m) { m.loading = false; Store.touch(); }
      });
  }
  function html(state, ready) {
    var m = current(state);
    if (!ready) { return '<p class="rule-card__gate">完成 GL 欄位配對後，即可設定科目分類。</p>'; }
    var categories = Ui.taxonomyTree(state);
    var rows = (m.rows || []).map(function (row, index) {
      var selected = Object.prototype.hasOwnProperty.call(m.changes, row.accountCode)
        ? m.changes[row.accountCode] : row.categoryId;
      var category = categories.find(function (c) { return c.categoryId === selected; });
      return '<tr' + (m.selected[row.accountCode] ? ' class="account-editor__row--selected"' : '') + '><td data-account-select-cell><input type="checkbox" data-account-select="' + index + '" data-focus-key="account-select-' + index +
        '" aria-label="選取 ' + Ui.esc(row.accountCode + ' ' + (row.accountName || '')) + '"' +
        (m.selected[row.accountCode] ? ' checked' : '') + '></td><td>' + Ui.esc(row.accountCode) +
        '</td><td>' + Ui.esc(row.accountName || '—') + '</td><td data-account-current="' + index +
        '" data-category-id="' + Ui.esc(selected || '') + '">' + Ui.esc(category ? category.label : '尚未配對') +
        (Object.prototype.hasOwnProperty.call(m.changes, row.accountCode) ? '<span class="account-editor__pending">待儲存</span>' : '') + '</td></tr>';
    }).join('');
    return '<section class="account-editor" data-bind="account-editor" aria-label="本案科目分類">' +
      '<form class="account-editor__search" data-account-search><label>搜尋科目編號或名稱' +
      '<input class="form__input" type="search" name="search" maxlength="400" value="' + Ui.esc(m.search) +
      '" data-focus-key="account-search"></label><button class="btn btn--ghost"' + (dirty(m) || m.loading ? ' disabled' : '') + '>搜尋</button></form>' +
      '<div class="account-editor__batch"><span role="status" data-account-selection-count>已選 ' + Object.keys(m.selected).length + ' 個科目</span>' +
      '<label><span class="visually-hidden">套用分類</span><select class="form__select" data-account-bulk-category data-focus-key="account-bulk-category">' +
        '<option value="">請選擇分類</option>' + categories.map(function (c) {
          return '<option value="' + Ui.esc(c.categoryId) + '"' + (m.category === c.categoryId ? ' selected' : '') + '>' +
            Ui.esc('　'.repeat(c.depth) + (c.depth ? '└ ' : '') + c.label) + '</option>';
        }).join('') + '</select></label><button type="button" class="btn btn--ghost" data-account-apply' +
        (!m.category || !Object.keys(m.selected).length || m.loading ? ' disabled' : '') + '>套用至已選科目</button></div>' +
      (m.error ? '<p class="form-notice" role="alert">' + Ui.esc(m.error) +
        ' <button class="btn btn--ghost btn--tiny" data-account-retry>重新載入</button></p>' : '') +
      (m.loading || !m.rows ? (m.error ? '' : '<p role="status">正在載入科目…</p>') :
        '<p class="account-editor__selection-hint">拖曳勾選欄可連選；Shift 點選可選取區間。</p>' +
        '<div class="account-editor__table" data-preserve-scroll="account-editor-table"><table class="preview-table"><thead><tr><th scope="col">' +
        '<label class="account-editor__select-all"><input type="checkbox" data-account-select-page aria-label="全選本頁科目"' +
        (!m.rows.length ? ' disabled' : '') +
        (m.rows.length && m.rows.every(function (row) { return m.selected[row.accountCode]; }) ? ' checked' : '') + '><span>本頁</span></label>' +
        '</th><th>科目編號</th><th>科目名稱</th><th>目前分類</th></tr></thead><tbody>' +
        (rows || '<tr><td colspan="4">沒有符合的科目。</td></tr>') + '</tbody></table></div>') +
      '<div class="account-editor__footer"><div class="import-card__actions">' +
        '<button class="btn" data-account-save' + (!dirty(m) || m.loading ? ' disabled' : '') + '>儲存科目分類</button>' +
        '<button class="btn btn--ghost" data-account-cancel' + (!dirty(m) || m.loading ? ' disabled' : '') + '>取消變更</button></div>' +
        '<div class="import-card__actions"><button class="btn btn--ghost" data-account-prev' +
        (!m.previous.length || dirty(m) || m.loading ? ' disabled' : '') + '>上一頁</button>' +
        '<span>第 ' + (m.previous.length + 1) + ' 頁</span><button class="btn btn--ghost" data-account-next' +
        (!m.nextCursor || dirty(m) || m.loading ? ' disabled' : '') + '>下一頁</button></div></div>' +
      '<p class="account-editor__notice" role="status">' + (dirty(m) ? '本頁有 ' + Object.keys(m.changes).length +
        ' 個科目待儲存。儲存或取消後即可換頁。' : Ui.esc(m.notice)) + '</p></section>';
  }
  // Selection is UI-only. During a drag, keep a reversible preview outside the model;
  // neither pointer movement nor checkbox changes rebuild the table or write categories.
  function bindAccountSelection(card, m) {
    var rows = m.rows || [];
    var boxes = Array.from(card.querySelectorAll('[data-account-select]'));
    var table = card.querySelector('.account-editor__table');
    var selectPage = card.querySelector('[data-account-select-page]');
    var count = card.querySelector('[data-account-selection-count]');
    var apply = card.querySelector('[data-account-apply]');
    var drag = null, frame = null, suppressClick = false;
    function sync(selection) {
      boxes.forEach(function (box) {
        box.checked = !!selection[rows[Number(box.getAttribute('data-account-select'))].accountCode];
        box.closest('tr').classList.toggle('account-editor__row--selected', box.checked);
      });
      var size = Object.keys(selection).length;
      if (count) { count.textContent = '已選 ' + size + ' 個科目'; }
      if (selectPage) {
        selectPage.checked = !!boxes.length && size === boxes.length;
        selectPage.indeterminate = size > 0 && size < boxes.length;
      }
      if (apply) { apply.disabled = !m.category || !size || m.loading; }
    }
    function range(base, from, to, checked) {
      var selected = Object.assign(Object.create(null), base);
      for (var i = Math.min(from, to); i <= Math.max(from, to); i++) {
        if (checked) { selected[rows[i].accountCode] = true; }
        else { delete selected[rows[i].accountCode]; }
      }
      return selected;
    }
    boxes.forEach(function (checkbox) {
      var shift = false;
      checkbox.addEventListener('click', function (event) { shift = event.shiftKey; });
      checkbox.addEventListener('change', function () {
        if (m.rows !== rows || m.loading) { return; }
        var index = Number(checkbox.getAttribute('data-account-select'));
        var from = shift && m.anchor !== null ? m.anchor : index;
        m.selected = range(m.selected, from, index, shift || checkbox.checked);
        if (!shift || m.anchor === null) { m.anchor = index; }
        shift = false; sync(m.selected);
      });
    });
    if (selectPage && m.rows) {
      selectPage.addEventListener('change', function () {
        if (m.rows !== rows || m.loading) { return; }
        m.selected = Object.create(null); m.anchor = null;
        if (selectPage.checked) { m.rows.forEach(function (row) { m.selected[row.accountCode] = true; }); }
        sync(m.selected);
      });
    }
    sync(m.selected);
    if (!table || !boxes.length) { return; }
    function finish(commit) {
      if (!drag) { return; }
      var finished = drag; drag = null;
      if (frame !== null) { global.cancelAnimationFrame(frame); frame = null; }
      if (commit && table.isConnected && m.rows === rows && !m.loading) { m.selected = finished.selection; m.anchor = finished.anchor; }
      table.classList.toggle('account-editor__table--selecting', false);
      sync(m.selected);
      if (table.hasPointerCapture(finished.id)) { table.releasePointerCapture(finished.id); }
    }
    function updateRange() {
      var index = 0;
      boxes.forEach(function (box, i) {
        if (drag.y >= box.closest('tr').getBoundingClientRect().top) { index = i; }
      });
      drag.selection = range(drag.base, drag.anchor, index, drag.checked);
      sync(drag.selection);
    }
    function scrollFrame() {
      frame = null;
      if (!drag) { return; }
      if (!table.isConnected || m.rows !== rows || m.loading) { finish(false); return; }
      var rect = table.getBoundingClientRect(), header = table.querySelector('thead').getBoundingClientRect();
      var top = Math.max(rect.top, header.bottom, 0), bottom = Math.min(rect.bottom, global.innerHeight || rect.bottom);
      var delta = drag.y < top + 24 ? -12 : drag.y > bottom - 24 ? 12 : 0;
      if (delta && drag.x >= rect.left && drag.x <= rect.right) {
        table.scrollTop = Math.max(0, Math.min(table.scrollTop + delta, table.scrollHeight - table.clientHeight));
        updateRange();
      }
      frame = global.requestAnimationFrame(scrollFrame);
    }
    table.addEventListener('pointerdown', function (event) {
      suppressClick = false;
      // Touch keeps native scrolling and checkbox taps; mouse/pen start in the selection gutter only.
      if (m.rows !== rows || m.loading || event.button !== 0 || event.isPrimary === false || event.pointerType === 'touch') { return; }
      var cell = event.target.closest('[data-account-select-cell]');
      if (!cell) { return; }
      var checkbox = cell.querySelector('[data-account-select]');
      var index = Number(checkbox.getAttribute('data-account-select'));
      var anchor = event.shiftKey && m.anchor !== null ? m.anchor : index;
      drag = { id: event.pointerId, anchor: anchor, base: m.selected, checked: event.shiftKey || !checkbox.checked,
        x: event.clientX, y: event.clientY };
      drag.selection = range(drag.base, anchor, index, drag.checked);
      suppressClick = true; event.preventDefault(); checkbox.focus({ preventScroll: true });
      table.setPointerCapture(event.pointerId);
      table.classList.toggle('account-editor__table--selecting', true);
      sync(drag.selection); frame = global.requestAnimationFrame(scrollFrame);
    });
    table.addEventListener('pointermove', function (event) {
      if (!drag || drag.id !== event.pointerId) { return; }
      if (!(event.buttons & 1)) { finish(false); return; }
      drag.x = event.clientX; drag.y = event.clientY; updateRange();
    });
    table.addEventListener('pointerup', function (event) { if (drag && event.pointerId === drag.id) { finish(true); } });
    table.addEventListener('pointercancel', function () { finish(false); });
    table.addEventListener('lostpointercapture', function () { finish(false); });
    table.addEventListener('keydown', function (event) { if (drag && event.key === 'Escape') { event.preventDefault(); finish(false); } });
    table.addEventListener('click', function (event) {
      if (suppressClick && event.detail > 0) { suppressClick = false; event.preventDefault(); event.stopPropagation(); }
    }, true);
  }
  function bind(container) {
    var card = container.querySelector('[data-bind="account-editor"]');
    if (!card) { return; }
    var m = current(Store.getState());
    bindAccountSelection(card, m);
    card.querySelector('[data-account-bulk-category]').addEventListener('change', function (event) {
      m.category = event.currentTarget.value; Store.touch();
    });
    card.querySelector('[data-account-apply]').addEventListener('click', function () {
      if (!m.category || m.loading || !Ui.taxonomyCategories(Store.getState()).some(function (c) { return c.categoryId === m.category; })) { return; }
      (m.rows || []).forEach(function (row) {
        if (!m.selected[row.accountCode]) { return; }
        if (m.category === row.categoryId) { delete m.changes[row.accountCode]; }
        else { m.changes[row.accountCode] = m.category; }
      });
      m.notice = ''; m.selected = Object.create(null); m.anchor = null; Store.touch();
    });
    card.querySelector('[data-account-save]').addEventListener('click', function () {
      if (!dirty(m)) { return; }
      Ui.run('儲存科目分類', function () {
        return global.JetApi.accountMappingSave({ changes: Object.keys(m.changes).map(function (code) {
          return { accountCode: code, categoryId: m.changes[code] };
        }) }).then(function (data) {
          if (model !== m || key(Store.getState()) !== m.key) { return data; }
          m.rows.forEach(function (row) { if (m.changes[row.accountCode]) { row.categoryId = m.changes[row.accountCode]; } });
          m.changes = Object.create(null); m.selected = Object.create(null); m.anchor = null; m.notice = '科目分類已儲存。';
          // setter 同步重繪前先對齊新批次，分類設定改名不丟棄尚未保存的選擇。
          m.key = key(Object.assign({}, Store.getState(), { importState:
            Object.assign({}, Store.getState().importState, { accountMapping: data }) }));
          Store.setAccountMappingState(data);
          Store.applyMutationEffects(data);
          return data;
        });
      });
    });
    card.querySelector('[data-account-cancel]').addEventListener('click', function () { m.changes = Object.create(null); m.selected = Object.create(null); m.anchor = null; m.notice = ''; Store.touch(); });
    card.querySelector('[data-account-prev]').addEventListener('click', function () {
      if (!dirty(m) && m.previous.length) { m.cursor = m.previous.pop(); load(m); }
    });
    card.querySelector('[data-account-next]').addEventListener('click', function () {
      if (!dirty(m) && m.nextCursor) { m.previous.push(m.cursor); m.cursor = m.nextCursor; load(m); }
    });
    card.querySelector('[data-account-search]').addEventListener('submit', function (event) {
      event.preventDefault(); if (dirty(m) || m.loading) { return; }
      m.search = event.currentTarget.querySelector('input').value.trim(); m.cursor = null; m.previous = []; load(m);
    });
    var retry = card.querySelector('[data-account-retry]');
    if (retry) { retry.addEventListener('click', function () { load(m); }); }
    if (!m.rows && !m.loading && !m.error) { load(m); }
  }
  Ui.accountMappingEditor = { html: html, bind: bind };
  Ui.registerWorkflowReset(function () { model = null; });
})(window);
